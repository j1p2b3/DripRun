using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using System.Data;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.Http;
using System.IO;
using Azure.Storage.Blobs.Specialized;

namespace DripRunBackend.Controllers
{
    [Authorize(Policy = "AdminJwt")]
    [ApiController]
    [Route("api/admin")]
    public class AdminUploadController : ControllerBase
    {
        private readonly string _cs;
        private readonly BlobContainerClient _couponImagesContainer;
        private readonly string _defaultTosLink;

        // ✅ Inject the container directly (matches Program.cs registration)
        public AdminUploadController(IConfiguration cfg, BlobContainerClient couponImagesContainer)
        {
            _cs = cfg.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Missing ConnectionStrings:DefaultConnection");

            // This is the container created in Program.cs using BlobStorage:ConnectionString/ContainerName
            _couponImagesContainer = couponImagesContainer;

            // Default ToS link (used when form sends null/empty)
            _defaultTosLink = cfg["DripRun:DefaultTosLink"]
                ?? "https://www.driprun.com.au/terms-and-conditions"; // adjust to your real policy link
        }

        [HttpPost("coupons/create-with-locations")]
        public async Task<IActionResult> CreateCouponWithLocationsJson(
        [FromBody] CreateCouponWithLocationsJsonRequest req)
        {
            // 1) Company from JWT
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            // 2) Mission ownership check
            await using (var checkCon = new NpgsqlConnection(_cs))
            {
                await checkCon.OpenAsync();
                const string checkSql = @"
            SELECT 1
            FROM missions
            WHERE missionid = @missionId
              AND companyid = @companyId;";
                await using var cmd = new NpgsqlCommand(checkSql, checkCon);
                cmd.Parameters.AddWithValue("missionId", req.MissionId);
                cmd.Parameters.AddWithValue("companyId", companyId);
                if (await cmd.ExecuteScalarAsync() == null)
                    return BadRequest(new { error = "Mission does not exist or does not belong to your company." });
            }

            // 3) Validate images
            if (string.IsNullOrWhiteSpace(req.ModelFileUrl))
            {
                return BadRequest(new { error = "ModelFileUrl is required." });
            }

            var lat = req.Latitudes ?? Array.Empty<double>();
            var lon = req.Longitudes ?? Array.Empty<double>();
            var names = req.LocationNames ?? Array.Empty<string>();

            if (lat.Length != lon.Length || lat.Length != names.Length)
                return BadRequest(new { error = "Latitudes, Longitudes and LocationNames must be same length." });

            // 4) Download images server-side
            using var http = new HttpClient();

            async Task<string> DownloadToPublicBlob(string url)
            {
                var bytes = await http.GetByteArrayAsync(url);
                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(new Uri(url).AbsolutePath)}";
                var blob = _couponImagesContainer.GetBlobClient(fileName);
                await blob.UploadAsync(new MemoryStream(bytes), overwrite: false);
                return fileName;
            }
            async Task<string> DownloadToSecureBlob(string url)
            {
                var bytes = await http.GetByteArrayAsync(url);
                var fileName = $"{Guid.NewGuid():N}{Path.GetExtension(new Uri(url).AbsolutePath)}";

                // 🔴 THIS IS THE ONLY NEW LINE THAT MATTERS
                var secureContainer =
                    _couponImagesContainer
                        .GetParentBlobServiceClient()
                        .GetBlobContainerClient("haulcoup-secureimages");

                var blob = secureContainer.GetBlobClient(fileName);
                await blob.UploadAsync(new MemoryStream(bytes), overwrite: false);
                return fileName;
            }
            async Task<string> CopyPublicBlobToSecure(string blobName)
            {
                // Public blob
                var publicBlob = _couponImagesContainer.GetBlobClient(blobName);

                if (!await publicBlob.ExistsAsync())
                    throw new Exception($"Public blob does not exist: {blobName}");

                // Secure container
                var secureContainer =
                    _couponImagesContainer
                        .GetParentBlobServiceClient()
                        .GetBlobContainerClient("haulcoup-secureimages");

                // Secure blob (same exact filename)
                var secureBlob = secureContainer.GetBlobClient(blobName);

                // Download public blob
                var ms = new MemoryStream();
                await publicBlob.DownloadToAsync(ms);
                ms.Position = 0;

                // Upload into secure container
                await secureBlob.UploadAsync(ms, overwrite: true);

                return blobName;
            }

            // 1. Model (always required → public)
            string modelImgName;

            try
            {
                var uri = new Uri(req.ModelFileUrl);

                // Already uploaded to Azure (/upload-model)
                if (uri.Host.Contains("blob.core.windows.net"))
                {
                    modelImgName = Path.GetFileName(uri.AbsolutePath);
                }
                else
                {
                    // External image (Wix/CDN/etc)
                    modelImgName = await DownloadToPublicBlob(req.ModelFileUrl);
                }
            }
            catch
            {
                return BadRequest(new { error = "Invalid ModelFileUrl" });
            }

            // 2. Main Image (fallback to model if null → secure)
            string imageName;

            // If NO main image → reuse model filename (no re-upload)
            if (string.IsNullOrWhiteSpace(req.MainImageUrl))
            {
                imageName = await CopyPublicBlobToSecure(modelImgName);
            }
            else
            {
                imageName = await DownloadToSecureBlob(req.MainImageUrl);
            }

            // 3. Logo (optional override → public)
            string logoImgName;

            if (!string.IsNullOrWhiteSpace(req.LogoImageUrl))
            {
                logoImgName = await DownloadToPublicBlob(req.LogoImageUrl);
            }
            else
            {
                logoImgName = ComputeLogoImgName(req);
            }

            // 5) Compute coupon metadata
            var couponType = ComputeCouponType(modelImgName, req.ShopifyLink, req.Description);
            var couponLevel = ComputeCouponLevel(req.Rarity, req.CouponOrCollectible);

            var tosLink = string.IsNullOrWhiteSpace(req.TosLink) ? _defaultTosLink : req.TosLink!;
            var dropName = req.DropName ?? "";
            var shopify = string.IsNullOrWhiteSpace(req.ShopifyLink) ? null : req.ShopifyLink;
            var description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();

            // 6) Insert DB (transaction)
            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();
            await using var tx = await con.BeginTransactionAsync();

            int couponId;
            const string insertCouponSql = @"
            INSERT INTO coupons
            (companyid, logoimgname, modelimgname, imagename,
            coupontype, shopifylink, toslink, couponlevel,
            missionid, description, dropname)
            VALUES
            (@companyid, @logo, @model, @image,
            @type, @shopify, @tos, @level,
            @missionid, @description, @drop)
            RETURNING couponid;";

            await using (var cmd = new NpgsqlCommand(insertCouponSql, con, tx))
            {
                cmd.Parameters.AddWithValue("companyid", companyId);
                cmd.Parameters.AddWithValue("logo", logoImgName);
                cmd.Parameters.AddWithValue("model", modelImgName);
                cmd.Parameters.AddWithValue("image", imageName);
                cmd.Parameters.AddWithValue("type", couponType);
                cmd.Parameters.AddWithValue("shopify", (object?)shopify ?? DBNull.Value);
                cmd.Parameters.AddWithValue("tos", tosLink);
                cmd.Parameters.AddWithValue("level", couponLevel);
                cmd.Parameters.AddWithValue("missionid", req.MissionId);
                cmd.Parameters.AddWithValue("description", (object?)description ?? DBNull.Value);
                cmd.Parameters.AddWithValue("drop", dropName);
                couponId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            // 7) Bump mission coupon total
            const string bumpSql = @"
            UPDATE missions
            SET coupontotal = COALESCE(coupontotal, 0) + 1
            WHERE missionid = @missionid
            AND companyid = @companyid
            RETURNING coupontotal;";

            int newTotal;
            await using (var bump = new NpgsqlCommand(bumpSql, con, tx))
            {
                bump.Parameters.AddWithValue("missionid", req.MissionId);
                bump.Parameters.AddWithValue("companyid", companyId);
                newTotal = Convert.ToInt32(await bump.ExecuteScalarAsync());
            }

            // 8) Insert locations
            if (lat.Length > 0)
            {
                const string locSql = @"
            INSERT INTO location (couponid, latitude, longitude, ""LocationName"")
            VALUES (@cid, @lat, @lon, @name);";

                await using var locCmd = new NpgsqlCommand(locSql, con, tx);
                for (int i = 0; i < lat.Length; i++)
                {
                    locCmd.Parameters.Clear();
                    locCmd.Parameters.AddWithValue("cid", couponId);
                    locCmd.Parameters.AddWithValue("lat", lat[i]);
                    locCmd.Parameters.AddWithValue("lon", lon[i]);
                    locCmd.Parameters.AddWithValue("name", names[i] ?? "");
                    await locCmd.ExecuteNonQueryAsync();
                }
            }

            await tx.CommitAsync();

            return Ok(new
            {
                couponId,
                missionId = req.MissionId,
                locationsCreated = lat.Length,
                missionCouponTotal = newTotal
            });
        }

        private static bool IsImageFileName(string fileName)
        {
            var ext = Path.GetExtension(fileName)?.ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif";
        }

        private static string ComputeLogoImgName(CreateCouponWithLocationsJsonRequest req)
        {
            var rarity = req.Rarity?.Trim();

            // Special cases
            if (rarity == "OneOff" || rarity == "Raid")
                return "raredrop.png";

            // Coupon
            if (req.CouponOrCollectible)
                return "standarddrop.png";

            // Collectible
            return "collectibledrop.png";
        }

        private static string ComputeCouponType(string modelImgName, string? shopifyLink, string? description)
        {
            bool hasShopify = !string.IsNullOrWhiteSpace(shopifyLink);
            bool hasDescription = !string.IsNullOrWhiteSpace(description);
            bool modelIsImage = IsImageFileName(modelImgName);

            // --- NO SHOPIFY ---
            if (!hasShopify)
            {
                return modelIsImage ? "ImgGen" : "ModelGen";
            }
            // --- SHOPIFY ONLY ---
            if (hasShopify && !hasDescription)
            {
                return modelIsImage ? "ImgShopify" : "ModelShopify";
            }
            // --- SHOPIFY + DESCRIPTION ---
            return modelIsImage ? "ImgGenShopify" : "ModelGenShopify";
        }

        private static int ComputeCouponLevel(string rarity, bool isCoupon)
        {
            rarity = rarity?.Trim() ?? "Common";

            // Special tiers
            if (rarity == "OneOff")
                return 100;

            if (rarity == "Raid")
                return 99;

            // Base ranges
            int baseValue = 90;

            int offset = rarity switch
            {
                "Ultra" => 4,
                "Legendary" => 3,
                "Epic" => 2,
                "Uncommon" => 1,
                "Common" => 0,
                _ => 0
            };

            return baseValue + offset;
        }

        public sealed class CreateCouponWithLocationsJsonRequest
        {
            public int MissionId { get; set; }

            // Text
            public string? DropName { get; set; }
            public string? ShopifyLink { get; set; }
            public string? TosLink { get; set; }
            public string? Description { get; set; }

            // NEW
            public string Rarity { get; set; } = "";

            public bool CouponOrCollectible { get; set; }

            // Locations
            public double[]? Latitudes { get; set; }
            public double[]? Longitudes { get; set; }
            public string[]? LocationNames { get; set; }

            // Image URLs
            public string? ModelFileUrl { get; set; }
            public string? MainImageUrl { get; set; }
            public string? LogoImageUrl { get; set; }
        }

        [HttpPost("upload-model")]
        public async Task<IActionResult> UploadModel(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return BadRequest("No file uploaded");

            var extension = Path.GetExtension(file.FileName)?.ToLower();

            if (extension != ".glb")
                return BadRequest("Only .glb files allowed");

            var fileName = $"{Guid.NewGuid():N}{extension}";

            var blob = _couponImagesContainer.GetBlobClient(fileName);

            using var stream = file.OpenReadStream();
            await blob.UploadAsync(stream, overwrite: false);

            return Ok(new { url = blob.Uri.ToString() });
        }

        public sealed class CreateMissionJsonRequest
        {
            public string? Name { get; set; }
            public bool IsVisible { get; set; } // new
            public string? LocationName { get; set; }
            public string? Description { get; set; }

            public string? MissionImageUrl { get; set; } // signed Wix URL
        }

        [HttpPost("missions")]
        public async Task<IActionResult> CreateMissionJson([FromBody] CreateMissionJsonRequest req)
        {
            var compClaim = User.FindFirst("company_id")?.Value;
            if (string.IsNullOrWhiteSpace(compClaim) || !int.TryParse(compClaim, out var companyId))
                return Forbid();

            var name = (req.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest(new { error = "Mission name is required." });

            if (string.IsNullOrWhiteSpace(req.MissionImageUrl))
                return BadRequest(new { error = "MissionImageUrl is required." });

            // Download image server-side
            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync(req.MissionImageUrl);

            // Pick a filename (or parse from URL)
            var imgName = $"mission_{Guid.NewGuid():N}.jpeg";
            var blob = _couponImagesContainer.GetBlobClient(imgName);
            await blob.UploadAsync(new MemoryStream(bytes), overwrite: false);

            // Always Active now (ignore frontend)
            const string status = "Active";

            var locationName = req.LocationName ?? "";
            var description = req.Description ?? "";

            // New field from frontend -> DB column
            var isVisible = req.IsVisible;

            await using var con = new NpgsqlConnection(_cs);
            await con.OpenAsync();

            const string sql = @"
            INSERT INTO missions (companyid, name, status, imgname, locationname, description, coupontotal, isvisible)
            VALUES (@companyId, @name, @status, @imgname, @locationname, @description, 0, @isvisible)
            RETURNING missionid;";

            int newMissionId;
            await using (var cmd = new NpgsqlCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("companyId", companyId);
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("status", status);
                cmd.Parameters.AddWithValue("imgname", imgName);
                cmd.Parameters.AddWithValue("locationname", locationName);
                cmd.Parameters.AddWithValue("description", description);
                cmd.Parameters.AddWithValue("isvisible", isVisible);
                newMissionId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            }

            return Ok(new
            {
                missionId = newMissionId,
                companyId,
                name,
                status,
                imgName,
                locationName,
                description,
                couponTotal = 0,
                isVisible
            });
        }
    }
}
