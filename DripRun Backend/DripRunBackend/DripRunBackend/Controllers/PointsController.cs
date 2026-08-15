using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace DripRunBackend.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/points")]
    public class PointsController : ControllerBase
    {
        private readonly string _connectionString;

        public PointsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        [HttpPost("login")]
        public async Task<IActionResult> AddLoginPoints()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
            {
                return Unauthorized();
            }

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            var command = new NpgsqlCommand("UPDATE users SET ldboardpts = ldboardpts + 5 WHERE userid = @UserId", connection);
            command.Parameters.AddWithValue("UserId", userId);

            int affectedRows = await command.ExecuteNonQueryAsync();
            if (affectedRows > 0)
            {
                return Ok(new { success = true });
            }

            return BadRequest(new { success = false });
        }
    }
}
