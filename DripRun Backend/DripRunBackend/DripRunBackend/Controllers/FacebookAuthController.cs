using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using System.Net.Http;
using System.Threading.Tasks;
using Npgsql;
using Microsoft.AspNetCore.Authorization;
using System;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Web;

namespace DripRunBackend.Controllers
{
    [Route("api/auth")]
    [ApiController]
    public class FacebookAuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly HttpClient _httpClient;
        private readonly string _facebookAppId;
        private readonly string _facebookAppSecret;
        private readonly string _jwtSecret;
        private readonly string _jwtIssuer;
        private readonly string _jwtAudience;

        // NEW: refresh config (match AppAuthController)
        private readonly string _refreshPepper;      // NEW
        private readonly int _refreshDays;           // NEW
        private const int AccessMinutes = 60;        // NEW

        // Pin a Graph API version for stability
        private const string GraphBase = "https://graph.facebook.com/v19.0";
        private const string FacebookDebugTokenUrl = $"{GraphBase}/debug_token";
        private const string FacebookGraphMeUrl = $"{GraphBase}/me?fields=id,name,email";

        public FacebookAuthController(IConfiguration configuration, HttpClient httpClient)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _httpClient = httpClient;
            _facebookAppId = configuration["Facebook:AppId"];
            _facebookAppSecret = configuration["Facebook:AppSecret"];
            _jwtSecret = configuration["JWT_SECRET"];
            _jwtIssuer = configuration["JWT_ISSUER"];
            _jwtAudience = configuration["JWT_AUDIENCE"];

            // NEW
            _refreshPepper = configuration["Refresh:Pepper"];
            _refreshDays = int.TryParse(configuration["Refresh:Days"], out var d) ? d : 60;
        }


        [AllowAnonymous]
        [HttpPost("facebook-login-1.5")]
        public async Task<IActionResult> FacebookLoginV15([FromBody] FacebookTokenRequest tokenRequest)
        {
            if (tokenRequest == null || string.IsNullOrWhiteSpace(tokenRequest.AccessToken))
                return BadRequest(new { error = "Invalid token request" });

            if (string.IsNullOrWhiteSpace(_refreshPepper))
                return StatusCode(500, new { error = "Server configuration error (missing refresh pepper)" }); // NEW

            var accessToken = tokenRequest.AccessToken.Trim();

            // Detect Limited Login tokens (JWT-like with dots) early, and fail fast with a helpful message.
            if (accessToken.Contains("."))
                return Unauthorized(new { error = "This looks like a Limited Login token. Use classic login (FB.LogInWithReadPermissions) to obtain a Graph API access token." });

            // 1) Verify with /debug_token
            bool isValid = await VerifyFacebookToken(accessToken);
            if (!isValid)
                return Unauthorized(new { error = "Invalid or expired Facebook Access Token" });

            // 2) Retrieve user with appsecret_proof
            var facebookUser = await GetFacebookUserInfo(accessToken);
            if (facebookUser == null)
                return Unauthorized(new { error = "Failed to retrieve Facebook user info" });

            if (string.IsNullOrWhiteSpace(facebookUser.Email))
                return Unauthorized(new { error = "Facebook account has no email or permission not granted" });

            string email = facebookUser.Email;
            string name = facebookUser.Name ?? "";

            int userId;
            await using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                // Try find user
                userId = await GetUserIdByEmail(connection, email);

                if (userId == 0)
                {
                    // Insert new user
                    const string insertQuery = @"
                        INSERT INTO users (email, name, created_at)
                        VALUES (@Email, @Name, @CreatedAt)
                        RETURNING userid;";
                    await using var insertCmd = new NpgsqlCommand(insertQuery, connection);
                    insertCmd.Parameters.AddWithValue("Email", email);
                    insertCmd.Parameters.AddWithValue("Name", name);
                    insertCmd.Parameters.AddWithValue("CreatedAt", DateTime.UtcNow);
                    userId = (int)await insertCmd.ExecuteScalarAsync();
                }
                else
                {
                    // Optional: keep name fresh
                    const string updateName = @"UPDATE users SET name = @Name WHERE userid = @UserId AND (name IS DISTINCT FROM @Name);";
                    await using var upCmd = new NpgsqlCommand(updateName, connection);
                    upCmd.Parameters.AddWithValue("Name", name);
                    upCmd.Parameters.AddWithValue("UserId", userId);
                    await upCmd.ExecuteNonQueryAsync();
                }

                // 3) Issue JWT
                string jwtToken = GenerateJwtToken(userId, email);

                // NEW: Issue our refresh token (same table/pepper/duration as AppAuthController)
                var (refreshPlain, _) = await IssueRefreshAsync(connection, userId); // NEW

                // IMPORTANT: return keys in lowercase to match your Unity AuthResponse
                return Ok(new { email = email, name = name, userId = userId, jwtToken = jwtToken, refreshToken = refreshPlain }); // NEW
            }
        }

        [AllowAnonymous]
        [HttpPost("facebook-login-web-1.5")]
        public async Task<IActionResult> FacebookLoginWebV15([FromBody] FacebookTokenRequest tokenRequest)
        {
            if (tokenRequest == null || string.IsNullOrWhiteSpace(tokenRequest.AccessToken))
                return BadRequest(new { error = "Invalid token request" });

            if (string.IsNullOrWhiteSpace(_refreshPepper))
                return StatusCode(500, new { error = "Server configuration error (missing refresh pepper)" });

            var accessToken = tokenRequest.AccessToken.Trim();

            // Limited Login tokens (JWT-like) won't work with Graph /debug_token
            if (accessToken.Contains("."))
                return Unauthorized(new { error = "This looks like a Limited Login token. Use classic login (Graph API access token) for this endpoint." });

            // 1) Verify with /debug_token
            bool isValid = await VerifyFacebookToken(accessToken);
            if (!isValid)
                return Unauthorized(new { error = "Invalid or expired Facebook Access Token" });

            // 2) Retrieve user info
            var facebookUser = await GetFacebookUserInfo(accessToken);
            if (facebookUser == null)
                return Unauthorized(new { error = "Failed to retrieve Facebook user info" });

            var email = facebookUser.Email?.Trim();
            if (string.IsNullOrWhiteSpace(email))
                return Unauthorized(new { error = "Facebook account has no email or permission not granted" });

            var name = (facebookUser.Name ?? "").Trim();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // 3) Find or create user (safe pattern)
            int userId = await GetUserIdByEmail(connection, email);

            if (userId == 0)
            {
                const string insertQuery = @"
            INSERT INTO users (email, name, created_at)
            VALUES (@Email, @Name, @CreatedAt)
            RETURNING userid;";
                await using var insertCmd = new NpgsqlCommand(insertQuery, connection);
                insertCmd.Parameters.AddWithValue("Email", email);
                insertCmd.Parameters.AddWithValue("Name", string.IsNullOrWhiteSpace(name) ? "Facebook User" : name);
                insertCmd.Parameters.AddWithValue("CreatedAt", DateTime.UtcNow);

                userId = Convert.ToInt32(await insertCmd.ExecuteScalarAsync());
            }
            else
            {
                // Optional: keep name fresh (avoid writing empty names)
                if (!string.IsNullOrWhiteSpace(name))
                {
                    const string updateName = @"
                UPDATE users
                SET name = @Name
                WHERE userid = @UserId AND (name IS DISTINCT FROM @Name);";
                    await using var upCmd = new NpgsqlCommand(updateName, connection);
                    upCmd.Parameters.AddWithValue("Name", name);
                    upCmd.Parameters.AddWithValue("UserId", userId);
                    await upCmd.ExecuteNonQueryAsync();
                }
            }

            if (userId <= 0)
                return StatusCode(500, new { error = "Failed to resolve user id" });

            // 4) Issue access JWT
            string jwtToken = GenerateJwtToken(userId, email);

            // 5) Issue refresh token (server-side)
            var (refreshPlain, _) = await IssueRefreshAsync(connection, userId);

            // 6) Set HttpOnly refresh cookie for WebGL
            Response.Cookies.Append("refresh", refreshPlain, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = "/",
                MaxAge = TimeSpan.FromDays(_refreshDays),
                // Domain = "..."; // only if you set it everywhere consistently
            });

            // 7) Do NOT return refresh token in body for web
            return Ok(new
            {
                email,
                name,
                userId,
                jwtToken
                // refreshToken intentionally omitted for web
            });
        }

        private async Task<bool> VerifyFacebookToken(string accessToken)
        {
            // Use app access token (app_id|app_secret) to inspect the user token
            var appAccess = $"{_facebookAppId}|{_facebookAppSecret}";
            var verificationUrl = $"{FacebookDebugTokenUrl}?input_token={HttpUtility.UrlEncode(accessToken)}&access_token={HttpUtility.UrlEncode(appAccess)}";

            using var response = await _httpClient.GetAsync(verificationUrl);
            if (!response.IsSuccessStatusCode) return false;

            var responseBody = await response.Content.ReadAsStringAsync();
            var tokenInfo = JsonConvert.DeserializeObject<FacebookTokenDebugEnvelope>(responseBody);

            var data = tokenInfo?.Data;
            if (data == null) return false;
            if (!data.IsValid) return false;
            if (!string.Equals(data.AppId, _facebookAppId, StringComparison.Ordinal)) return false;
            if (data.ExpiresAt > 0 && data.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

            return true;
        }

        private async Task<FacebookUserInfo> GetFacebookUserInfo(string accessToken)
        {
            // appsecret_proof = HMAC_SHA256(access_token, app_secret)
            var appsecret_proof = ComputeAppSecretProof(accessToken, _facebookAppSecret);

            var url = $"{FacebookGraphMeUrl}&access_token={HttpUtility.UrlEncode(accessToken)}&appsecret_proof={appsecret_proof}";
            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<FacebookUserInfo>(body);
        }

        private static string ComputeAppSecretProof(string accessToken, string appSecret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(accessToken));
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private async Task<int> GetUserIdByEmail(NpgsqlConnection connection, string email)
        {
            const string query = "SELECT userid FROM users WHERE email = @Email LIMIT 1;";
            await using var cmd = new NpgsqlCommand(query, connection);
            cmd.Parameters.AddWithValue("Email", email);
            var obj = await cmd.ExecuteScalarAsync();
            return obj == null || obj is DBNull ? 0 : Convert.ToInt32(obj);
        }

        private string GenerateJwtToken(int userId, string email)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email)
            };

            var token = new JwtSecurityToken(
                issuer: _jwtIssuer,
                audience: _jwtAudience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(AccessMinutes), // NEW (same window as others)
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // ===== NEW: minimal refresh helpers (compatible with AppAuthController) =====

        private static string GenerateRandomBase64Url(int bytesLen = 48) // NEW
        {
            var bytes = RandomNumberGenerator.GetBytes(bytesLen);
            return Base64UrlEncoder.Encode(bytes);
        }

        private string HashRefresh(string plaintext) // NEW
        {
            using var sha = SHA256.Create();
            var data = Encoding.UTF8.GetBytes(plaintext + _refreshPepper);
            return Convert.ToHexString(sha.ComputeHash(data));
        }

        private async Task<(string refreshPlain, Guid familyId)> IssueRefreshAsync(NpgsqlConnection conn, int userId, Guid? existingFamily = null) // NEW
        {
            var family = existingFamily ?? Guid.NewGuid();
            var refreshPlain = GenerateRandomBase64Url();
            var hash = HashRefresh(refreshPlain);
            var expires = DateTime.UtcNow.AddDays(_refreshDays);

            const string sql = @"
                INSERT INTO refresh_tokens (user_id, family_id, token_hash, expires_utc)
                VALUES (@u, @f, @h, @exp)";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("f", family);
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("exp", expires);
            await cmd.ExecuteNonQueryAsync();

            return (refreshPlain, family);
        }

        // ===== DTOs =====

        public class FacebookTokenRequest
        {
            public string AccessToken { get; set; }
        }

        public class FacebookUserInfo
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("email")] public string Email { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
        }

        public class FacebookTokenDebugEnvelope
        {
            [JsonProperty("data")] public FacebookTokenDebug Data { get; set; }
        }

        public class FacebookTokenDebug
        {
            [JsonProperty("app_id")] public string AppId { get; set; }
            [JsonProperty("is_valid")] public bool IsValid { get; set; }
            [JsonProperty("expires_at")] public long ExpiresAt { get; set; }  // Unix timestamp
        }
    }
}
