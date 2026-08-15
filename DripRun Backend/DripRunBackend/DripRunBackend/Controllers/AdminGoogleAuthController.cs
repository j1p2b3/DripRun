using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Google.Apis.Auth;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace DripRunBackend.Controllers
{
    [Route("api/admin-auth")]
    [ApiController]
    public class AdminGoogleAuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _googleClientId;
        private readonly string _jwtSecret;
        private readonly string _jwtIssuer;
        private readonly string _jwtAdminAudience;

        public AdminGoogleAuthController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                                   ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");
            _googleClientId = configuration["Google:ClientId"]
                                   ?? throw new InvalidOperationException("Missing Google:ClientId");
            _jwtSecret = configuration["JWT_SECRET"]
                                   ?? throw new InvalidOperationException("Missing JWT_SECRET");
            _jwtIssuer = configuration["JWT_ISSUER"]
                                   ?? throw new InvalidOperationException("Missing JWT_ISSUER");
            _jwtAdminAudience = configuration["JWT_ADMIN_AUDIENCE"]
                                   ?? throw new InvalidOperationException("Missing JWT_ADMIN_AUDIENCE (admin tokens require a dedicated audience)");
        }

        public sealed class GoogleTokenRequest { public string IdToken { get; set; } }

        [AllowAnonymous]
        [HttpPost("google-admin")]
        public async Task<IActionResult> GoogleAdmin([FromBody] GoogleTokenRequest body)
        {
            if (body == null || string.IsNullOrWhiteSpace(body.IdToken))
                return BadRequest(new { error = "Missing idToken" });

            // 1) Verify Google ID token against your Web Client ID
            GoogleJsonWebSignature.Payload payload;
            try
            {
                payload = await GoogleJsonWebSignature.ValidateAsync(
                    body.IdToken,
                    new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { _googleClientId } }
                );
            }
            catch
            {
                return Unauthorized(new { error = "Invalid Google ID token" });
            }

            var email = payload.Email ?? string.Empty;

            // 2) Look up existing user; DO NOT create
            int userId;
            int? companyId;

            await using (var con = new NpgsqlConnection(_connectionString))
            {
                await con.OpenAsync();

                const string sql = @"SELECT userid, ""CompanyID"" FROM users WHERE lower(email) = lower(@Email) LIMIT 1;";
                await using var cmd = new NpgsqlCommand(sql, con);
                cmd.Parameters.AddWithValue("Email", email);

                await using var r = await cmd.ExecuteReaderAsync();
                if (!await r.ReadAsync())
                    return Unauthorized(new { error = "No account. Create account in the app first." });

                userId = r.GetInt32(0);
                companyId = r.IsDBNull(1) ? (int?)null : r.GetInt32(1);
            }

            // 3) Enforce admin rule: must have a company
            if (companyId is null)
                return Forbid(); // user exists but is not a company admin

            // 4) Issue short-lived Admin JWT (aud = admin audience)
            var jwt = GenerateAdminJwtToken(userId, email, companyId.Value);

            // unified response (omit userId)
            return Ok(new
            {
                email,
                jwtToken = jwt,
                expiresIn = 60 * 60 // seconds
            });
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
