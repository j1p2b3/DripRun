using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace DripRunBackend.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class OnceADayCouponController : ControllerBase
    {
        private readonly string _connectionString;

        public OnceADayCouponController(IConfiguration configuration)
        {
            // Update the key if your appsettings.json uses a different name
            _connectionString = configuration.GetConnectionString("DefaultConnection")!;
        }

        // Helper: pull your integer user id from JWT claims
        private int? GetUserIdFromToken()
        {
            var idClaim =
                User.FindFirst(ClaimTypes.NameIdentifier) ??
                User.FindFirst("userid") ??
                User.FindFirst("user_id");

            if (idClaim == null) return null;
            return int.TryParse(idClaim.Value, out var id) ? id : null;
        }


        [HttpPost("daily-claim-v1")]
        public async Task<IActionResult> DailyClaimV1()
        {
            int? userId = GetUserIdFromToken();
            if (userId is null) return Unauthorized();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // Serialize per-user so double-taps / retries can't race
                await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k);", conn, tx))
                {
                    lockCmd.Parameters.AddWithValue("@k", userId.Value);
                    await lockCmd.ExecuteNonQueryAsync();
                }

                // 1) Compute Sydney today + next date (server authoritative)
                DateTime todayLocal;
                DateTime nextLocal;
                await using (var dateCmd = new NpgsqlCommand(@"
                SELECT (timezone('Australia/Sydney', now()))::date AS today_local,
                       ((timezone('Australia/Sydney', now()))::date + 1) AS next_local;
            ", conn, tx))
                {
                    await using var r = await dateCmd.ExecuteReaderAsync();
                    await r.ReadAsync();
                    todayLocal = (DateTime)r["today_local"];
                    nextLocal = (DateTime)r["next_local"];
                }

                // 2) Cooldown check (uses users.last_claimed_local_date)
                DateTime? lastClaimedLocal = null;
                await using (var lastCmd = new NpgsqlCommand(@"
                SELECT last_claimed_local_date
                FROM users
                WHERE userid = @uid;
            ", conn, tx))
                {
                    lastCmd.Parameters.AddWithValue("@uid", userId.Value);
                    var x = await lastCmd.ExecuteScalarAsync();
                    lastClaimedLocal = (x == null || x is DBNull) ? (DateTime?)null : (DateTime)x;
                }

                if (lastClaimedLocal.HasValue && lastClaimedLocal.Value >= todayLocal)
                {
                    await tx.RollbackAsync();
                    return Conflict(new { failed = true, nextDateLocal = nextLocal.ToString("yyyy-MM-dd") });
                }

                // 3) Pick one eligible NOT-owned coupon (same rule as your query)
                int? couponId = null;
                await using (var rewardCmd = new NpgsqlCommand(@"
                SELECT l.couponid
                FROM location l
                JOIN coupons c ON c.couponid = l.couponid
                WHERE c.couponlevel >= 1
                AND c.couponlevel <= 94
                  AND NOT EXISTS (
                    SELECT 1
                    FROM user_coupon uc
                    WHERE uc.userid = @uid
                      AND uc.couponid = l.couponid
                  )
                ORDER BY random()
                LIMIT 1;
            ", conn, tx))
                {
                    rewardCmd.Parameters.AddWithValue("@uid", userId.Value);
                    var x = await rewardCmd.ExecuteScalarAsync();
                    couponId = (x == null || x is DBNull) ? (int?)null : Convert.ToInt32(x);
                }

                if (!couponId.HasValue)
                {
                    await tx.RollbackAsync();
                    // No eligible coupons left for this user
                    return Conflict(new { failed = true, nextDateLocal = todayLocal.ToString("yyyy-MM-dd") });
                }

                // 4) Insert the daily claim (server timestamps + claimedviadaily=true)
                await using (var insCmd = new NpgsqlCommand(@"
                INSERT INTO user_coupon (userid, couponid, dateearned, claimedat, claimedviadaily)
                VALUES (@uid, @cid, now(), now(), true)
                ON CONFLICT (userid, couponid) DO NOTHING
                RETURNING 1;
            ", conn, tx))
                {
                    insCmd.Parameters.AddWithValue("@uid", userId.Value);
                    insCmd.Parameters.AddWithValue("@cid", couponId.Value);

                    var inserted = await insCmd.ExecuteScalarAsync();
                    if (inserted == null)
                    {
                        // Extremely rare (race / already owned) – treat as failure for today
                        await tx.RollbackAsync();
                        return Conflict(new { failed = true, nextDateLocal = nextLocal.ToString("yyyy-MM-dd") });
                    }
                }

                // 5) Update cooldown date AFTER successful insert
                await using (var updCmd = new NpgsqlCommand(@"
                UPDATE users
                SET last_claimed_local_date = @today
                WHERE userid = @uid;
            ", conn, tx))
                {
                    updCmd.Parameters.AddWithValue("@today", todayLocal);
                    updCmd.Parameters.AddWithValue("@uid", userId.Value);
                    await updCmd.ExecuteNonQueryAsync();
                }

                // 6) Pick one locationId for AR scene (ID only)
                int? locationId = null;
                await using (var locCmd = new NpgsqlCommand(@"
                SELECT locationid
                FROM location
                WHERE couponid = @cid
                ORDER BY random()
                LIMIT 1;
            ", conn, tx))
                {
                    locCmd.Parameters.AddWithValue("@cid", couponId.Value);
                    var x = await locCmd.ExecuteScalarAsync();
                    locationId = (x == null || x is DBNull) ? (int?)null : Convert.ToInt32(x);
                }

                await tx.CommitAsync();

                return Ok(new
                {
                    couponId = couponId.Value,
                    locationId,
                    nextDateLocal = nextLocal.ToString("yyyy-MM-dd")
                });
            }
            catch (PostgresException pgEx)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { error = pgEx.MessageText, code = pgEx.SqlState });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { error = ex.Message });
            }
        }

        [HttpPost("daily-claim-v2")]
        public async Task<IActionResult> DailyClaimV2()
        {
            int? userId = GetUserIdFromToken();
            if (userId is null) return Unauthorized();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();

            try
            {
                // Serialize per-user so double-taps / retries can't race
                await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k);", conn, tx))
                {
                    lockCmd.Parameters.AddWithValue("@k", userId.Value);
                    await lockCmd.ExecuteNonQueryAsync();
                }

                // 1) Compute Sydney today + next date (server authoritative)
                DateTime todayLocal;
                DateTime nextLocal;
                await using (var dateCmd = new NpgsqlCommand(@"
                SELECT (timezone('Australia/Sydney', now()))::date AS today_local,
                       ((timezone('Australia/Sydney', now()))::date + 1) AS next_local;
            ", conn, tx))
                {
                    await using var r = await dateCmd.ExecuteReaderAsync();
                    await r.ReadAsync();
                    todayLocal = (DateTime)r["today_local"];
                    nextLocal = (DateTime)r["next_local"];
                }

                // 2) Cooldown check (uses users.last_claimed_local_date)
                DateTime? lastClaimedLocal = null;
                await using (var lastCmd = new NpgsqlCommand(@"
                SELECT last_claimed_local_date
                FROM users
                WHERE userid = @uid;
            ", conn, tx))
                {
                    lastCmd.Parameters.AddWithValue("@uid", userId.Value);
                    var x = await lastCmd.ExecuteScalarAsync();
                    lastClaimedLocal = (x == null || x is DBNull) ? (DateTime?)null : (DateTime)x;
                }

                if (lastClaimedLocal.HasValue && lastClaimedLocal.Value >= todayLocal)
                {
                    await tx.RollbackAsync();
                    return Conflict(new { failed = true, nextDateLocal = nextLocal.ToString("yyyy-MM-dd") });
                }

                // 3) Pick one eligible NOT-owned coupon (same rule as your query)
                int? couponId = null;
                await using (var rewardCmd = new NpgsqlCommand(@"
                SELECT DISTINCT l.couponid
                FROM location l
                JOIN coupons c ON c.couponid = l.couponid
                WHERE c.couponlevel >= 1
                AND c.couponlevel <= 94

                  -- User does NOT already own it
                  AND NOT EXISTS (
                    SELECT 1
                    FROM user_coupon uc
                    WHERE uc.userid = @uid
                      AND uc.couponid = l.couponid
                  )

                  -- User HAS viewed / impressed this coupon before
                  AND EXISTS (
                    SELECT 1
                    FROM userbrandimpressions ubi
                    WHERE ubi.userid = @uid
                      AND ubi.couponid = l.couponid
                  )

                ORDER BY random()
                LIMIT 1;
                ", conn, tx))
                {
                    rewardCmd.Parameters.AddWithValue("@uid", userId.Value);
                    var x = await rewardCmd.ExecuteScalarAsync();
                    couponId = (x == null || x is DBNull) ? (int?)null : Convert.ToInt32(x);
                }

                if (!couponId.HasValue)
                {
                    await tx.RollbackAsync();
                    // No eligible coupons left for this user
                    return Conflict(new { failed = true, nextDateLocal = todayLocal.ToString("yyyy-MM-dd") });
                }

                // 4) Insert the daily claim (server timestamps + claimedviadaily=true)
                await using (var insCmd = new NpgsqlCommand(@"
                INSERT INTO user_coupon (userid, couponid, dateearned, claimedat, claimedviadaily)
                VALUES (@uid, @cid, now(), now(), true)
                ON CONFLICT (userid, couponid) DO NOTHING
                RETURNING 1;
            ", conn, tx))
                {
                    insCmd.Parameters.AddWithValue("@uid", userId.Value);
                    insCmd.Parameters.AddWithValue("@cid", couponId.Value);

                    var inserted = await insCmd.ExecuteScalarAsync();
                    if (inserted == null)
                    {
                        // Extremely rare (race / already owned) – treat as failure for today
                        await tx.RollbackAsync();
                        return Conflict(new { failed = true, nextDateLocal = nextLocal.ToString("yyyy-MM-dd") });
                    }
                }

                // 5) Update cooldown date AFTER successful insert
                await using (var updCmd = new NpgsqlCommand(@"
                UPDATE users
                SET last_claimed_local_date = @today
                WHERE userid = @uid;
            ", conn, tx))
                {
                    updCmd.Parameters.AddWithValue("@today", todayLocal);
                    updCmd.Parameters.AddWithValue("@uid", userId.Value);
                    await updCmd.ExecuteNonQueryAsync();
                }

                // 6) Pick one locationId for AR scene (ID only)
                int? locationId = null;
                await using (var locCmd = new NpgsqlCommand(@"
                SELECT locationid
                FROM location
                WHERE couponid = @cid
                ORDER BY random()
                LIMIT 1;
            ", conn, tx))
                {
                    locCmd.Parameters.AddWithValue("@cid", couponId.Value);
                    var x = await locCmd.ExecuteScalarAsync();
                    locationId = (x == null || x is DBNull) ? (int?)null : Convert.ToInt32(x);
                }

                await tx.CommitAsync();

                return Ok(new
                {
                    couponId = couponId.Value,
                    locationId,
                    nextDateLocal = nextLocal.ToString("yyyy-MM-dd")
                });
            }
            catch (PostgresException pgEx)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { error = pgEx.MessageText, code = pgEx.SqlState });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return StatusCode(500, new { error = ex.Message });
            }
        }


    }
}

