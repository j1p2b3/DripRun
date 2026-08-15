using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace DripRunBackend.Controllers
{
    [ApiController]
    [Route("api/leaderboard")]
    [Authorize]
    public class LeaderboardController : ControllerBase
    {
        private readonly string _connectionString;

        public LeaderboardController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection");
        }
        [HttpGet]
        public async Task<IActionResult> GetLeaderboard()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int currentUserId))
                return Unauthorized();

            var leaderboard = new List<LeaderboardEntry>();
            LeaderboardEntry currentUserEntry = null;

            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            // Check if the current user is public
            bool currentUserIsPublic = false;
            {
                var pubCmd = new NpgsqlCommand(
                    @"SELECT ispublic FROM users WHERE userid = @UserId",
                    connection);
                pubCmd.Parameters.AddWithValue("UserId", currentUserId);
                var obj = await pubCmd.ExecuteScalarAsync();
                if (obj is bool b) currentUserIsPublic = b;
            }

            // Top 200 PUBLIC users
            var topCommand = new NpgsqlCommand(@"
        SELECT userid, name, ldboardpts 
        FROM users 
        WHERE ispublic = true 
        ORDER BY ldboardpts DESC, userid ASC
        LIMIT 200", connection);

            int rank = 1;
            await using (var reader = await topCommand.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    int userId = reader.GetInt32(0);
                    var entry = new LeaderboardEntry
                    {
                        rank = rank,
                        name = reader.GetString(1),
                        points = reader.GetInt32(2),
                        // Will only be true if the current user is public and appears in the top-200
                        isCurrentUser = userId == currentUserId
                    };

                    if (entry.isCurrentUser)
                        currentUserEntry = entry;

                    leaderboard.Add(entry);
                    rank++;
                }
            }

            // If the user is PUBLIC but not in the top list, fetch their public rank and append them at the bottom
            if (currentUserIsPublic && currentUserEntry == null)
            {
                var selfCommand = new NpgsqlCommand(@"
            SELECT name, ldboardpts,
            (
                SELECT COUNT(*) + 1 
                FROM users 
                WHERE ispublic = true AND ldboardpts > u.ldboardpts
            ) AS rank
            FROM users u 
            WHERE userid = @UserId", connection);

                selfCommand.Parameters.AddWithValue("UserId", currentUserId);

                await using var reader = await selfCommand.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var selfEntry = new LeaderboardEntry
                    {
                        name = reader.GetString(0),
                        points = reader.GetInt32(1),
                        rank = reader.GetInt32(2), // public rank among ispublic=true users
                        isCurrentUser = true
                    };

                    // No need to insert at rank; if they were ≤ 200 they would have been in the top list
                    leaderboard.Add(selfEntry);
                }
            }

            return Ok(new LeaderboardWrapper { entries = leaderboard });
        }

        public class LeaderboardEntry
        {
            public int rank { get; set; }
            public string name { get; set; }
            public int points { get; set; }
            public bool isCurrentUser { get; set; }
        }

        public class LeaderboardWrapper
        {
            public List<LeaderboardEntry> entries { get; set; }
        }
    }
}