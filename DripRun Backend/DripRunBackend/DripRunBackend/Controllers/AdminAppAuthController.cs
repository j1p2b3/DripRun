using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace DripRunBackend.Controllers
{
    [ApiController]
    [Route("api/admin-auth")]
    public class AdminAppleAuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _jwtSecret;
        private readonly string _jwtIssuer;
        private readonly string _jwtAdminAudience;
        private readonly string _appleClientId;   // Services ID (web) or App ID
        private readonly string _appleBundleId;   // Native bundle id (allowed as aud)
        private readonly string _appleWebClientId;   // Web admin Services ID
        private readonly HttpClient _httpClient;
        private readonly ILogger<AdminAppleAuthController> _logger;

        // Static JWKS cache (12h)
        private static JsonWebKeySet? _jwks;
        private static DateTime _jwksAt = DateTime.MinValue;

        public AdminAppleAuthController(IConfiguration cfg, HttpClient httpClient, ILogger<AdminAppleAuthController> logger)
        {
            _connectionString = cfg.GetConnectionString("DefaultConnection");
            _jwtSecret = cfg["JWT_SECRET"] ?? throw new InvalidOperationException("Missing JWT_SECRET");
            _jwtIssuer = cfg["JWT_ISSUER"] ?? throw new InvalidOperationException("Missing JWT_ISSUER");
            _jwtAdminAudience = cfg["JWT_ADMIN_AUDIENCE"] ?? throw new InvalidOperationException("Missing JWT_ADMIN_AUDIENCE (required for admin tokens)");

            _appleClientId = cfg["Apple:ClientId"] ?? "";
            _appleBundleId = cfg["Apple:BundleId"] ?? "";
            _appleWebClientId = cfg["Apple:AdminWebClientId"] ?? "";  // NEW

            _httpClient = httpClient;
            _logger = logger;
        }

        public class AppleAdminRequest { public string IdToken { get; set; } }

        [AllowAnonymous]
        [HttpPost("apple-admin")]
        public async Task<IActionResult> AppleAdmin([FromBody] AppleAdminRequest dto)
        {
            if (string.IsNullOrWhiteSpace(dto?.IdToken))
                return BadRequest(new { error = "No id_token supplied." });

            var email = await ValidateAndExtractEmailAsync(dto.IdToken);
            if (string.IsNullOrWhiteSpace(email))
                return Unauthorized(new { error = "Invalid Apple ID token or email unavailable." });

            int userId;
            int? companyId;

            await using (var conn = new NpgsqlConnection(_connectionString))
            {
                await conn.OpenAsync();

                const string sql = @"SELECT userid, ""CompanyID"" FROM users WHERE lower(email)=lower(@e) LIMIT 1;";
                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("e", email);

                await using var rdr = await cmd.ExecuteReaderAsync();
                if (!await rdr.ReadAsync())
                    return Unauthorized(new { error = "No account. Create account in the app first." });

                userId = rdr.GetInt32(0);
                companyId = rdr.IsDBNull(1) ? (int?)null : rdr.GetInt32(1);
            }

            if (companyId is null)
                return Forbid(); // user exists but is not a company admin

            var jwt = GenerateAdminJwtToken(userId, email!, companyId.Value);
            return Ok(new { email, jwtToken = jwt, expiresIn = 60 * 60 }); // seconds
        }

        // ---- Apple ID token validation (Apple JWKS, 12h cache) ----
        private async Task<string?> ValidateAndExtractEmailAsync(string idToken)
        {
            var handler = new JwtSecurityTokenHandler();
            JwtSecurityToken unvalidated;
            try
            {
                unvalidated = handler.ReadJwtToken(idToken);
            }
            catch
            {
                return null;
            }

            // Refresh JWKS occasionally
            if (_jwks is null || (DateTime.UtcNow - _jwksAt) > TimeSpan.FromHours(12))
            {
                var keysJson = await _httpClient.GetStringAsync("https://appleid.apple.com/auth/keys");
                _jwks = new JsonWebKeySet(keysJson);
                _jwksAt = DateTime.UtcNow;
            }

            var validAudiences = new List<string>();

            if (!string.IsNullOrWhiteSpace(_appleClientId))
                validAudiences.Add(_appleClientId);

            if (!string.IsNullOrWhiteSpace(_appleBundleId))
                validAudiences.Add(_appleBundleId);

            if (!string.IsNullOrWhiteSpace(_appleWebClientId))
                validAudiences.Add(_appleWebClientId);   // NEW: web admin Services ID

            var tvp = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://appleid.apple.com",
                ValidateAudience = true,
                ValidAudiences = validAudiences,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                IssuerSigningKeys = _jwks!.Keys,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromMinutes(2)
            };

            try
            {
                var principal = handler.ValidateToken(idToken, tvp, out var validated);
                var jwt = (JwtSecurityToken)validated;
                var email =
                    principal.Claims.FirstOrDefault(c => c.Type == "email")?.Value ??
                    jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value;
                return email;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Apple token validation failed.");
                return null;
            }
        }

        // ---- Admin JWT minting ----
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
