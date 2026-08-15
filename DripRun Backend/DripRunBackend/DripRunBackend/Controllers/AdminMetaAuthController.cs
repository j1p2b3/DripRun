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
    [Route("api/admin-auth")]
    [ApiController]
    public class AdminMetaAuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly HttpClient _httpClient;
        private readonly string _facebookAppId;
        private readonly string _facebookAppSecret;
        private readonly string _jwtSecret;
        private readonly string _jwtIssuer;
        private readonly string _jwtAdminAudience;

        private const string GraphBase = "https://graph.facebook.com/v19.0";
        private const string FacebookDebugTokenUrl = $"{GraphBase}/debug_token";
        private const string FacebookGraphMeUrl = $"{GraphBase}/me?fields=id,name,email";

        public AdminMetaAuthController(IConfiguration configuration, HttpClient httpClient)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                  ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
            _httpClient = httpClient;
            _facebookAppId = configuration["Facebook:AppId"]
                                  ?? throw new InvalidOperationException("Missing Facebook:AppId");
            _facebookAppSecret = configuration["Facebook:AppSecret"]
                                  ?? throw new InvalidOperationException("Missing Facebook:AppSecret");
            _jwtSecret = configuration["JWT_SECRET"]
                                  ?? throw new InvalidOperationException("Missing JWT_SECRET");
            _jwtIssuer = configuration["JWT_ISSUER"]
                                  ?? throw new InvalidOperationException("Missing JWT_ISSUER");
            _jwtAdminAudience = configuration["JWT_ADMIN_AUDIENCE"]
                                  ?? throw new InvalidOperationException("Missing JWT_ADMIN_AUDIENCE (required for admin tokens)");
        }

        public class FacebookTokenRequest { public string AccessToken { get; set; } }

        private class FacebookUserInfo
        {
            [JsonProperty("id")] public string Id { get; set; }
            [JsonProperty("email")] public string Email { get; set; }
            [JsonProperty("name")] public string Name { get; set; }
        }

        private class FacebookTokenDebugEnvelope { [JsonProperty("data")] public FacebookTokenDebug Data { get; set; } }
        private class FacebookTokenDebug
        {
            [JsonProperty("app_id")] public string AppId { get; set; }
            [JsonProperty("is_valid")] public bool IsValid { get; set; }
            [JsonProperty("expires_at")] public long ExpiresAt { get; set; }  // Unix epoch seconds
        }

        [AllowAnonymous]
        [HttpPost("meta-admin")]
        public async Task<IActionResult> MetaAdmin([FromBody] FacebookTokenRequest tokenRequest)
        {
            if (tokenRequest == null || string.IsNullOrWhiteSpace(tokenRequest.AccessToken))
                return BadRequest(new { error = "Invalid token request" });

            var accessToken = tokenRequest.AccessToken.Trim();

            // Limited Login tokens (JWT-like with dots) are not Graph user access tokens.
            if (accessToken.Contains("."))
                return Unauthorized(new { error = "This looks like a Limited Login token. Use classic login to obtain a Graph API user access token." });

            // 1) Verify with /debug_token
            if (!await VerifyFacebookToken(accessToken))
                return Unauthorized(new { error = "Invalid or expired Facebook Access Token" });

            // 2) Retrieve FB user info (id/email/name) with appsecret_proof
            var fbUser = await GetFacebookUserInfo(accessToken);
            if (fbUser == null)
                return Unauthorized(new { error = "Failed to retrieve Facebook user info" });

            if (string.IsNullOrWhiteSpace(fbUser.Email))
                return Unauthorized(new { error = "Facebook account has no email or permission not granted" });

            var email = fbUser.Email;
            int userId;
            int? companyId;

            // 3) Look up existing user; DO NOT create
            await using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();

                const string sql = @"SELECT userid, ""CompanyID"" FROM users WHERE lower(email) = lower(@Email) LIMIT 1;";
                await using var cmd = new NpgsqlCommand(sql, connection);
                cmd.Parameters.AddWithValue("Email", email);

                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync())
                    return Unauthorized(new { error = "No account. Create account in the app first." });

                userId = reader.GetInt32(0);
                companyId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
            }

            // 4) Enforce admin rule: must have company
            if (companyId is null)
                return Forbid(); // user exists but is not a company admin

            // 5) Issue short-lived Admin JWT (aud = admin audience)
            var jwt = GenerateAdminJwtToken(userId, email, companyId.Value);

            return Ok(new
            {
                email,
                jwtToken = jwt,
                expiresIn = 60 * 60 // seconds
            });
        }

        private async Task<bool> VerifyFacebookToken(string accessToken)
        {
            var appAccess = $"{_facebookAppId}|{_facebookAppSecret}";
            var url = $"{FacebookDebugTokenUrl}?input_token={HttpUtility.UrlEncode(accessToken)}&access_token={HttpUtility.UrlEncode(appAccess)}";

            using var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode) return false;

            var body = await response.Content.ReadAsStringAsync();
            var env = JsonConvert.DeserializeObject<FacebookTokenDebugEnvelope>(body);
            var data = env?.Data;
            if (data == null) return false;
            if (!data.IsValid) return false;
            if (!string.Equals(data.AppId, _facebookAppId, StringComparison.Ordinal)) return false;
            if (data.ExpiresAt > 0 && data.ExpiresAt < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;

            return true;
        }

        private async Task<FacebookUserInfo> GetFacebookUserInfo(string accessToken)
        {
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

        private string GenerateAdminJwtToken(int userId, string email, int companyId)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, email ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Aud, _jwtAdminAudience),
                new Claim("company_id", companyId.ToString()),
                new Claim("is_company_admin", "true"),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            };

            var token = new JwtSecurityToken(
                issuer: _jwtIssuer,
                audience: _jwtAdminAudience,
                claims: claims,
                notBefore: DateTime.UtcNow,
                expires: DateTime.UtcNow.AddMinutes(60),
                signingCredentials: creds
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }
}
