using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;
using System;               // for Console.WriteLine
using System.Linq;          // keep handy (Distinct, etc.)

namespace DripRunBackend.Controllers
{
    [Route("api/query")]
    [ApiController]
    [Authorize]
    public class QueryController : ControllerBase
    {
        private readonly string _connectionString;

        public QueryController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        private int? GetUserIdFromToken()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            return userIdClaim != null && int.TryParse(userIdClaim.Value, out var userId) ? userId : (int?)null;
        }

        [HttpGet("locations-logos")]
        public async Task<IActionResult> GetLocationsLogos()
        {
            int? userId = GetUserIdFromToken();
            if (userId == null)
                return Unauthorized();

            var locations = new List<object>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(@"
            SELECT 
                l.locationid,
                l.latitude,
                l.longitude,
                c.logoimgname,
                m.description
            FROM location l
            INNER JOIN coupons c ON l.couponid = c.couponid
            LEFT JOIN missions m ON m.missionid = c.missionid
            WHERE
            -- exclude coupons the user already owns
            NOT EXISTS (
                SELECT 1
                FROM user_coupon uc
                WHERE uc.userid = @UserId
                  AND uc.couponid = c.couponid
            )
            -- mission gating:
            -- allow coupons with no mission OR an Active mission
            AND (c.missionid IS NULL OR m.status = 'Active');
        ", connection);

            command.Parameters.AddWithValue("UserId", userId.Value);

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                string hint = reader.IsDBNull(4) ? null : reader.GetString(4);

                locations.Add(new
                {
                    LocationId = reader.GetInt32(0),
                    Latitude = reader.GetDouble(1),
                    Longitude = reader.GetDouble(2),
                    LogoImage = reader.GetString(3),
                    CoupHint = hint
                });
            }

            return Ok(locations);
        }


        [HttpGet("ARquery-V3")]
        public async Task<IActionResult> ARqueryV3([FromQuery] int locationId)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // --------------------------------------------------
            // STEP 1: Check ownership (treat locationId as couponId here)
            // --------------------------------------------------
            bool hasCoupon;

            await using (var ownsCmd = new NpgsqlCommand(@"
            SELECT EXISTS (
            SELECT 1 FROM user_coupon
            WHERE userid = @uid AND couponid = @cid
                )
            ", connection))
            {
                ownsCmd.Parameters.AddWithValue("@uid", userId);
                ownsCmd.Parameters.AddWithValue("@cid", locationId);

                hasCoupon = (bool)(await ownsCmd.ExecuteScalarAsync());
            }

            // --------------------------------------------------
            // STEP 2A: If already owned → skip EVERYTHING else
            // --------------------------------------------------
            if (hasCoupon)
            {
                await using var cmd = new NpgsqlCommand(@"
            SELECT
                c.couponid,
                c.modelimgname,
                m.imgname AS missionimgname,
                c.coupontype,
                c.companyid,
                c.shopifylink,
                c.couponlevel
            FROM coupons c
            LEFT JOIN missions m ON m.missionid = c.missionid
            WHERE c.couponid = @cid
        ", connection);

                cmd.Parameters.AddWithValue("@cid", locationId);

                await using var reader = await cmd.ExecuteReaderAsync();

                if (!await reader.ReadAsync())
                    return NotFound(new { error = "Coupon not found." });

                var couponId = Convert.ToInt32(reader["couponid"]);
                var modelImgName = reader["modelimgname"] as string;
                var missionImgName = reader["missionimgname"] as string;
                var couponType = reader["coupontype"] as string;
                var shopifyLink = reader["shopifylink"] as string;
                var couponLevel = reader["couponlevel"] == DBNull.Value
                    ? (int?)null
                    : Convert.ToInt32(reader["couponlevel"]);

                // Optional: log impression
                if (reader["companyid"] != DBNull.Value)
                {
                    int companyId = Convert.ToInt32(reader["companyid"]);
                    await DripRunBackend.Services.ImpressionLogger
                        .LogCouponAsync(connection, userId, companyId, couponId);
                }

                return Ok(new
                {
                    couponId,
                    modelImgName,
                    missionImgName,
                    couponType,
                    shopifyLink,
                    couponLevel
                });
            }

            // --------------------------------------------------
            // STEP 2B: NOT owned → run full original query
            // --------------------------------------------------
            await using var fullCmd = new NpgsqlCommand(@"
            WITH target AS (
              SELECT
                c.couponid        AS target_couponid,
                c.modelimgname    AS target_modelimgname,
                m.imgname         AS target_missionimgname,
                c.coupontype      AS target_coupontype,
                c.companyid       AS target_companyid,
                c.shopifylink     AS target_shopifylink,
                c.couponlevel     AS target_couponlevel,
                l.latitude        AS target_lat,
                l.longitude       AS target_lon
              FROM location l
              JOIN coupons c ON c.couponid = l.couponid
              LEFT JOIN missions m ON m.missionid = c.missionid
              WHERE l.locationid = @locationId
            ),
            prev AS (
              SELECT uc.couponid AS prev_couponid,
                     uc.claimedat AS prev_claimedat
              FROM user_coupon uc
              WHERE uc.userid = @uid
                AND uc.claimedviadaily = false
                AND uc.claimedat IS NOT NULL
              ORDER BY uc.claimedat DESC
              LIMIT 1
            ),
            prev_closest AS (
              SELECT
                l.locationid AS prev_locationid,
                l.latitude   AS prev_lat,
                l.longitude  AS prev_lon,
                2 * 6371000 * asin(
                  sqrt(
                    pow(sin(radians((t.target_lat - l.latitude) / 2.0)), 2) +
                    cos(radians(l.latitude)) * cos(radians(t.target_lat)) *
                    pow(sin(radians((t.target_lon - l.longitude) / 2.0)), 2)
                  )
                ) AS distance_m
              FROM location l
              CROSS JOIN target t
              JOIN prev p ON p.prev_couponid = l.couponid
              ORDER BY distance_m ASC
              LIMIT 1
            ),
            calc AS (
              SELECT
                p.prev_couponid,
                p.prev_claimedat,
                pc.prev_locationid,
                pc.distance_m,
                EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) AS elapsed_s,
                CASE
                  WHEN EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) > 0
                  THEN pc.distance_m / EXTRACT(EPOCH FROM (now() - p.prev_claimedat))
                END AS speed_mps
              FROM prev p
              LEFT JOIN prev_closest pc ON TRUE
            )
            SELECT
              t.target_couponid,
              t.target_modelimgname,
              t.target_missionimgname,
              t.target_coupontype,
              t.target_companyid,
              t.target_shopifylink,
              t.target_couponlevel,
              c.speed_mps
            FROM target t
            LEFT JOIN calc c ON TRUE;
            ", connection);

            fullCmd.Parameters.AddWithValue("@locationId", locationId);
            fullCmd.Parameters.AddWithValue("@uid", userId);

            int? f_couponId = null;
            string f_modelImgName = null;
            string f_missionImgName = null;
            string f_couponType = null;
            int? f_companyId = null;
            string f_shopifyLink = null;
            int? f_couponLevel = null;
            double? speedMps = null;

            await using (var reader = await fullCmd.ExecuteReaderAsync())
            {
                if (!await reader.ReadAsync())
                    return NotFound(new { error = "Location not found." });

                f_couponId = reader["target_couponid"] as int?;
                f_modelImgName = reader["target_modelimgname"] as string;
                f_missionImgName = reader["target_missionimgname"] as string;
                f_couponType = reader["target_coupontype"] as string;
                f_shopifyLink = reader["target_shopifylink"] as string;

                if (reader["target_companyid"] != DBNull.Value)
                    f_companyId = Convert.ToInt32(reader["target_companyid"]);

                if (reader["target_couponlevel"] != DBNull.Value)
                    f_couponLevel = Convert.ToInt32(reader["target_couponlevel"]);

                speedMps = reader["speed_mps"] == DBNull.Value
                    ? (double?)null
                    : Convert.ToDouble(reader["speed_mps"]);
            }

            // Log impression
            if (f_couponId.HasValue && f_companyId.HasValue)
            {
                await DripRunBackend.Services.ImpressionLogger
                    .LogCouponAsync(connection, userId, f_companyId.Value, f_couponId.Value);
            }

            double? speedKph = speedMps.HasValue ? speedMps.Value * 3.6 : null;
            bool suspicious = speedKph.HasValue && speedKph.Value > 200;

            if (suspicious)
            {
                return StatusCode(403, new
                {
                    rejected = true,
                    reason = "Unrealistic travel speed detected"
                });
            }

            return Ok(new
            {
                couponId = f_couponId,
                modelImgName = f_modelImgName,
                missionImgName = f_missionImgName,
                couponType = f_couponType,
                shopifyLink = f_shopifyLink,
                couponLevel = f_couponLevel
            });
        }


        [HttpGet("ARqueryEarly-V2")]
        public async Task<IActionResult> ARqueryEarlyV2([FromQuery] int locationId)
        {
            var results = new List<object>();

            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(@"
            SELECT 
                c.couponid, 
                c.modelimgname, 
                m.imgname AS missionimgname, -- 🆕
                c.coupontype, 
                c.companyid
            FROM location l
            INNER JOIN coupons c ON l.couponid = c.couponid
            LEFT JOIN missions m ON m.missionid = c.missionid -- 🆕
            WHERE l.locationid = @locationId
            ", connection);

            command.Parameters.AddWithValue("@locationId", locationId);

            int? couponId = null;
            string modelImgName = null;
            string missionImgName = null; // 🆕
            string couponType = null;
            int? companyId = null;

            using (var reader = await command.ExecuteReaderAsync())
            {
                if (await reader.ReadAsync())
                {
                    couponId = reader.GetInt32(0);
                    modelImgName = reader["modelimgname"] as string;

                    missionImgName = reader["missionimgname"] == DBNull.Value
                        ? null
                        : reader["missionimgname"].ToString(); // 🆕

                    couponType = reader["coupontype"] as string;
                    companyId = reader.GetInt32(4);
                }
            }

            if (modelImgName != null && couponId.HasValue)
            {
                results.Add(new
                {
                    modelImgName,
                    missionImgName, // 🆕 returned for preload
                    couponType
                });

                if (companyId.HasValue)
                {
                    await DripRunBackend.Services.ImpressionLogger
                        .LogCouponAsync(connection, userId, companyId.Value, couponId.Value);
                }
            }

            return Ok(results);
        }



        [HttpGet("user-coupons-v2")]
        public async Task<IActionResult> GetUserCouponsV2()
        {
            int? userId = GetUserIdFromToken();
            if (userId == null)
                return Unauthorized();

            var results = new List<object>();
            var toLog = new List<(int companyId, int couponId, int? missionId)>(); // row-level log buffer

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(@"
            SELECT 
            c.couponid, 
            c.imagename, 
            c.coupontype, 
            c.toslink,
            c.shopifylink,
            c.companyid,
            c.missionid,
            c.couponlevel,
            c.description,
            co.companyname
            FROM user_coupon uc
            JOIN coupons c ON uc.couponid = c.couponid
            JOIN company co ON c.companyid = co.companyid
            WHERE uc.userid = @userId;", connection);

            command.Parameters.AddWithValue("@userId", userId.Value);

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int couponId = reader.GetInt32(0);
                    string imageName = reader["imagename"].ToString();
                    string couponType = reader["coupontype"] == DBNull.Value ? null : reader["coupontype"].ToString();
                    string tosLink = reader["toslink"] == DBNull.Value ? null : reader["toslink"].ToString();
                    string shopifyLink = reader["shopifylink"] == DBNull.Value ? null : reader["shopifylink"].ToString();
                    int companyId = reader.GetInt32(5);
                    int? missionId = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);

                    int couponLevel = reader.GetInt32(reader.GetOrdinal("couponlevel"));
                    string description = reader["description"] == DBNull.Value ? null : reader["description"].ToString();
                    string companyName = reader["companyname"].ToString();

                    results.Add(new
                    {
                        couponId,
                        imageName,
                        couponType,
                        tosLink,
                        shopifyLink,
                        couponLevel,
                        description,
                        companyName
                    });

                    toLog.Add((companyId, couponId, missionId));
                }
            } // reader closed

            // Row-level logging: one insert per returned row
            foreach (var row in toLog)
            {
                await DripRunBackend.Services.ImpressionLogger.LogAsync(
                    connection,
                    userId.Value,
                    row.companyId,
                    couponId: row.couponId,
                    missionId: row.missionId
                );
            }

            return Ok(results);
        }

        //[HttpGet("user-couponcollectible-v2")]
        //public async Task<IActionResult> GetUserCollectiblesV2()
        //{
        //    int? userId = GetUserIdFromToken();
        //    if (userId == null)
        //        return Unauthorized();

        //    var results = new List<object>();
        //    var toLog = new List<(int companyId, int couponId, int? missionId)>(); // row-level log buffer

        //    await using var connection = new NpgsqlConnection(_connectionString);
        //    await connection.OpenAsync();

        //    using var command = new NpgsqlCommand(@"
        //    SELECT 
        //    c.couponid, 
        //    c.imagename, 
        //    c.coupontype, 
        //    c.toslink,
        //    c.shopifylink,
        //    c.companyid,
        //    c.missionid,
        //    c.couponlevel,
        //    c.description,
        //    co.companyname
        //    FROM user_coupon uc
        //    JOIN coupons c ON uc.couponid = c.couponid
        //    JOIN company co ON c.companyid = co.companyid
        //    WHERE uc.userid = @userId
        //    AND c.couponlevel BETWEEN 1 AND 50;", connection);

        //    command.Parameters.AddWithValue("@userId", userId.Value);

        //    using (var reader = await command.ExecuteReaderAsync())
        //    {
        //        while (await reader.ReadAsync())
        //        {
        //            int couponId = reader.GetInt32(0);
        //            string imageName = reader["imagename"].ToString();
        //            string couponType = reader["coupontype"] == DBNull.Value ? null : reader["coupontype"].ToString();
        //            string tosLink = reader["toslink"] == DBNull.Value ? null : reader["toslink"].ToString();
        //            string shopifyLink = reader["shopifylink"] == DBNull.Value ? null : reader["shopifylink"].ToString();
        //            int companyId = reader.GetInt32(5);
        //            int? missionId = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);

        //            int couponLevel = reader.GetInt32(reader.GetOrdinal("couponlevel"));
        //            string description = reader["description"] == DBNull.Value ? null : reader["description"].ToString();
        //            string companyName = reader["companyname"].ToString();

        //            results.Add(new
        //            {
        //                couponId,
        //                imageName,
        //                couponType,
        //                tosLink,
        //                shopifyLink,
        //                couponLevel,
        //                description,
        //                companyName
        //            });

        //            toLog.Add((companyId, couponId, missionId));
        //        }
        //    } // reader closed

        //    // Row-level logging: one insert per returned row
        //    foreach (var row in toLog)
        //    {
        //        await DripRunBackend.Services.ImpressionLogger.LogAsync(
        //            connection,
        //            userId.Value,
        //            row.companyId,
        //            couponId: row.couponId,
        //            missionId: row.missionId
        //        );
        //    }

        //    return Ok(results);
        //}


        [HttpGet("missionsquery")]
        public async Task<IActionResult> Missions()
        {
            // Get current user ID from JWT
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            var results = new List<object>();
            var companyIds = new List<(int companyId, int missionId)>();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(@"
            WITH user_counts AS (
            SELECT
                co.missionid,
                COUNT(*)::INT AS owned_count
            FROM user_coupon uc
            JOIN coupons co ON co.couponid = uc.couponid
            WHERE uc.userid = @userId
              AND co.missionid IS NOT NULL
            GROUP BY co.missionid
            )
            SELECT 
            m.missionid,
            m.name,
            m.status,
            m.imgname,
            m.locationname,
            c.companyname,
            c.companyid,
            COALESCE(m.coupontotal, 0)::INT AS total_count,
            COALESCE(uc.owned_count, 0)::INT AS owned_count
            FROM missions m
            JOIN company c ON m.companyid = c.companyid
            LEFT JOIN user_counts uc ON uc.missionid = m.missionid
            WHERE m.isvisible = TRUE
            ORDER BY
            CASE WHEN m.status = 'Active' THEN 0 ELSE 1 END,
            m.name;", connection);

            command.Parameters.AddWithValue("@userId", userId);

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int missionId = reader.GetInt32(0);
                    string name = reader.GetString(1);
                    string status = reader.GetString(2);
                    string imgName = reader.GetString(3);
                    string location = reader.GetString(4);
                    string company = reader.GetString(5);
                    int companyId = reader.GetInt32(6);
                    int total = reader.GetInt32(7);
                    int owned = reader.GetInt32(8);

                    // Build "X/Y" string
                    string progress = $"{owned}/{total}";

                    results.Add(new
                    {
                        missionId,
                        name,
                        status,
                        imgName,
                        locationName = location,
                        companyName = company,
                        couponProgress = progress
                        // If you also want raw numbers, add: owned, total
                    });

                    companyIds.Add((companyId, missionId));
                }
            } // reader closed

            // After reader closes, log one mission-impression per returned row
            foreach (var item in companyIds)
            {
                await DripRunBackend.Services.ImpressionLogger.LogMissionAsync(
                    connection, userId, item.companyId, item.missionId);
            }

            return Ok(results);
        }

        [HttpGet("login-points")]
        public async Task<IActionResult> GetLoginPoints()
        {
            int? userId = GetUserIdFromToken();
            if (userId == null)
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(@"
                SELECT ldboardpts FROM ""users"" WHERE userid = @userId", connection);

            command.Parameters.AddWithValue("@userId", userId.Value);

            object result = await command.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
                return NotFound("Points not found for user.");

            int points = Convert.ToInt32(result);
            return Ok(new { points });
        }

        [HttpGet("leaderboard-prize")]
        public async Task<IActionResult> GetLatestLeaderboardPrize()
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = new NpgsqlCommand(@"
            SELECT prizeimagename
            FROM leaderboardprizes
            ORDER BY prizeid DESC
            LIMIT 1;", connection);

            var result = await command.ExecuteScalarAsync();

            if (result == null || result == DBNull.Value)
                return NotFound(new { error = "No leaderboard prize found." });

            string prizeImageName = result.ToString();

            return Ok(new
            {
                prizeImageName
            });
        }
    }
}

namespace DripRunBackend.Services
{
    public static class ImpressionLogger
    {
        /// <summary>
        /// Logs an impression. Either couponId or missionId (or both) can be null.
        /// Always include companyId for scoping.
        /// </summary>
        public static async Task LogAsync(
            NpgsqlConnection existingOpenConn,
            int userId,
            int companyId,
            int? couponId = null,
            int? missionId = null,
            System.Threading.CancellationToken ct = default)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO userbrandimpressions (userid, companyid, couponid, missionid, currenttime)
                    VALUES (@UserId, @CompanyId, @CouponId, @MissionId, NOW());
                ", existingOpenConn);

                cmd.Parameters.AddWithValue("UserId", userId);
                cmd.Parameters.AddWithValue("CompanyId", companyId);
                cmd.Parameters.AddWithValue("CouponId", (object?)couponId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("MissionId", (object?)missionId ?? DBNull.Value);

                await cmd.ExecuteNonQueryAsync(ct);
                Console.WriteLine($"[ImpressionLogger] Logged impression u:{userId} cpy:{companyId} cup:{couponId?.ToString() ?? "null"} mis:{missionId?.ToString() ?? "null"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ImpressionLogger] Failed: {ex}");
            }
        }

        // Convenience helpers (optional):
        public static Task LogCompanyAsync(NpgsqlConnection conn, int userId, int companyId, CancellationToken ct = default)
            => LogAsync(conn, userId, companyId, null, null, ct);

        public static Task LogCouponAsync(NpgsqlConnection conn, int userId, int companyId, int couponId, CancellationToken ct = default)
            => LogAsync(conn, userId, companyId, couponId, null, ct);

        public static Task LogMissionAsync(NpgsqlConnection conn, int userId, int companyId, int missionId, CancellationToken ct = default)
            => LogAsync(conn, userId, companyId, null, missionId, ct);
    }
}
