using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Google.Apis.Auth;
using Npgsql;

namespace DripRunBackend.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class GoogleAuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly HttpClient _httpClient;
        private readonly string _googleClientId;
        private readonly string _jwtSecret;
        private readonly string _jwtIssuer;
        private readonly string _jwtAudience;

        // NEW: refresh config (match AppAuthController)
        private readonly string _refreshPepper;
        private readonly int _refreshDays;
        private const int AccessMinutes = 60;

        public GoogleAuthController(IConfiguration configuration, HttpClient httpClient)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _httpClient = httpClient;
            _googleClientId = configuration["Google:ClientId"];
            _jwtSecret = configuration["JWT_SECRET"];
            _jwtIssuer = configuration["JWT_ISSUER"];
            _jwtAudience = configuration["JWT_AUDIENCE"];

            // NEW
            _refreshPepper = configuration["Refresh:Pepper"];
            _refreshDays = int.TryParse(configuration["Refresh:Days"], out var d) ? d : 60;
        }


        [AllowAnonymous]
        [HttpPost("google-login-1.5")]
        public async Task<IActionResult> GoogleLoginV15([FromBody] GoogleTokenRequest tokenRequest)
        {
            if (string.IsNullOrWhiteSpace(_refreshPepper))
                return StatusCode(500, new { error = "Server configuration error (missing refresh pepper)" });

            var googlePayload = await VerifyGoogleToken(tokenRequest?.IdToken);
            if (googlePayload == null)
                return Unauthorized(new { error = "Invalid Google ID Token" });

            string email = googlePayload.Email;
            string name = string.IsNullOrWhiteSpace(googlePayload.Name) ? "Google User" : googlePayload.Name.Trim();

            int userId;
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Find or create user
            await using (var cmd = new NpgsqlCommand("SELECT userid, name FROM users WHERE email=@e", connection))
            {
                cmd.Parameters.AddWithValue("e", email);
                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    userId = r.GetInt32(0);
                    var storedName = r.IsDBNull(1) ? null : r.GetString(1);
                    if (!string.IsNullOrWhiteSpace(storedName) && storedName != "Google User")
                        name = storedName;
                }
                else
                {
                    await r.DisposeAsync();
                    await using var ins = new NpgsqlCommand(@"
                        INSERT INTO users (email, name, created_at)
                        VALUES (@e, @n, @ts)
                        RETURNING userid", connection);
                    ins.Parameters.AddWithValue("e", email);
                    ins.Parameters.AddWithValue("n", name);
                    ins.Parameters.AddWithValue("ts", DateTime.UtcNow);
                    userId = (int)await ins.ExecuteScalarAsync();
                }
            }

            // Access JWT (same expiry as AppAuthController)
            var accessJwt = GenerateAccessJwt(userId, email);

            // NEW: issue refresh (same table/pepper/duration as AppAuthController)
            var (refreshPlain, _) = await IssueRefreshAsync(connection, userId);

            return Ok(new { Email = email, Name = name, JwtToken = accessJwt, RefreshToken = refreshPlain });
        }


        [AllowAnonymous]
        [HttpPost("google-login-web-1.5")]
        public async Task<IActionResult> GoogleLoginWebV15([FromBody] GoogleTokenRequest tokenRequest)
        {
            if (string.IsNullOrWhiteSpace(_refreshPepper))
                return StatusCode(500, new { error = "Server configuration error (missing refresh pepper)" });

            var googlePayload = await VerifyGoogleToken(tokenRequest?.IdToken);
            if (googlePayload == null)
                return Unauthorized(new { error = "Invalid Google ID Token" });

            var email = googlePayload.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email))
                return Unauthorized(new { error = "Google token missing email" });

            var name = string.IsNullOrWhiteSpace(googlePayload.Name) ? "Google User" : googlePayload.Name.Trim();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 1) Resolve or create user WITHOUT leaving a reader open
            int userId = -1;
            string? storedName = null;

            await using (var cmd = new NpgsqlCommand("SELECT userid, name FROM users WHERE email=@e", connection))
            {
                cmd.Parameters.AddWithValue("e", email);
                await using var r = await cmd.ExecuteReaderAsync();

                if (await r.ReadAsync())
                {
                    userId = r.GetInt32(0);
                    storedName = r.IsDBNull(1) ? null : r.GetString(1);
                }
            }

            if (userId > 0)
            {
                // Prefer existing non-placeholder name if present
                if (!string.IsNullOrWhiteSpace(storedName) && storedName != "Google User")
                    name = storedName!;
            }
            else
            {
                await using var ins = new NpgsqlCommand(@"
            INSERT INTO users (email, name, created_at)
            VALUES (@e, @n, @ts)
            RETURNING userid", connection);

                ins.Parameters.AddWithValue("e", email);
                ins.Parameters.AddWithValue("n", name);
                ins.Parameters.AddWithValue("ts", DateTime.UtcNow);

                userId = Convert.ToInt32(await ins.ExecuteScalarAsync());
            }

            if (userId <= 0)
                return StatusCode(500, new { error = "Failed to resolve user id" });

            // 2) Issue access + refresh
            var accessJwt = GenerateAccessJwt(userId, email);
            var (refreshPlain, _) = await IssueRefreshAsync(connection, userId);

            // 3) Set HttpOnly refresh cookie for WebGL (do NOT return refresh in body)
            Response.Cookies.Append("refresh", refreshPlain, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,                 // required for SameSite=None in modern browsers
                SameSite = SameSiteMode.None,  // needed if WebGL site is cross-origin to API
                Path = "/",
                MaxAge = TimeSpan.FromDays(_refreshDays),
                // If (and only if) you set Domain when creating the cookie, set the same Domain here.
                // Domain = "yourdomain.com",
            });

            return Ok(new { Email = email, Name = name, JwtToken = accessJwt });
        }



        // --- Google token verify ---
        private async Task<GoogleJsonWebSignature.Payload?> VerifyGoogleToken(string idToken)
        {
            if (string.IsNullOrWhiteSpace(idToken)) return null;
            try
            {
                var settings = new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { _googleClientId } };
                return await GoogleJsonWebSignature.ValidateAsync(idToken, settings);
            }
            catch { return null; }
        }

        // --- Access JWT (mirror AppAuthController) ---
        private string GenerateAccessJwt(int userId, string email)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
            var claims = new[] {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email)
            };
            var token = new JwtSecurityToken(
                _jwtIssuer, _jwtAudience, claims,
                expires: DateTime.UtcNow.AddMinutes(AccessMinutes),
                signingCredentials: creds);
            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // --- Minimal refresh helpers (compatible with AppAuthController schema) ---
        private static string GenerateRandomBase64Url(int bytesLen = 48)
        {
            var bytes = RandomNumberGenerator.GetBytes(bytesLen);
            return Base64UrlEncoder.Encode(bytes);
        }

        private string HashRefresh(string plaintext)
        {
            using var sha = SHA256.Create();
            var data = Encoding.UTF8.GetBytes(plaintext + _refreshPepper);
            return Convert.ToHexString(sha.ComputeHash(data));
        }

        private async Task<(string refreshPlain, Guid familyId)> IssueRefreshAsync(NpgsqlConnection conn, int userId, Guid? existingFamily = null)
        {
            var family = existingFamily ?? Guid.NewGuid();
            var refreshPlain = GenerateRandomBase64Url();
            var hash = HashRefresh(refreshPlain);
            var expires = DateTime.UtcNow.AddDays(_refreshDays);

            await using var cmd = new NpgsqlCommand(@"
                INSERT INTO refresh_tokens (user_id, family_id, token_hash, expires_utc)
                VALUES (@u, @f, @h, @exp)", conn);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("f", family);
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("exp", expires);
            await cmd.ExecuteNonQueryAsync();

            return (refreshPlain, family);
        }

        public class GoogleTokenRequest { public string IdToken { get; set; } }
    }
}
