using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;
using System.Threading.Tasks;

namespace DripRunBackend.Controllers
{
    [Authorize]
    [ApiController]
    [Route("api/brandrequests")]
    public class BrandRequestsController : ControllerBase
    {
        private readonly string _connectionString;

        public BrandRequestsController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }

        public class BrandRequestsDto
        {
            public string Brand1 { get; set; }
            public string Brand2 { get; set; }
            public string Brand3 { get; set; }
        }

        [HttpPost]
        public async Task<IActionResult> SubmitBrands([FromBody] BrandRequestsDto dto)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var transaction = await connection.BeginTransactionAsync();

            try
            {
                // Insert 3 brand requests
                using var insertCommand = new NpgsqlCommand(@"
                    INSERT INTO brandrequests (name) VALUES 
                    (@brand1), 
                    (@brand2), 
                    (@brand3);", connection);

                insertCommand.Parameters.AddWithValue("brand1", dto.Brand1 ?? string.Empty);
                insertCommand.Parameters.AddWithValue("brand2", dto.Brand2 ?? string.Empty);
                insertCommand.Parameters.AddWithValue("brand3", dto.Brand3 ?? string.Empty);

                await insertCommand.ExecuteNonQueryAsync();

                // Add 5 leaderboard points
                var command = new NpgsqlCommand("UPDATE users SET ldboardpts = ldboardpts + 5 WHERE userid = @UserId", connection);
                command.Parameters.AddWithValue("UserId", userId);  // match casing exactly

                await command.ExecuteNonQueryAsync();

                await transaction.CommitAsync();

                return Ok(new { success = true });
            }
            catch
            {
                await transaction.RollbackAsync();
                return StatusCode(500, new { success = false, error = "An error occurred while processing your request." });
            }
        }
    }
}