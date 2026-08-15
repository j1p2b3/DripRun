using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Security.Claims;

namespace DripRunBackend.Controllers
{
    [Route("api/download")]  // Matches Unity's expected API route
    [ApiController]
    [Authorize]
    public class StorageDownloadController : ControllerBase
    {
        private readonly string _blobStorageConnectionString;
        private readonly string _connectionString;
        private readonly string _containerName;
        private readonly string _secureContainerName;

        public StorageDownloadController(IConfiguration configuration)
        {
            _blobStorageConnectionString = configuration["BlobStorage:ConnectionString"];
            _connectionString = configuration.GetConnectionString("DefaultConnection");
            _containerName = configuration["BlobStorage:ContainerName"];
            _secureContainerName = configuration["BlobStorage:SecureContainerName"];
        }


        [HttpGet("secure-image/by-coupon/{couponId:int}")]
        [Authorize]
        public async Task<IActionResult> GetSecureImageByCouponId(int couponId)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Ownership check
            await using (var ownCmd = new NpgsqlCommand(@"
            SELECT 1
            FROM user_coupon
            WHERE userid = @uid AND couponid = @cid
            LIMIT 1;
            ", conn))
            {
                ownCmd.Parameters.AddWithValue("@uid", userId);
                ownCmd.Parameters.AddWithValue("@cid", couponId);

                if (await ownCmd.ExecuteScalarAsync() == null)
                    return StatusCode(403, "You do not own this coupon.");
            }

            // Get image name
            string imageName;
            await using (var imgCmd = new NpgsqlCommand(@"
            SELECT imagename
            FROM coupons
            WHERE couponid = @cid;
            ", conn))
            {
                imgCmd.Parameters.AddWithValue("@cid", couponId);
                var obj = await imgCmd.ExecuteScalarAsync();

                if (obj == null || obj is DBNull)
                    return NotFound("No image assigned to this coupon.");

                imageName = (string)obj;
            }

            // Download from SECURE container
            var blobServiceClient = new BlobServiceClient(_blobStorageConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(_secureContainerName);
            var blobClient = blobContainerClient.GetBlobClient(imageName);

            if (!await blobClient.ExistsAsync())
                return NotFound("Image not found in secure storage.");

            var ms = new MemoryStream();
            await blobClient.DownloadToAsync(ms);
            ms.Position = 0;

            return File(ms, "image/png");
        }



        [HttpGet("secure-model/by-coupon/{couponId:int}")]
        [Authorize]
        public async Task<IActionResult> GetSecureModelByCouponId(int couponId)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                return Unauthorized();

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync();

            // Ownership check
            await using (var ownCmd = new NpgsqlCommand(@"
            SELECT 1
            FROM user_coupon
            WHERE userid = @uid AND couponid = @cid
            LIMIT 1;
            ", conn))
            {
                ownCmd.Parameters.AddWithValue("@uid", userId);
                ownCmd.Parameters.AddWithValue("@cid", couponId);

                if (await ownCmd.ExecuteScalarAsync() == null)
                    return StatusCode(403, "You do not own this coupon.");
            }

            // Get model name
            string modelName;
            await using (var mdlCmd = new NpgsqlCommand(@"
            SELECT modelimgname
            FROM coupons
            WHERE couponid = @cid;
            ", conn))
            {
                mdlCmd.Parameters.AddWithValue("@cid", couponId);
                var obj = await mdlCmd.ExecuteScalarAsync();

                if (obj == null || obj is DBNull)
                    return NotFound("No model assigned to this coupon.");

                modelName = (string)obj;
            }

            var blobServiceClient = new BlobServiceClient(_blobStorageConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(_secureContainerName);
            var blobClient = blobContainerClient.GetBlobClient(modelName);

            if (!await blobClient.ExistsAsync())
                return NotFound("Model not found in secure storage.");

            var ms = new MemoryStream();
            await blobClient.DownloadToAsync(ms);
            ms.Position = 0;

            return File(ms, "application/octet-stream", modelName);
        }






        [HttpGet("image/{imageName}")]
        public async Task<IActionResult> GetImage(string imageName)
        {
            //Security Check: Prevent invalid characters
            if (string.IsNullOrWhiteSpace(imageName) || imageName.Contains("..") || imageName.Contains("/"))
            {
                return BadRequest("Invalid image name.");
            }

            //Create Blob Storage Client
            var blobServiceClient = new BlobServiceClient(_blobStorageConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(_containerName);
            var blobClient = blobContainerClient.GetBlobClient(imageName);

            //Check if the image exists in Azure Blob Storage
            if (!await blobClient.ExistsAsync())
            {
                return NotFound("Image not found in storage.");
            }

            //Download the image as a stream
            var memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            memoryStream.Position = 0;  // Reset stream position

            // Return the image as a PNG file
            return File(memoryStream, "image/png");
        }

        [HttpGet("model/file/{modelName}")]
        public async Task<IActionResult> GetModel(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName) || modelName.Contains("..") || modelName.Contains("/"))
            {
                return BadRequest("Invalid model name.");
            }

            var blobServiceClient = new BlobServiceClient(_blobStorageConnectionString);
            var blobContainerClient = blobServiceClient.GetBlobContainerClient(_containerName);
            var blobClient = blobContainerClient.GetBlobClient(modelName);

            if (!await blobClient.ExistsAsync())
            {
                return NotFound("Model not found in storage.");
            }

            var memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            memoryStream.Position = 0;

            return File(memoryStream, "application/octet-stream", modelName);
        }

    }
}