// CouponAdderController.cs
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System;
using System.Security.Claims;
using System.Threading.Tasks;

namespace DripRunBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class CouponAdderController : ControllerBase
    {
        private readonly IConfiguration _configuration;

        public CouponAdderController(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        [HttpPost("v1")]
        [Authorize]
        public async Task<IActionResult> AddUserCoupon([FromBody] UserCouponDtoV1 dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 1) Basic sanity: ensure the locationId exists and maps to the couponId provided (and fetch target lat/lon)
                double targetLat, targetLon;
                int? couponFromLocation = null;

                await using (var locCmd = new NpgsqlCommand(@"
                SELECT couponid, latitude, longitude
                FROM location
                WHERE locationid = @locationId;
                ", conn, tx))
                {
                    locCmd.Parameters.AddWithValue("@locationId", dto.locationId);

                    await using var r = await locCmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return BadRequest(new { error = "Invalid locationId." });
                    }

                    couponFromLocation = r.GetInt32(0);
                    targetLat = r.GetDouble(1);
                    targetLon = r.GetDouble(2);
                }

                if (couponFromLocation.Value != dto.couponId)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new { error = "locationId does not match couponId." });
                }

                // 2) Speed check (same style as before). If sus -> reject.
                //    Uses most recent (claimedat) user_coupon where claimedviadaily=false, picks closest location for that coupon.
                const string speedSql = @"
                WITH prev AS (
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
                    2 * 6371000 * asin(
                      sqrt(
                        pow(sin(radians((@tLat - l.latitude) / 2.0)), 2) +
                        cos(radians(l.latitude)) * cos(radians(@tLat)) *
                        pow(sin(radians((@tLon - l.longitude) / 2.0)), 2)
                      )
                    ) AS distance_m
                  FROM location l
                  JOIN prev p ON p.prev_couponid = l.couponid
                  ORDER BY distance_m ASC
                  LIMIT 1
                ),
                calc AS (
                  SELECT
                    pc.distance_m,
                    EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) AS elapsed_s,
                    CASE
                      WHEN EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) > 0
                      THEN (pc.distance_m / EXTRACT(EPOCH FROM (now() - p.prev_claimedat))) * 3.6
                    END AS speed_kph
                  FROM prev p
                  LEFT JOIN prev_closest pc ON TRUE
                )
                SELECT distance_m, elapsed_s, speed_kph
                FROM calc;
                ";

                double? distanceM = null, elapsedS = null, speedKph = null;

                await using (var speedCmd = new NpgsqlCommand(speedSql, conn, tx))
                {
                    speedCmd.Parameters.AddWithValue("@uid", userId);
                    speedCmd.Parameters.AddWithValue("@tLat", targetLat);
                    speedCmd.Parameters.AddWithValue("@tLon", targetLon);

                    await using var r = await speedCmd.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        distanceM = r["distance_m"] == DBNull.Value ? (double?)null : Convert.ToDouble(r["distance_m"]);
                        elapsedS = r["elapsed_s"] == DBNull.Value ? (double?)null : Convert.ToDouble(r["elapsed_s"]);
                        speedKph = r["speed_kph"] == DBNull.Value ? (double?)null : Convert.ToDouble(r["speed_kph"]);
                    }
                }

                // Threshold (tweak later). If no prev coupon exists, speedKph will be null => allow.
                if (speedKph.HasValue && speedKph.Value > 200)
                {
                    await tx.RollbackAsync();
                    return StatusCode(403, new
                    {
                        rejected = true,
                        reason = "Unrealistic travel speed detected",
                        distanceMeters = distanceM,
                        elapsedSeconds = elapsedS,
                        speedKph
                    });
                }

                // 3) Advisory lock per coupon (keeps your existing anti-race behavior)
                await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k);", conn, tx))
                {
                    lockCmd.Parameters.AddWithValue("@k", dto.couponId);
                    await lockCmd.ExecuteNonQueryAsync();
                }

                // 4) Read couponlevel + leaderboard points
                int? couponLevel = null;
                int pointsToAdd = 5; // fallback safety

                await using (var lvlCmd = new NpgsqlCommand(
                    "SELECT couponlevel, ldboardpts FROM coupons WHERE couponid = @couponid;",
                    conn, tx))
                {
                    lvlCmd.Parameters.AddWithValue("@couponid", dto.couponId);

                    await using var r = await lvlCmd.ExecuteReaderAsync();
                    if (await r.ReadAsync())
                    {
                        couponLevel = r["couponlevel"] == DBNull.Value ? (int?)null : Convert.ToInt32(r["couponlevel"]);
                        pointsToAdd = r["ldboardpts"] == DBNull.Value ? 5 : Convert.ToInt32(r["ldboardpts"]);
                    }
                }

                // 4.5) Reject raid-type coupons (99) — handled elsewhere
                if (couponLevel == 99)
                {
                    await tx.RollbackAsync();
                    return StatusCode(403, new
                    {
                        rejected = true,
                        reason = "Raid coupon requires group participation"
                    });
                }

                // 5) Insert claim (server-side timestamps + claimedviadaily=false)
                const string insertSql = @"
                INSERT INTO user_coupon (userid, couponid, dateearned, claimedat, claimedviadaily)
                VALUES (@userid, @couponid, now(), now(), false)
                ON CONFLICT (userid, couponid) DO NOTHING
                RETURNING 1;
                ";

                await using (var insertCmd = new NpgsqlCommand(insertSql, conn, tx))
                {
                    insertCmd.Parameters.AddWithValue("@userid", userId);
                    insertCmd.Parameters.AddWithValue("@couponid", dto.couponId);

                    var inserted = await insertCmd.ExecuteScalarAsync();
                    if (inserted == null)
                    {
                        await tx.RollbackAsync();
                        return Conflict(new { error = "Coupon already claimed." });
                    }
                }

                // 6) Limited coupon => remove all its locations (existing logic)
                long deletedLocations = 0;
                if (couponLevel == 100)
                {
                    const string deleteLocSql = @"DELETE FROM location WHERE couponid = @couponid;";
                    await using var delCmd = new NpgsqlCommand(deleteLocSql, conn, tx);
                    delCmd.Parameters.AddWithValue("@couponid", dto.couponId);
                    deletedLocations = await delCmd.ExecuteNonQueryAsync();
                }

                // 7) Award points (existing logic)
                const string pointsSql = @"
                UPDATE users
                SET ldboardpts = COALESCE(ldboardpts, 0) + @points
                WHERE userid = @userid;
                ";
                await using (var pointsCmd = new NpgsqlCommand(pointsSql, conn, tx))
                {
                    pointsCmd.Parameters.AddWithValue("@userid", userId);
                    pointsCmd.Parameters.AddWithValue("@points", pointsToAdd);
                    await pointsCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();
                return Ok(new
                {
                    message = "Coupon claimed and points awarded.",
                    pointsAdded = pointsToAdd,
                    locationId = dto.locationId,
                    couponId = dto.couponId,
                    deletedLocations
                });
            }
            catch (PostgresException pgEx)
            {
                await tx.RollbackAsync();

                if (pgEx.SqlState == "23505")
                    return Conflict(new { error = "Coupon already claimed." });

                return StatusCode(500, new { error = pgEx.Message });
            }
        }

        [HttpPost("raid-join")]
        [Authorize]
        public async Task<IActionResult> JoinRaid([FromBody] UserCouponDtoV1 dto)
        {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                    return Unauthorized();

                var connectionString = _configuration.GetConnectionString("DefaultConnection");

                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                try
                {
                    // 1) Validate locationId ↔ couponId and get lat/lon
                    double targetLat, targetLon;

                    await using (var locCmd = new NpgsqlCommand(@"
                    SELECT couponid, latitude, longitude
                    FROM location
                    WHERE locationid = @locationId;
                ", conn))
                    {
                        locCmd.Parameters.AddWithValue("@locationId", dto.locationId);

                        await using var r = await locCmd.ExecuteReaderAsync();
                        if (!await r.ReadAsync())
                            return BadRequest(new { error = "Invalid locationId." });

                        if (r.GetInt32(0) != dto.couponId)
                            return BadRequest(new { error = "locationId does not match couponId." });

                        targetLat = r.GetDouble(1);
                        targetLon = r.GetDouble(2);
                    }

                    // 2) Speed check (same as v1)
                    double? speedKph = null;

                    const string speedSql = @"
                    WITH prev AS (
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
                        2 * 6371000 * asin(
                          sqrt(
                            pow(sin(radians((@tLat - l.latitude) / 2.0)), 2) +
                            cos(radians(l.latitude)) * cos(radians(@tLat)) *
                            pow(sin(radians((@tLon - l.longitude) / 2.0)), 2)
                          )
                        ) AS distance_m
                      FROM location l
                      JOIN prev p ON p.prev_couponid = l.couponid
                      ORDER BY distance_m ASC
                      LIMIT 1
                    ),
                    calc AS (
                      SELECT
                        pc.distance_m,
                        EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) AS elapsed_s,
                        CASE
                          WHEN EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) > 0
                          THEN (pc.distance_m / EXTRACT(EPOCH FROM (now() - p.prev_claimedat))) * 3.6
                        END AS speed_kph
                      FROM prev p
                      LEFT JOIN prev_closest pc ON TRUE
                    )
                    SELECT speed_kph FROM calc;
                    ";

                    await using (var speedCmd = new NpgsqlCommand(speedSql, conn))
                    {
                        speedCmd.Parameters.AddWithValue("@uid", userId);
                        speedCmd.Parameters.AddWithValue("@tLat", targetLat);
                        speedCmd.Parameters.AddWithValue("@tLon", targetLon);

                        var result = await speedCmd.ExecuteScalarAsync();

                        if (result != null && result != DBNull.Value)
                        {
                            speedKph = Convert.ToDouble(result);

                            if (speedKph > 200)
                            {
                                return StatusCode(403, new
                                {
                                    rejected = true,
                                    reason = "Unrealistic travel speed detected"
                                });
                            }
                        }
                    }

                    // 3) Insert raid instance
                    await using (var cmd = new NpgsqlCommand(@"
                        INSERT INTO RaidInstances (CouponID, UserID, TimeCreated)
                        VALUES (@couponId, @userId, now());
                    ", conn))
                    {
                        cmd.Parameters.AddWithValue("@couponId", dto.couponId);
                        cmd.Parameters.AddWithValue("@userId", userId);

                        await cmd.ExecuteNonQueryAsync();
                    }

                    return Ok(new
                    {
                        message = "Joined raid successfully.",
                        dto.couponId,
                        userId
                    });
                }
                catch (PostgresException pgEx)
                {
                    return StatusCode(500, new { error = pgEx.Message });
                }
        }


        [HttpPost("raid-check")]
        [Authorize]
        public async Task<IActionResult> CheckRaid([FromBody] UserCouponDtoV1 dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int currentUserId))
                return Unauthorized();

            var connectionString = _configuration.GetConnectionString("DefaultConnection");

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // 🔒 Lock per coupon
                await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k);", conn, tx))
                {
                    lockCmd.Parameters.AddWithValue("@k", dto.couponId);
                    await lockCmd.ExecuteNonQueryAsync();
                }

                // ✅ EARLY EXIT: if current user already owns it → success
                await using (var ownCmd = new NpgsqlCommand(@"
                    SELECT 1 FROM user_coupon 
                    WHERE userid = @uid AND couponid = @couponId;
                ", conn, tx))
                {
                    ownCmd.Parameters.AddWithValue("@uid", currentUserId);
                    ownCmd.Parameters.AddWithValue("@couponId", dto.couponId);

                    var alreadyOwned = await ownCmd.ExecuteScalarAsync();
                    if (alreadyOwned != null)
                    {
                        await tx.CommitAsync();
                        return Ok(new
                        {
                            success = true,
                            alreadyOwned = true
                        });
                    }
                }

                // 1) Validate location + get lat/lon
                double targetLat, targetLon;

                await using (var locCmd = new NpgsqlCommand(@"
                    SELECT couponid, latitude, longitude
                    FROM location
                    WHERE locationid = @locationId;
                ", conn, tx))
                {
                    locCmd.Parameters.AddWithValue("@locationId", dto.locationId);

                    await using var r = await locCmd.ExecuteReaderAsync();
                    if (!await r.ReadAsync())
                    {
                        await tx.RollbackAsync();
                        return BadRequest(new { error = "Invalid locationId." });
                    }

                    if (r.GetInt32(0) != dto.couponId)
                    {
                        await tx.RollbackAsync();
                        return BadRequest(new { error = "locationId does not match couponId." });
                    }

                    targetLat = r.GetDouble(1);
                    targetLon = r.GetDouble(2);
                }

                // 2) Get raidCount
                int raidCount;
                await using (var cmd = new NpgsqlCommand(
                    "SELECT raidcount FROM coupons WHERE couponid = @couponId;",
                    conn, tx))
                {
                    cmd.Parameters.AddWithValue("@couponId", dto.couponId);
                    raidCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // 3) Get users (last 30 mins)
                var userIds = new List<int>();

                await using (var cmd = new NpgsqlCommand(@"
                    SELECT userid
                    FROM RaidInstances
                    WHERE couponid = @couponId
                      AND timecreated >= now() - interval '30 minutes'
                    LIMIT @raidCount;
                ", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@couponId", dto.couponId);
                    cmd.Parameters.AddWithValue("@raidCount", raidCount);

                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                        userIds.Add(reader.GetInt32(0));
                }

                // 4) Not enough players yet
                if (userIds.Count < raidCount)
                {
                    await tx.RollbackAsync();
                    return Ok(new
                    {
                        success = false,
                        currentCount = userIds.Count,
                        requiredCount = raidCount
                    });
                }

                // 5) Speed check ALL users
                foreach (var userId in userIds)
                {
                    const string speedSql = @"
                    WITH prev AS (
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
                        2 * 6371000 * asin(
                          sqrt(
                            pow(sin(radians((@tLat - l.latitude) / 2.0)), 2) +
                            cos(radians(l.latitude)) * cos(radians(@tLat)) *
                            pow(sin(radians((@tLon - l.longitude) / 2.0)), 2)
                          )
                        ) AS distance_m
                      FROM location l
                      JOIN prev p ON p.prev_couponid = l.couponid
                      ORDER BY distance_m ASC
                      LIMIT 1
                    ),
                    calc AS (
                      SELECT
                        pc.distance_m,
                        EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) AS elapsed_s,
                        CASE
                          WHEN EXTRACT(EPOCH FROM (now() - p.prev_claimedat)) > 0
                          THEN (pc.distance_m / EXTRACT(EPOCH FROM (now() - p.prev_claimedat))) * 3.6
                        END AS speed_kph
                      FROM prev p
                      LEFT JOIN prev_closest pc ON TRUE
                    )
                    SELECT speed_kph FROM calc;
                    ";

                    await using var speedCmd = new NpgsqlCommand(speedSql, conn, tx);
                    speedCmd.Parameters.AddWithValue("@uid", userId);
                    speedCmd.Parameters.AddWithValue("@tLat", targetLat);
                    speedCmd.Parameters.AddWithValue("@tLon", targetLon);

                    var result = await speedCmd.ExecuteScalarAsync();

                    if (result != null && result != DBNull.Value)
                    {
                        double speedKph = Convert.ToDouble(result);
                        if (speedKph > 200)
                        {
                            await tx.RollbackAsync();
                            return StatusCode(403, new
                            {
                                rejected = true,
                                reason = "Raid failed due to suspicious movement"
                            });
                        }
                    }
                }

                // 6) Award ALL users (safe)
                foreach (var userId in userIds)
                {
                    await using var insertCmd = new NpgsqlCommand(@"
                        INSERT INTO user_coupon (userid, couponid, dateearned, claimedat, claimedviadaily)
                        VALUES (@userid, @couponid, now(), now(), false)
                        ON CONFLICT (userid, couponid) DO NOTHING;
                    ", conn, tx);

                    insertCmd.Parameters.AddWithValue("@userid", userId);
                    insertCmd.Parameters.AddWithValue("@couponid", dto.couponId);

                    await insertCmd.ExecuteNonQueryAsync();
                }

                // 7) Cleanup raid
                await using (var deleteCmd = new NpgsqlCommand(@"
                    DELETE FROM RaidInstances
                    WHERE couponid = @couponId
                      AND timecreated >= now() - interval '30 minutes';
                ", conn, tx))
                {
                    deleteCmd.Parameters.AddWithValue("@couponId", dto.couponId);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                await tx.CommitAsync();

                return Ok(new
                {
                    success = true,
                    rewardedUsers = userIds.Count
                });
            }
            catch (PostgresException pgEx)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { error = pgEx.Message });
            }
        }



        public class UserCouponDtoV1
        {
            public int couponId { get; set; }
            public int locationId { get; set; }
        }

    }
}
