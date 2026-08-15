using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Npgsql;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace DripRunBackend.Controllers
{
    [ApiController]
    [Route("api/auth")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public class AppAuthController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly string _jwtSecret;
        private readonly string _jwtIssuer;
        private readonly string _jwtAudience;
        private readonly string _appleClientId;
        private readonly string _appleBundleId;
        private readonly string _appleWebClientId;   // Web admin Services ID
        private readonly HttpClient _httpClient;
        private readonly ILogger<AppAuthController> _logger;
        private const string RefreshCookieName = "refresh";

        private readonly string _refreshPepper;
        private readonly int _refreshDays;

        private const int AccessMinutes = 60;

        public AppAuthController(IConfiguration cfg, HttpClient httpClient, ILogger<AppAuthController> logger)
        {
            _connectionString = cfg.GetConnectionString("DefaultConnection");
            _jwtSecret = cfg["JWT_SECRET"];
            _jwtIssuer = cfg["JWT_ISSUER"];
            _jwtAudience = cfg["JWT_AUDIENCE"];
            _appleClientId = cfg["Apple:ClientId"];
            _appleBundleId = cfg["Apple:BundleId"];
            _appleWebClientId = cfg["Apple:WebClientId"] ?? "";  // NEW
            _httpClient = httpClient;
            _logger = logger;

            _refreshPepper = cfg["Refresh:Pepper"];
            _refreshDays = int.TryParse(cfg["Refresh:Days"], out var d) ? d : 60;
        }

        private bool IsRefreshConfigured() => !string.IsNullOrWhiteSpace(_refreshPepper);

        public class RefreshRequest { public string RefreshToken { get; set; } }

        // ─────────────────────────────────────────────
        // APPLE LOGIN
        // ─────────────────────────────────────────────
        public class AppleTokenRequest { public string IdToken { get; set; } public string FullName { get; set; } public string Email { get; set; } }


        [AllowAnonymous]
        [HttpPost("apple-login-1.5")]
        public async Task<IActionResult> AppleLogin15([FromBody] AppleTokenRequest dto)
        {
            if (!IsRefreshConfigured())
            {
                _logger.LogError("Missing Refresh:Pepper");
                return StatusCode(500, new { error = "Server configuration error" });
            }

            if (string.IsNullOrWhiteSpace(dto?.IdToken))
                return BadRequest(new { error = "No id_token supplied." });

            var applePayload = await ValidateAppleTokenAsync(dto.IdToken);
            if (applePayload == null)
                return Unauthorized(new { error = "Invalid Apple ID token." });

            string email = applePayload.Email ?? dto.Email;
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { error = "Email not available from Apple. Re-consent required." });

            string incomingName = string.IsNullOrWhiteSpace(dto.FullName) ? null : dto.FullName.Trim();
            string name = incomingName ?? "Apple User";

            int userId;

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var checkCmd = new NpgsqlCommand("SELECT userid, name FROM users WHERE email = @e", conn);
            checkCmd.Parameters.AddWithValue("e", email);
            await using var rdr = await checkCmd.ExecuteReaderAsync();

            if (await rdr.ReadAsync())
            {
                userId = rdr.GetInt32(0);
                var storedName = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                if (!string.IsNullOrWhiteSpace(storedName) && storedName != "Apple User")
                    name = storedName;
                else if (!string.IsNullOrWhiteSpace(incomingName))
                    name = incomingName;
            }
            else
            {
                rdr.Close();
                if (string.IsNullOrWhiteSpace(incomingName))
                    return BadRequest(new { error = "Name not available from Apple. Re-consent required." });

                var insCmd = new NpgsqlCommand(@"
                    INSERT INTO users (email, name, created_at)
                    VALUES (@e, @n, @ts)
                    RETURNING userid", conn);
                insCmd.Parameters.AddWithValue("e", email);
                insCmd.Parameters.AddWithValue("n", incomingName);
                insCmd.Parameters.AddWithValue("ts", DateTime.UtcNow);
                userId = (int)await insCmd.ExecuteScalarAsync();
            }

            if (!rdr.IsClosed) await rdr.DisposeAsync();
            if (!string.IsNullOrWhiteSpace(incomingName))
            {
                var updCmd = new NpgsqlCommand(@"
                    UPDATE users SET name = @n
                    WHERE userid = @id AND (name IS NULL OR name = 'Apple User')", conn);
                updCmd.Parameters.AddWithValue("n", incomingName);
                updCmd.Parameters.AddWithValue("id", userId);
                await updCmd.ExecuteNonQueryAsync();
            }

            var accessJwt = GenerateAccessJwt(userId, email);
            var (refreshPlain, _) = await IssueRefreshAsync(conn, userId);

            return Ok(new { email, name, jwtToken = accessJwt, refreshToken = refreshPlain });
        }


        [AllowAnonymous]
        [HttpPost("apple-login-web-1.5")]
        public async Task<IActionResult> AppleLoginWeb15([FromBody] AppleTokenRequest dto)
        {
            if (!IsRefreshConfigured())
            {
                _logger.LogError("Missing Refresh:Pepper");
                return StatusCode(500, new { error = "Server configuration error" });
            }

            if (string.IsNullOrWhiteSpace(dto?.IdToken))
                return BadRequest(new { error = "No id_token supplied." });

            var applePayload = await ValidateAppleTokenAsync(dto.IdToken);
            if (applePayload == null)
                return Unauthorized(new { error = "Invalid Apple ID token." });

            var email = (applePayload.Email ?? dto.Email)?.Trim();
            if (string.IsNullOrWhiteSpace(email))
                return BadRequest(new { error = "Email not available from Apple. Re-consent required." });

            var incomingName = string.IsNullOrWhiteSpace(dto.FullName) ? null : dto.FullName.Trim();
            var name = incomingName ?? "Apple User";

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // 1) Lookup user (close reader before doing anything else)
            int userId = -1;
            string? storedName = null;

            await using (var checkCmd = new NpgsqlCommand("SELECT userid, name FROM users WHERE email = @e", conn))
            {
                checkCmd.Parameters.AddWithValue("e", email);
                await using var rdr = await checkCmd.ExecuteReaderAsync();

                if (await rdr.ReadAsync())
                {
                    userId = rdr.GetInt32(0);
                    storedName = rdr.IsDBNull(1) ? null : rdr.GetString(1);
                }
            }

            if (userId > 0)
            {
                // Prefer stored real name
                if (!string.IsNullOrWhiteSpace(storedName) && storedName != "Apple User")
                    name = storedName!;
                // Otherwise if Apple gave us a name (rare after first consent), use it
                else if (!string.IsNullOrWhiteSpace(incomingName))
                    name = incomingName!;
            }
            else
            {
                // Apple only provides FullName on first consent (commonly). If missing, you can still create
                // the user as "Apple User" to avoid blocking login, OR force re-consent like your old code.
                // Choose one behavior. Here I keep your stricter behavior:
                if (string.IsNullOrWhiteSpace(incomingName))
                    return BadRequest(new { error = "Name not available from Apple. Re-consent required." });

                await using var insCmd = new NpgsqlCommand(@"
            INSERT INTO users (email, name, created_at)
            VALUES (@e, @n, @ts)
            RETURNING userid", conn);

                insCmd.Parameters.AddWithValue("e", email);
                insCmd.Parameters.AddWithValue("n", incomingName);
                insCmd.Parameters.AddWithValue("ts", DateTime.UtcNow);

                userId = Convert.ToInt32(await insCmd.ExecuteScalarAsync());
                name = incomingName!;
            }

            if (userId <= 0)
                return StatusCode(500, new { error = "Failed to resolve user id" });

            // Optional: upgrade placeholder name if we got a real one
            if (!string.IsNullOrWhiteSpace(incomingName))
            {
                await using var updCmd = new NpgsqlCommand(@"
            UPDATE users SET name = @n
            WHERE userid = @id AND (name IS NULL OR name = 'Apple User')", conn);

                updCmd.Parameters.AddWithValue("n", incomingName);
                updCmd.Parameters.AddWithValue("id", userId);
                await updCmd.ExecuteNonQueryAsync();
            }

            // 2) Issue access + refresh
            var accessJwt = GenerateAccessJwt(userId, email);
            var (refreshPlain, _) = await IssueRefreshAsync(conn, userId);

            // 3) Set HttpOnly refresh cookie for WebGL (do NOT return refresh token in body)
            Response.Cookies.Append("refresh", refreshPlain, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,
                Path = "/",
                MaxAge = TimeSpan.FromDays(_refreshDays),
                // Domain = "..."; // only if you set it everywhere consistently
            });

            return Ok(new { email, name, jwtToken = accessJwt });
        }



        // ─────────────────────────────────────────────
        // REFRESH endpoint (rotation)
        // ─────────────────────────────────────────────
        public class RefreshResponse { public string AccessToken { get; set; } public string RefreshToken { get; set; } public long AccessExpiresInSec { get; set; } }


        [AllowAnonymous]
        [HttpPost("refresh-1.5")]
        public async Task<ActionResult<RefreshResponse>> Refresh15([FromBody] RefreshRequest req)
        {
            if (!IsRefreshConfigured())
            {
                _logger.LogError("Missing Refresh:Pepper");
                return StatusCode(500, new { error = "Server configuration error" });
            }

            if (string.IsNullOrWhiteSpace(req?.RefreshToken))
                return BadRequest(new { error = "Missing refresh token" });

            string cleaned = req.RefreshToken.Trim().Replace("\r", "").Replace("\n", "");

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var rotated = await RotateRefreshAsync(conn, cleaned);
            if (rotated == null)
                return Unauthorized(new { error = "Invalid or expired refresh token" });

            var (_, _, newRefreshPlain, accessToken, accessLifetimeSec) = rotated.Value;

            return Ok(new RefreshResponse
            {
                AccessToken = accessToken,
                RefreshToken = newRefreshPlain,
                AccessExpiresInSec = accessLifetimeSec
            });
        }


        [AllowAnonymous]
        [HttpPost("refresh-web-1.5")]
        public async Task<ActionResult<RefreshResponse>> RefreshWeb14()
        {
            if (!IsRefreshConfigured())
            {
                _logger.LogError("Missing Refresh:Pepper");
                return StatusCode(500, new { error = "Server configuration error" });
            }

            if (!Request.Cookies.TryGetValue("refresh", out var refresh) || string.IsNullOrWhiteSpace(refresh))
                return StatusCode(670, new { error = "Missing refresh cookie" });

            string cleaned = refresh.Trim().Replace("\r", "").Replace("\n", "");

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var rotated = await RotateRefreshAsync(conn, cleaned);
            if (rotated == null)
                return StatusCode(700, new { error = "Invalid or expired refresh token" });

            var (_, _, newRefreshPlain, accessToken, accessLifetimeSec) = rotated.Value;

            //return StatusCode(598, new { error = newRefreshPlain.ToString() + "   " + accessToken.ToString() + "   " + accessLifetimeSec.ToString() + "AHHHHHHHHHHHHHHHHHHHHHHHHH"});

            // Set rotated refresh back into HttpOnly cookie
            Response.Cookies.Append("refresh", newRefreshPlain, new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.None,  // needed if WebGL site is on a different domain
                Path = "/",
                MaxAge = TimeSpan.FromDays(_refreshDays),
            });

            // Return access token in body (fine)
            return Ok(new RefreshResponse
            {
                AccessToken = accessToken,
                AccessExpiresInSec = accessLifetimeSec
            });
        }


        // ─────────────────────────────────────────────
        // LOGOUT (family revoke)
        // ─────────────────────────────────────────────
        [AllowAnonymous]
        [HttpPost("logout-mobile")]
        public async Task<IActionResult> LogoutMobile([FromBody] RefreshRequest req)
        {
            if (!IsRefreshConfigured())
            {
                _logger.LogError("Missing Refresh:Pepper");
                return StatusCode(500, new { error = "Server configuration error" });
            }

            if (string.IsNullOrWhiteSpace(req?.RefreshToken))
                return BadRequest(new { error = "Missing refresh token" });

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            var ok = await RevokeFamilyAsync(conn, req.RefreshToken.Trim().Replace("\r", "").Replace("\n", ""));
            if (!ok) return Unauthorized(new { error = "Invalid refresh token" });

            return NoContent();
        }

        [AllowAnonymous]
        [HttpPost("logout-web")]
        public async Task<IActionResult> LogoutWeb()
        {
            if (!IsRefreshConfigured())
            {
                _logger.LogError("Missing Refresh:Pepper");
                return StatusCode(500, new { error = "Server configuration error" });
            }

            // 1) Read refresh token from HttpOnly cookie
            if (!Request.Cookies.TryGetValue(RefreshCookieName, out var refresh) ||
                string.IsNullOrWhiteSpace(refresh))
            {
                // If already logged out / no cookie, treat as success
                ClearRefreshCookie();
                return NoContent();
            }

            refresh = refresh.Trim().Replace("\r", "").Replace("\n", "");

            // 2) Revoke refresh family in DB
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // If token is invalid/expired, you can still clear cookies and return 204
            _ = await RevokeFamilyAsync(conn, refresh);

            // 3) Clear the cookie so the browser stops sending it
            ClearRefreshCookie();

            return NoContent();
        }

        private void ClearRefreshCookie()
        {
            var opts = new CookieOptions
            {
                HttpOnly = true,
                Secure = true,              // required for SameSite=None
                SameSite = SameSiteMode.None,
                Path = "/",                 // must match the cookie path you used when setting it
                Expires = DateTimeOffset.UnixEpoch
            };

            Response.Cookies.Append(RefreshCookieName, "", opts);

            // If you also set other cookies (e.g. "refresh2", "rt", etc.), clear them too.
            // Response.Cookies.Append("refresh2", "", opts);
        }


        // ─────────────────────────────────────────────
        // Apple ID token validation
        // ─────────────────────────────────────────────
        public class AppleTokenPayload { public string Email; }
        public class AppleKeys { public List<AppleKey> Keys { get; set; } }
        public class AppleKey { public string Kid { get; set; } public string Alg { get; set; } public string N { get; set; } public string E { get; set; } }

        private async Task<AppleTokenPayload?> ValidateAppleTokenAsync(string idToken)
        {
            var handler = new JwtSecurityTokenHandler();
            var unvalidated = handler.ReadJwtToken(idToken);

            string kid = unvalidated.Header["kid"]?.ToString() ?? "";
            string alg = unvalidated.Header["alg"]?.ToString() ?? "";

            string keysJson = await _httpClient.GetStringAsync("https://appleid.apple.com/auth/keys");
            var appleKeys = JsonConvert.DeserializeObject<AppleKeys>(keysJson);
            var match = appleKeys.Keys.FirstOrDefault(k => k.Kid == kid && k.Alg == alg);
            if (match == null) return null;

            var rsaParams = new RSAParameters
            {
                Modulus = Base64UrlEncoder.DecodeBytes(match.N),
                Exponent = Base64UrlEncoder.DecodeBytes(match.E)
            };

            var validationParams = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://appleid.apple.com",
                ValidateAudience = true,
                ValidAudiences = new[] { _appleClientId, _appleBundleId, _appleWebClientId },
                RequireExpirationTime = true,
                ValidateLifetime = true,
                IssuerSigningKey = new RsaSecurityKey(rsaParams),
                ValidateIssuerSigningKey = true
            };

            try
            {
                handler.ValidateToken(idToken, validationParams, out var validatedToken);
                var jwt = (JwtSecurityToken)validatedToken;
                return new AppleTokenPayload
                {
                    Email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value
                };
            }
            catch
            {
                _logger.LogWarning("Apple token validation failed.");
                return null;
            }
        }

        // ─────────────────────────────────────────────
        // Access JWT
        // ─────────────────────────────────────────────
        private string GenerateAccessJwt(int userId, string email)
        {
            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSecret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(ClaimTypes.Email, email)
            };

            var token = new JwtSecurityToken(
                _jwtIssuer,
                _jwtAudience,
                claims,
                expires: DateTime.UtcNow.AddMinutes(AccessMinutes),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        // ─────────────────────────────────────────────
        // Refresh helpers
        // ─────────────────────────────────────────────
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

            var cmd = new NpgsqlCommand(@"
                INSERT INTO refresh_tokens (user_id, family_id, token_hash, expires_utc)
                VALUES (@u, @f, @h, @exp)", conn);
            cmd.Parameters.AddWithValue("u", userId);
            cmd.Parameters.AddWithValue("f", family);
            cmd.Parameters.AddWithValue("h", hash);
            cmd.Parameters.AddWithValue("exp", expires);
            await cmd.ExecuteNonQueryAsync();

            return (refreshPlain, family);
        }

        private async Task<(int userId, string email, string newRefreshPlain, string accessToken, long accessLifetimeSec)?>
            RotateRefreshAsync(NpgsqlConnection conn, string refreshPlain)
        {
            var incoming = refreshPlain?.Trim()?.Replace("\r", "")?.Replace("\n", "");
            if (string.IsNullOrWhiteSpace(incoming))
                return null;

            var hash = HashRefresh(incoming);

            var find = new NpgsqlCommand(@"
                SELECT rt.user_id, rt.family_id, u.email, rt.expires_utc, rt.revoked_utc
                FROM refresh_tokens rt
                JOIN users u ON u.userid = rt.user_id
                WHERE rt.token_hash = @h
                LIMIT 1", conn);
            find.Parameters.AddWithValue("h", hash);

            using var rdr = await find.ExecuteReaderAsync();
            if (!await rdr.ReadAsync())
                return null;

            var userId = rdr.GetInt32(0);
            var familyId = rdr.GetGuid(1);
            var email = rdr.GetString(2);
            var expiresUtc = rdr.GetDateTime(3);
            var revoked = !rdr.IsDBNull(4);

            if (revoked || expiresUtc <= DateTime.UtcNow)
                return null;

            await rdr.DisposeAsync();

            var revoke = new NpgsqlCommand(@"
                UPDATE refresh_tokens
                SET revoked_utc = NOW(), last_used_utc = NOW()
                WHERE token_hash = @h", conn);
            revoke.Parameters.AddWithValue("h", hash);
            await revoke.ExecuteNonQueryAsync();

            var (newRefreshPlain, _) = await IssueRefreshAsync(conn, userId, familyId);
            var access = GenerateAccessJwt(userId, email);

            return (userId, email, newRefreshPlain, access, AccessMinutes * 60L);
        }

        private async Task<bool> RevokeFamilyAsync(NpgsqlConnection conn, string refreshPlain)
        {
            var clean = refreshPlain?.Trim()?.Replace("\r", "")?.Replace("\n", "");
            if (string.IsNullOrWhiteSpace(clean))
                return false;

            var hash = HashRefresh(clean);

            var get = new NpgsqlCommand(@"
                SELECT family_id FROM refresh_tokens
                WHERE token_hash = @h LIMIT 1", conn);
            get.Parameters.AddWithValue("h", hash);

            var famObj = await get.ExecuteScalarAsync();
            if (famObj == null) return false;

            var familyId = (Guid)famObj;

            var revoke = new NpgsqlCommand(@"
                UPDATE refresh_tokens
                SET revoked_utc = NOW()
                WHERE family_id = @f AND revoked_utc IS NULL", conn);
            revoke.Parameters.AddWithValue("f", familyId);
            await revoke.ExecuteNonQueryAsync();

            return true;
        }
    }
}
