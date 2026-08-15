using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Data;
using Microsoft.AspNetCore.Http;
using System.IO;


namespace DripRunBackend.Controllers
{
    [Authorize(Policy = "AdminJwt")]
    [ApiController]
    [Route("api/admin")]
    public sealed class AdminController : ControllerBase
    {
        private readonly string _cs;

        public AdminController(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
        }


        // -------------------------------
        // Stats models
        // -------------------------------
        // GET /api/admin/stats?missionId=-1&start=2025-11-01&end=2025-11-10
        // start inclusive, end exclusive; omit both for all-time
        // -------------------------------
        public sealed class StatsResponse
        {
            public ScopeSummary Summary { get; set; } = new();
            public SplitBreakdown Splits { get; set; } = new();
            public NewPlayersBlock NewPlayers { get; set; } = new();
            public TotalsBlock Totals { get; set; } = new();
            public List<CouponRow> Coupons { get; set; } = new();

            public sealed class ScopeSummary
            {
                public int CompanyId { get; set; }
                public int MissionId { get; set; } // -1 for All
                public bool IsAllTime { get; set; }
                public DateTime? Start { get; set; }
                public DateTime? End { get; set; }
                public string? MissionStatus { get; set; }
            }

            public sealed class SplitBreakdown
            {
                public int UniqueImpressionUsers { get; set; }
                public List<LabelPct> GenderPct { get; set; } = new();
                public List<LabelPct> AgeRangePct { get; set; } = new();
            }

            public sealed class LabelPct
            {
                public string Label { get; set; } = "";
                public double Pct { get; set; }
            }

            public sealed class NewPlayersBlock
            {
                public int Total { get; set; }
                public List<DayCount> Daily { get; set; } = new();
            }

            public sealed class DayCount
            {
                public DateTime Day { get; set; }
                public int Count { get; set; }
            }

            public sealed class TotalsBlock
            {
                public long Impressions { get; set; }
                public int UniqueUserCoupons { get; set; }
                public long TotalUserCoupons { get; set; }
                public double AvgDropsPerViewer { get; set; }
            }

            public sealed class CouponRow
            {
                public int CouponId { get; set; }
                public string ShopifyLink { get; set; } = "";
                public string DropName { get; set; } = "";
                public int DistinctUsersWithCoupon { get; set; }
                public long Impressions { get; set; }
            }
        }


        [HttpGet("stats")]
        public async Task<IActionResult> GetStats([FromQuery] int missionId, [FromQuery] DateTime? start, [FromQuery] DateTime? end)
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            var hasRange = start.HasValue && end.HasValue;
            if (hasRange && start!.Value >= end!.Value)
                return BadRequest(new { error = "Invalid date range (start must be before end)." });

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();

            // 0) Scope: coupons that define the set of impressions & user_coupons to consider
            //    missionId >= 0 => coupons for that mission
            //    missionId = -1 => all company coupons WITH a MissionId (explicitly exclude null mission)
            const string scopeSql = @"
                CREATE TEMP TABLE _scope_coupons AS
                SELECT 
                    c.couponid,
                    c.missionid,
                    c.shopifylink,
                    COALESCE(c.dropname, '') AS dropname
                FROM coupons c
                WHERE c.companyid = @companyId
                  AND (
                        (@missionId = -1 AND c.missionid IS NOT NULL)
                     OR (@missionId <> -1 AND c.missionid = @missionId)
                  );";
            await using (var cmd = new NpgsqlCommand(scopeSql, con))
            {
                cmd.Parameters.AddWithValue("companyId", companyId);
                cmd.Parameters.AddWithValue("missionId", missionId);
                await cmd.ExecuteNonQueryAsync();
            }

            // ✅ Mission status for summary (single mission only)
            string? missionStatus = null;

            if (missionId == -1)
            {
                missionStatus = "Mixed"; // all missions scope
            }
            else
            {
                const string missionStatusSql = @"
                SELECT m.status
                FROM missions m
                WHERE m.companyid = @companyId
                AND m.missionid = @missionId
                LIMIT 1;";

                await using var statusCmd = new NpgsqlCommand(missionStatusSql, con);
                statusCmd.Parameters.AddWithValue("companyId", companyId);
                statusCmd.Parameters.AddWithValue("missionId", missionId);

                var obj = await statusCmd.ExecuteScalarAsync();
                missionStatus = (obj == null || obj == DBNull.Value) ? null : obj.ToString();
            }

            // 1) Unique impression users (ONLY impressions that reference a scoped coupon)
            const string imprUsersSql = @"
                CREATE TEMP TABLE _impr_users AS
                SELECT DISTINCT i.userid
                FROM userbrandimpressions i
                JOIN _scope_coupons s ON s.couponid = i.couponid
                WHERE (@hasRange = FALSE) OR (i.currenttime >= @start AND i.currenttime < @end);";
            await using (var cmd = new NpgsqlCommand(imprUsersSql, con))
            {
                cmd.Parameters.AddWithValue("hasRange", hasRange);
                cmd.Parameters.AddWithValue("start", (object?)start ?? DBNull.Value);
                cmd.Parameters.AddWithValue("end", (object?)end ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();
            }

            int uniqueImprUsers;
            await using (var cmd = new NpgsqlCommand(@"SELECT COUNT(*) FROM _impr_users;", con))
            {
                uniqueImprUsers = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            static async Task<List<StatsResponse.LabelPct>> ReadLabelPctAsync(NpgsqlCommand cmd)
            {
                var list = new List<StatsResponse.LabelPct>();
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    list.Add(new StatsResponse.LabelPct
                    {
                        Label = r.IsDBNull(0) ? "" : r.GetString(0),
                        Pct = r.IsDBNull(1) ? 0d : r.GetDouble(1)
                    });
                }
                return list;
            }

            // Splits: Gender / AgeRange, among those unique impression users
            List<StatsResponse.LabelPct> genderPct;
            const string genderSql = @"
                WITH base AS (
                    SELECT u.""Gender""
                    FROM users u
                    JOIN _impr_users iu ON iu.userid = u.userid
                    WHERE u.""Gender"" IS NOT NULL
                ),
                denom AS (SELECT COUNT(*)::float AS n FROM base),
                agg AS (SELECT ""Gender"" AS label, COUNT(*)::float AS c FROM base GROUP BY ""Gender"")
                SELECT label, CASE WHEN d.n > 0 THEN ROUND(((a.c / d.n) * 100.0)::numeric, 2)::double precision ELSE 0 END AS pct
                FROM agg a CROSS JOIN denom d
                ORDER BY pct DESC, label;";
            await using (var cmd = new NpgsqlCommand(genderSql, con))
                genderPct = await ReadLabelPctAsync(cmd);

            List<StatsResponse.LabelPct> agePct;
            const string ageSql = @"
                WITH base AS (
                    SELECT u.""AgeRange""
                    FROM users u
                    JOIN _impr_users iu ON iu.userid = u.userid
                    WHERE u.""AgeRange"" IS NOT NULL
                ),
                denom AS (SELECT COUNT(*)::float AS n FROM base),
                agg AS (SELECT ""AgeRange""::text AS label, COUNT(*)::float AS c FROM base GROUP BY ""AgeRange"")
                SELECT label, CASE WHEN d.n > 0 THEN ROUND(((a.c / d.n) * 100.0)::numeric, 2)::double precision ELSE 0 END AS pct
                FROM agg a CROSS JOIN denom d
                ORDER BY pct DESC, label;";
            await using (var cmd = new NpgsqlCommand(ageSql, con))
                agePct = await ReadLabelPctAsync(cmd);


            // 2) New players: among in-scope impression users whose users.created_at is within range (only if range provided)
            var newDaily = new List<StatsResponse.DayCount>();
            var newTotal = 0;
            if (hasRange)
            {
                const string newDailySql = @"
                    SELECT (i.currenttime::date) AS day, COUNT(DISTINCT i.userid) AS cnt
                    FROM userbrandimpressions i
                    JOIN _scope_coupons s ON s.couponid = i.couponid
                    JOIN users u ON u.userid = i.userid
                    WHERE i.currenttime >= @start AND i.currenttime < @end
                      AND u.created_at >= @start AND u.created_at < @end
                    GROUP BY day
                    ORDER BY day;";
                await using (var cmd = new NpgsqlCommand(newDailySql, con))
                {
                    cmd.Parameters.AddWithValue("start", start!.Value);
                    cmd.Parameters.AddWithValue("end", end!.Value);
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        newDaily.Add(new StatsResponse.DayCount
                        {
                            Day = r.GetDateTime(0),
                            Count = r.GetInt32(1)
                        });
                    }
                }

                const string newTotalSql = @"
                    SELECT COUNT(DISTINCT i.userid)
                    FROM userbrandimpressions i
                    JOIN _scope_coupons s ON s.couponid = i.couponid
                    JOIN users u ON u.userid = i.userid
                    WHERE i.currenttime >= @start AND i.currenttime < @end
                      AND u.created_at >= @start AND u.created_at < @end;";
                await using (var cmd = new NpgsqlCommand(newTotalSql, con))
                {
                    cmd.Parameters.AddWithValue("start", start!.Value);
                    cmd.Parameters.AddWithValue("end", end!.Value);
                    newTotal = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }
            }

            // 3) Totals
            long totalImpressions;
            const string imprTotalSql = @"
            SELECT COUNT(*)
            FROM userbrandimpressions i
            WHERE
            ((@hasRange = FALSE) OR (i.currenttime >= @start AND i.currenttime < @end))
            AND (
            (@missionId <> -1 AND i.missionid = @missionId)
            OR
            (@missionId = -1 AND i.companyid = @companyId AND i.missionid IS NOT NULL)
            OR
            (i.couponid IS NOT NULL AND EXISTS (SELECT 1 FROM _scope_coupons s WHERE s.couponid = i.couponid))
            );";

            await using (var cmd = new NpgsqlCommand(imprTotalSql, con))
            {
                cmd.Parameters.AddWithValue("hasRange", hasRange);
                cmd.Parameters.AddWithValue("start", (object?)start ?? DBNull.Value);
                cmd.Parameters.AddWithValue("end", (object?)end ?? DBNull.Value);

                // ✅ missing before
                cmd.Parameters.AddWithValue("companyId", companyId);
                cmd.Parameters.AddWithValue("missionId", missionId);

                totalImpressions = (long)await cmd.ExecuteScalarAsync();
            }

            int distinctUsersWithAnyCoupon;
            const string ucDistinctSql = @"
                SELECT COUNT(DISTINCT uc.userid)
                FROM user_coupon uc
                JOIN _scope_coupons s ON s.couponid = uc.couponid
                WHERE (@hasRange = FALSE) OR (uc.dateearned >= @start AND uc.dateearned < @end);";
            await using (var cmd = new NpgsqlCommand(ucDistinctSql, con))
            {
                cmd.Parameters.AddWithValue("hasRange", hasRange);
                cmd.Parameters.AddWithValue("start", (object?)start ?? DBNull.Value);
                cmd.Parameters.AddWithValue("end", (object?)end ?? DBNull.Value);
                distinctUsersWithAnyCoupon = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            long totalUserCoupons;
            const string ucTotalSql = @"
                SELECT COUNT(*)
                FROM user_coupon uc
                JOIN _scope_coupons s ON s.couponid = uc.couponid
                WHERE (@hasRange = FALSE) OR (uc.dateearned >= @start AND uc.dateearned < @end);";
            await using (var cmd = new NpgsqlCommand(ucTotalSql, con))
            {
                cmd.Parameters.AddWithValue("hasRange", hasRange);
                cmd.Parameters.AddWithValue("start", (object?)start ?? DBNull.Value);
                cmd.Parameters.AddWithValue("end", (object?)end ?? DBNull.Value);
                totalUserCoupons = (long)await cmd.ExecuteScalarAsync();
            }

            double avgDropsPerViewer = uniqueImprUsers > 0
                ? Math.Round((double)totalUserCoupons / uniqueImprUsers, 3)
                : 0d;

            // 4) Per-coupon breakdown
            var coupons = new List<StatsResponse.CouponRow>();
            const string perCouponSql = @"
                WITH ucd AS (
                    SELECT uc.couponid, COUNT(DISTINCT uc.userid) AS users_distinct
                    FROM user_coupon uc
                    JOIN _scope_coupons s ON s.couponid = uc.couponid
                    WHERE (@hasRange = FALSE) OR (uc.dateearned >= @start AND uc.dateearned < @end)
                    GROUP BY uc.couponid
                ),
                impr AS (
                    SELECT i.couponid, COUNT(*) AS impressions
                    FROM userbrandimpressions i
                    JOIN _scope_coupons s ON s.couponid = i.couponid
                    WHERE (@hasRange = FALSE) OR (i.currenttime >= @start AND i.currenttime < @end)
                    GROUP BY i.couponid
                )
                SELECT 
                    s.couponid,
                    COALESCE(s.shopifylink, '') AS shopifylink,
                    COALESCE(s.dropname, '')    AS dropname,
                    COALESCE(ucd.users_distinct, 0) AS users_distinct,
                    COALESCE(impr.impressions, 0) AS impressions
                FROM _scope_coupons s
                LEFT JOIN ucd  ON ucd.couponid  = s.couponid
                LEFT JOIN impr ON impr.couponid = s.couponid
                ORDER BY s.couponid;";
            await using (var cmd = new NpgsqlCommand(perCouponSql, con))
            {
                cmd.Parameters.AddWithValue("hasRange", hasRange);
                cmd.Parameters.AddWithValue("start", (object?)start ?? DBNull.Value);
                cmd.Parameters.AddWithValue("end", (object?)end ?? DBNull.Value);

                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    coupons.Add(new StatsResponse.CouponRow
                    {
                        CouponId = r.GetInt32(0),
                        ShopifyLink = r.GetString(1),
                        DropName = r.GetString(2),
                        DistinctUsersWithCoupon = r.GetInt32(3),
                        Impressions = r.GetInt64(4)
                    });
                }
            }

            var resp = new StatsResponse
            {
                Summary = new StatsResponse.ScopeSummary
                {
                    CompanyId = companyId,
                    MissionId = missionId,
                    MissionStatus = missionStatus,
                    IsAllTime = !hasRange,
                    Start = start,
                    End = end
                },
                Splits = new StatsResponse.SplitBreakdown
                {
                    UniqueImpressionUsers = uniqueImprUsers,
                    GenderPct = genderPct,
                    AgeRangePct = agePct,
                },
                NewPlayers = new StatsResponse.NewPlayersBlock
                {
                    Total = newTotal,
                    Daily = newDaily
                },
                Totals = new StatsResponse.TotalsBlock
                {
                    Impressions = totalImpressions,
                    UniqueUserCoupons = distinctUsersWithAnyCoupon,
                    TotalUserCoupons = totalUserCoupons,
                    AvgDropsPerViewer = avgDropsPerViewer
                },
                Coupons = coupons
            };

            return Ok(resp);
        }



        // -------------------------------
        // GET /api/admin/missions        - Getter and Deleter
        // -------------------------------
        [HttpGet("missions")]
        public async Task<IActionResult> GetMissions()
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();

            const string sql = @"
                SELECT 
                    m.missionid,
                    m.name,
                    m.locationname,
                    COUNT(c.couponid) AS coupon_count
                FROM missions m
                LEFT JOIN coupons c 
                       ON c.missionid = m.missionid
                      AND c.companyid = m.companyid
                WHERE m.companyid = @companyId
                GROUP BY m.missionid, m.name, m.locationname
                ORDER BY m.missionid;";

            await using var cmd = new NpgsqlCommand(sql, con);
            cmd.Parameters.AddWithValue("companyId", companyId);

            var missions = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync();

            while (await r.ReadAsync())
            {
                missions.Add(new
                {
                    missionId = r.GetInt32(0),
                    name = r.IsDBNull(1) ? "" : r.GetString(1),
                    locationName = r.IsDBNull(2) ? "" : r.GetString(2),
                    couponCount = r.GetInt64(3)    // COUNT(*) is bigint
                });
            }

            return Ok(new { missions });
        }


        [HttpPatch("missions/{missionId:int}/toggle-status")]
        public async Task<IActionResult> ToggleMissionStatus(int missionId)
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();

            const string sql = @"
        UPDATE missions
        SET status = CASE
            WHEN status = 'Active' THEN 'Closed'
            ELSE 'Active'
        END
        WHERE missionid = @missionId
          AND companyid = @companyId
        RETURNING status;";

            await using var cmd = new NpgsqlCommand(sql, con);
            cmd.Parameters.AddWithValue("missionId", missionId);
            cmd.Parameters.AddWithValue("companyId", companyId);

            var newStatus = await cmd.ExecuteScalarAsync();

            if (newStatus == null)
                return NotFound(new { error = "Mission not found for this company." });

            return Ok(new
            {
                missionId,
                status = newStatus.ToString()
            });
        }


        [HttpDelete("missions/{missionId:int}")]
        public async Task<IActionResult> DeleteMission(int missionId)
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();

            const string sql = @"
            DELETE FROM missions
            WHERE missionid = @missionId
            AND companyid = @companyId;";

            await using var cmd = new NpgsqlCommand(sql, con);
            cmd.Parameters.AddWithValue("missionId", missionId);
            cmd.Parameters.AddWithValue("companyId", companyId);

            var affected = await cmd.ExecuteNonQueryAsync();

            if (affected == 0)
                return NotFound(new { error = "Mission not found for this company." });

            return Ok(new { deleted = true, missionId });
        }


        // -------------------------------
        // GET /api/admin/coupons     - CouponDeleter and Location Getter
        // -------------------------------
        [HttpDelete("coupons/{couponId:int}")]
        public async Task<IActionResult> DeleteCoupon(int couponId)
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();
            await using var tx = await con.BeginTransactionAsync();

            // 1️⃣ Get the mission this coupon belongs to
            int? missionId;
            const string getMissionSql = @"
        SELECT missionid
        FROM coupons
        WHERE couponid = @couponId
          AND companyid = @companyId;";

            await using (var getCmd = new NpgsqlCommand(getMissionSql, con, tx))
            {
                getCmd.Parameters.AddWithValue("couponId", couponId);
                getCmd.Parameters.AddWithValue("companyId", companyId);

                missionId = (int?)await getCmd.ExecuteScalarAsync();
            }

            if (missionId == null)
                return NotFound(new { error = "Coupon not found for this company." });

            // 2️⃣ Delete the coupon
            const string deleteSql = @"
        DELETE FROM coupons
        WHERE couponid = @couponId
          AND companyid = @companyId;";

            await using (var delCmd = new NpgsqlCommand(deleteSql, con, tx))
            {
                delCmd.Parameters.AddWithValue("couponId", couponId);
                delCmd.Parameters.AddWithValue("companyId", companyId);
                await delCmd.ExecuteNonQueryAsync();
            }

            // 3️⃣ Decrement mission coupon total (never below 0)
            const string updateMissionSql = @"
            UPDATE missions
            SET coupontotal = GREATEST(coupontotal - 1, 0)
            WHERE missionid = @missionId
            AND companyid = @companyId;";

            await using (var updCmd = new NpgsqlCommand(updateMissionSql, con, tx))
            {
                updCmd.Parameters.AddWithValue("missionId", missionId.Value);
                updCmd.Parameters.AddWithValue("companyId", companyId);
                await updCmd.ExecuteNonQueryAsync();
            }

            await tx.CommitAsync();

            return Ok(new { deleted = true, couponId });
        }


        public sealed class CouponLocationDto
        {
            public double Latitude { get; set; }
            public double Longitude { get; set; }
            public string LocationName { get; set; } = "";
        }

        [HttpGet("coupons/{couponId:int}/locations")]
        public async Task<IActionResult> GetLocationsForCoupon(int couponId)
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();

            const string sql = @"
            SELECT 
            l.latitude,
            l.longitude,
            COALESCE(l.""LocationName"", '') AS locationname
            FROM location l
            JOIN coupons c ON c.couponid = l.couponid
            WHERE l.couponid = @couponId
            AND c.companyid = @companyId
            ORDER BY l.locationid;";

            await using var cmd = new NpgsqlCommand(sql, con);
            cmd.Parameters.AddWithValue("couponId", couponId);
            cmd.Parameters.AddWithValue("companyId", companyId);

            var locations = new List<CouponLocationDto>();

            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                locations.Add(new CouponLocationDto
                {
                    Latitude = r.GetDouble(0),
                    Longitude = r.GetDouble(1),
                    LocationName = r.GetString(2)
                });
            }

            return Ok(new { locations });
        }

    }
}
