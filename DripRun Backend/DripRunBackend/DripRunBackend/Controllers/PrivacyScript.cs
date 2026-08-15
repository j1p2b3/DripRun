using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace DripRunBackend.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/user")]
    public class PrivacyScript : ControllerBase
    {
        private readonly string _connectionString;
        private readonly ILogger<PrivacyScript> _logger;

        public PrivacyScript(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        // GET: api/user/privacy
        [HttpGet("privacy")]
        public async Task<IActionResult> GetPrivacyStatus()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
            {
                return Unauthorized();
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var command = new NpgsqlCommand("SELECT ispublic FROM users WHERE userid = @UserId", connection);
            command.Parameters.AddWithValue("UserId", userId);

            var result = await command.ExecuteScalarAsync();
            if (result is bool isPublic)
            {
                // Use lowercase property name to match Unity JsonUtility expectations
                return Ok(new { isPublic });
            }

            return NotFound();
        }

        // POST: api/user/privacy
        [HttpPost("privacy")]
        public async Task<IActionResult> UpdatePrivacyStatus([FromBody] PrivacyUpdateRequest request)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
            {
                return Unauthorized();
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var command = new NpgsqlCommand("UPDATE users SET ispublic = @IsPublic WHERE userid = @UserId", connection);
            command.Parameters.AddWithValue("IsPublic", request.isPublic);
            command.Parameters.AddWithValue("UserId", userId);

            int affectedRows = await command.ExecuteNonQueryAsync();

            if (affectedRows > 0)
            {
                return Ok(new { success = true });
            }

            return BadRequest(new { success = false });
        }

        [HttpDelete("delete")]
        public async Task<IActionResult> DeleteAccount()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();

            try
            {
                // 1) Revoke/delete ALL refresh tokens for this user (covers every device)
                // TODO: Replace table/column names with your actual refresh storage.
                var revokeRefresh = new NpgsqlCommand(
                    "DELETE FROM refresh_tokens WHERE user_id = @UserId", connection, tx);
                revokeRefresh.Parameters.AddWithValue("UserId", userId);
                await revokeRefresh.ExecuteNonQueryAsync();

                // 2) Delete dependent data (or rely on ON DELETE CASCADE)
                var deleteUserCoupons = new NpgsqlCommand(
                    "DELETE FROM user_coupon WHERE userid = @UserId", connection, tx);
                deleteUserCoupons.Parameters.AddWithValue("UserId", userId);
                await deleteUserCoupons.ExecuteNonQueryAsync();

                // 3) Delete the user
                var deleteUser = new NpgsqlCommand(
                    "DELETE FROM users WHERE userid = @UserId", connection, tx);
                deleteUser.Parameters.AddWithValue("UserId", userId);
                var rows = await deleteUser.ExecuteNonQueryAsync();

                await tx.CommitAsync();

                if (rows <= 0) return BadRequest(new { success = false });

                // 4) Clear WebGL refresh cookie (mobile ignores this header)
                ClearRefreshCookie();

                return NoContent(); // 204
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                _logger.LogError(ex, "DeleteAccount failed for userId={UserId}", userId);
                return StatusCode(500, new { error = "Server error" });
            }
        }

        private void ClearRefreshCookie()
        {
            Response.Cookies.Append("refresh", "", new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = "/",
                Expires = DateTimeOffset.UnixEpoch
                // If you set Domain when creating the cookie, set the same Domain here too.
                // Domain = "yourdomain.com"
            });
        }

        // GET: api/user/has-age
        [HttpGet("has-age")]
        public async Task<IActionResult> HasAge()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Must use quotes because column is case-sensitive
            using var command = new NpgsqlCommand(
                "SELECT \"AgeRange\" IS NOT NULL FROM users WHERE userid = @UserId",
                connection);
            command.Parameters.AddWithValue("UserId", userId);

            var result = await command.ExecuteScalarAsync();

            if (result is bool hasAge)
                return Ok(new { hasAge });

            return Ok(new { hasAge = false });
        }

        // POST: api/user/details
        [HttpPost("details")]
        public async Task<IActionResult> UpdateUserDetails([FromBody] UserDetailsDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);

            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var command = new NpgsqlCommand(@"
        UPDATE users
        SET ""AgeRange"" = @AgeRange,
            ""Gender"" = @Gender
        WHERE userid = @UserId", connection);

            // Handle null/whitespace safely
            if (string.IsNullOrWhiteSpace(dto.AgeRange))
                command.Parameters.AddWithValue("AgeRange", DBNull.Value);
            else
                command.Parameters.AddWithValue("AgeRange", dto.AgeRange.Trim());

            if (string.IsNullOrWhiteSpace(dto.Gender))
                command.Parameters.AddWithValue("Gender", DBNull.Value);
            else
                command.Parameters.AddWithValue("Gender", dto.Gender.Trim());

            command.Parameters.AddWithValue("UserId", userId);

            int rows = await command.ExecuteNonQueryAsync();

            if (rows > 0)
                return Ok(new { success = true });

            return BadRequest(new { success = false });
        }

        // DTO for request body
        public class UserDetailsDto
        {
            public string? AgeRange { get; set; }
            public string? Gender { get; set; }
        }

        public class PrivacyUpdateRequest
        {
            public bool isPublic { get; set; }  // Lowercase to match Unity frontend
        }
    }
}