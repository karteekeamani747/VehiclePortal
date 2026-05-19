using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using VehiclePortal.Data;
using VehiclePortal.Models;
using VehiclePortal.Services;

namespace VehiclePortal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class ListingController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly IAuditService _audit;
        private readonly ILogger<ListingController> _logger;

        public ListingController(
            AppDbContext db,
            UserManager<ApplicationUser> userManager,
            IAuditService audit,
            ILogger<ListingController> logger)
        {
            _db = db;
            _userManager = userManager;
            _audit = audit;
            _logger = logger;
        }

        // ── GET /api/listing ──────────────────────────────────────────────────
        [HttpGet]
        [AllowAnonymous]
        public async Task<IActionResult> GetListings(
            [FromQuery] string? make = null,
            [FromQuery] string? model = null,
            [FromQuery] int? year = null,
            [FromQuery] string? zipCode = null,
            [FromQuery] decimal? minPrice = null,
            [FromQuery] decimal? maxPrice = null,
            [FromQuery] int? maxMileage = null,
            [FromQuery] string? vin = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _db.Listings
                .Where(l => l.Status == ListingStatus.Active)
                .AsQueryable();

            if (!string.IsNullOrWhiteSpace(make))
                query = query.Where(l => l.Make.ToLower().Contains(make.ToLower()));
            if (!string.IsNullOrWhiteSpace(model))
                query = query.Where(l => l.Model.ToLower().Contains(model.ToLower()));
            if (year.HasValue)
                query = query.Where(l => l.Year == year.Value);
            if (!string.IsNullOrWhiteSpace(zipCode))
                query = query.Where(l => l.ZipCode == zipCode);
            if (minPrice.HasValue)
                query = query.Where(l => l.AskingPrice >= minPrice.Value);
            if (maxPrice.HasValue)
                query = query.Where(l => l.AskingPrice <= maxPrice.Value);
            if (maxMileage.HasValue)
                query = query.Where(l => l.Mileage <= maxMileage.Value);
            if (!string.IsNullOrWhiteSpace(vin))
                query = query.Where(l => l.Vin == vin.ToUpper());

            var total = await query.CountAsync();

            var listings = await query
                .OrderByDescending(l => l.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(l => new
                {
                    l.Id,
                    l.Vin,
                    l.Make,
                    l.Model,
                    l.Year,
                    l.Color,
                    l.Mileage,
                    l.HasAccidents,
                    l.ZipCode,
                    l.AskingPrice,
                    l.Description,
                    l.Status,
                    l.CreatedAt,
                    SellerName = l.Seller != null
                        ? $"{l.Seller.FirstName} {l.Seller.LastName}" : "Unknown"
                })
                .ToListAsync();

            return Ok(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                listings
            });
        }

        // ── GET /api/listing/{id} ─────────────────────────────────────────────
        [HttpGet("{id:int}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetListing(int id)
        {
            var listing = await _db.Listings
                .Include(l => l.Seller)
                .Include(l => l.Documents)
                .FirstOrDefaultAsync(l => l.Id == id && l.Status == ListingStatus.Active);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            return Ok(new
            {
                listing.Id,
                listing.Vin,
                listing.Make,
                listing.Model,
                listing.Year,
                listing.Color,
                listing.Mileage,
                listing.HasAccidents,
                listing.AccidentDetails,
                listing.ZipCode,
                listing.AskingPrice,
                listing.Description,
                listing.Status,
                listing.CreatedAt,
                Seller = listing.Seller != null ? new
                {
                    listing.Seller.FirstName,
                    listing.Seller.LastName
                } : null,
                Documents = listing.Documents.Select(d => new
                {
                    d.Id,
                    d.FileName,
                    d.Type,
                    d.OcrStatus,
                    d.UploadedAt
                })
            });
        }

        // ── GET /api/listing/my ───────────────────────────────────────────────
        [HttpGet("my")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetMyListings()
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var listings = await _db.Listings
                .IgnoreQueryFilters()
                .Where(l => l.SellerId == sellerId)
                .Include(l => l.Documents)
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new
                {
                    l.Id,
                    l.Vin,
                    l.Make,
                    l.Model,
                    l.Year,
                    l.Color,
                    l.Mileage,
                    l.AskingPrice,
                    l.Status,
                    l.IsDeleted,
                    l.CreatedAt,
                    l.UpdatedAt,
                    DocumentCount = l.Documents.Count
                })
                .ToListAsync();

            return Ok(listings);
        }

        // ── POST /api/listing ─────────────────────────────────────────────────
        [HttpPost]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> CreateListing([FromBody] CreateListingRequest? request)
        {
            if (request == null)
                return BadRequest(new { message = "Request body is required." });

            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            // ── Idempotency check ─────────────────────────────────────────────
            var idempotencyKey = Request.Headers["X-Idempotency-Key"].ToString();
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = await _db.IdempotencyLogs
                    .FirstOrDefaultAsync(i => i.IdempotencyKey == idempotencyKey
                                           && i.UserId == sellerId
                                           && i.ExpiresAt > DateTime.UtcNow);
                if (existing != null)
                    return StatusCode(existing.StatusCode,
                        JsonSerializer.Deserialize<object>(existing.ResponsePayload));
            }

            if (!IsValidVin(request.Vin))
                return BadRequest(new { message = "Invalid VIN. Must be 17 characters, no I O Q." });

            var duplicateByThisSeller = await _db.Listings
                .AnyAsync(l => l.Vin == request.Vin.ToUpper()
                            && l.SellerId == sellerId
                            && l.Status != ListingStatus.Sold
                            && !l.IsDeleted);

            if (duplicateByThisSeller)
                return Conflict(new { message = "You already have an active listing for this VIN." });

            var activeListingExists = await _db.Listings
                .AnyAsync(l => l.Vin == request.Vin.ToUpper()
                            && l.Status == ListingStatus.Active);

            if (activeListingExists)
                return Conflict(new { message = "This VIN already has an active listing on the marketplace." });

            var listing = new Listing
            {
                SellerId = sellerId,
                Vin = request.Vin.ToUpper().Trim(),
                Make = request.Make.Trim(),
                Model = request.Model.Trim(),
                Year = request.Year,
                Color = request.Color.Trim(),
                Mileage = request.Mileage,
                HasAccidents = request.HasAccidents,
                AccidentDetails = request.AccidentDetails?.Trim(),
                ZipCode = request.ZipCode.Trim(),
                AskingPrice = request.AskingPrice,
                Description = request.Description?.Trim(),
                Status = ListingStatus.Draft,
                VinLocked = false
            };

            var outboxMessage = new OutboxMessage
            {
                EventType = "listing.created",
                Payload = JsonSerializer.Serialize(new
                {
                    ListingId = 0,
                    Vin = listing.Vin,
                    SellerId = sellerId
                }),
                CreatedBy = sellerId,
                Status = OutboxStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(10)
            };

            _db.Listings.Add(listing);
            _db.OutboxMessages.Add(outboxMessage);
            await _db.SaveChangesAsync();

            outboxMessage.Payload = JsonSerializer.Serialize(new
            {
                ListingId = listing.Id,
                Vin = listing.Vin,
                SellerId = sellerId
            });
            await _db.SaveChangesAsync();

            // Audit log
            await _audit.LogAsync(
                userId: sellerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.ListingCreated,
                entityType: "Listing",
                entityId: listing.Id.ToString(),
                details: $"Created draft listing for {listing.Year} {listing.Make} {listing.Model} VIN: {listing.Vin}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            var responsePayload = new { listing.Id, message = "Draft listing created." };

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                _db.IdempotencyLogs.Add(new IdempotencyLog
                {
                    IdempotencyKey = idempotencyKey,
                    UserId = sellerId,
                    Endpoint = "/api/listing",
                    ResponsePayload = JsonSerializer.Serialize(responsePayload),
                    StatusCode = 200
                });
                await _db.SaveChangesAsync();
            }

            _logger.LogInformation("[Listing] Created draft listing {Id} VIN {Vin}", listing.Id, listing.Vin);

            return Ok(responsePayload);
        }

        // ── PUT /api/listing/{id} ─────────────────────────────────────────────
        [HttpPut("{id:int}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> UpdateListing(int id, [FromBody] UpdateListingRequest? request)
        {
            if (request == null)
                return BadRequest(new { message = "Request body is required." });

            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == id && l.SellerId == sellerId);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            if (listing.Status == ListingStatus.Sold)
                return BadRequest(new { message = "Cannot edit a sold listing." });

            if (!string.IsNullOrWhiteSpace(request.Color)) listing.Color = request.Color.Trim();
            if (request.Mileage.HasValue) listing.Mileage = request.Mileage.Value;
            if (request.HasAccidents.HasValue) listing.HasAccidents = request.HasAccidents.Value;
            if (request.AccidentDetails != null) listing.AccidentDetails = request.AccidentDetails.Trim();
            if (!string.IsNullOrWhiteSpace(request.ZipCode)) listing.ZipCode = request.ZipCode.Trim();
            if (request.AskingPrice.HasValue) listing.AskingPrice = request.AskingPrice.Value;
            if (request.Description != null) listing.Description = request.Description.Trim();

            listing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: sellerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.ListingUpdated,
                entityType: "Listing",
                entityId: listing.Id.ToString(),
                details: $"Updated listing for {listing.Year} {listing.Make} {listing.Model}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            return Ok(new { message = "Listing updated." });
        }

        // ── POST /api/listing/{id}/publish ────────────────────────────────────
        [HttpPost("{id:int}/publish")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> PublishListing(int id)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == id && l.SellerId == sellerId);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            if (listing.Status != ListingStatus.Draft)
                return BadRequest(new { message = "Only draft listings can be published." });

            listing.Status = ListingStatus.Active;
            listing.VinLocked = true;
            listing.UpdatedAt = DateTime.UtcNow;

            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = "listing.published",
                Payload = JsonSerializer.Serialize(new
                {
                    ListingId = listing.Id,
                    Vin = listing.Vin,
                    SellerId = sellerId
                }),
                CreatedBy = sellerId,
                Status = OutboxStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(10)
            });

            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: sellerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.ListingPublished,
                entityType: "Listing",
                entityId: listing.Id.ToString(),
                details: $"Published listing for {listing.Year} {listing.Make} {listing.Model} VIN: {listing.Vin}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            _logger.LogInformation("[Listing] Published {Id} VIN {Vin}", id, listing.Vin);

            return Ok(new { message = "Listing published successfully." });
        }

        // ── DELETE /api/listing/{id} ──────────────────────────────────────────
        [HttpDelete("{id:int}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> DeleteListing(int id)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == id && l.SellerId == sellerId);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            if (listing.Status == ListingStatus.Sold)
                return BadRequest(new { message = "Cannot delete a sold listing." });

            listing.IsDeleted = true;
            listing.DeletedAt = DateTime.UtcNow;
            listing.Status = ListingStatus.Inactive;
            listing.VinLocked = false;
            listing.UpdatedAt = DateTime.UtcNow;

            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = "listing.deleted",
                Payload = JsonSerializer.Serialize(new
                {
                    ListingId = listing.Id,
                    Vin = listing.Vin,
                    SellerId = sellerId
                }),
                CreatedBy = sellerId,
                Status = OutboxStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(10)
            });

            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: sellerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.ListingDeleted,
                entityType: "Listing",
                entityId: listing.Id.ToString(),
                details: $"Deleted listing for {listing.Year} {listing.Make} {listing.Model} VIN: {listing.Vin}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            return Ok(new { message = "Listing deleted." });
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string? GetCurrentUserId() =>
            User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

        private string GetCurrentUserEmail() =>
            User.Claims.FirstOrDefault(c => c.Type == "unique_name")?.Value ?? "unknown";

        private static bool IsValidVin(string vin)
        {
            if (string.IsNullOrWhiteSpace(vin)) return false;
            vin = vin.ToUpper().Trim();
            if (vin.Length != 17) return false;
            return vin.All(c => char.IsLetterOrDigit(c) && c != 'I' && c != 'O' && c != 'Q');
        }
        // ── GET /api/listing/prefill/{documentId} ────────────────────────────────────
        // Returns OCR extracted data from a document so the seller can
        // pre-fill the listing creation form without typing manually.
        // Only returns data if OCR is complete and document belongs to seller.
        [HttpGet("prefill/{documentId:int}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetPrefillData(int documentId)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var document = await _db.Documents
                .FirstOrDefaultAsync(d => d.Id == documentId
                                       && d.UploadedBy == sellerId);

            if (document == null)
                return NotFound(new { message = "Document not found." });

            if (document.OcrStatus != OcrStatus.Completed)
                return BadRequest(new
                {
                    message = "OCR has not completed for this document yet.",
                    ocrStatus = document.OcrStatus.ToString()
                });

            if (string.IsNullOrWhiteSpace(document.ExtractedData))
                return Ok(new
                {
                    message = "OCR completed but no vehicle data was extracted.",
                    extractedVin = document.ExtractedVin
                });

            // Deserialize the extracted data JSON
            var extracted = System.Text.Json.JsonSerializer.Deserialize<ExtractedVehicleData>(
                document.ExtractedData,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

            return Ok(new
            {
                documentId = document.Id,
                extractedVin = extracted?.Vin,
                make = extracted?.Make,
                model = extracted?.Model,
                year = extracted?.Year,
                message = "Data extracted from document. Please review before publishing."
            });
        }
    }


    public class CreateListingRequest
    {
        public string Vin { get; set; } = string.Empty;
        public string Make { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int Year { get; set; }
        public string Color { get; set; } = string.Empty;
        public int Mileage { get; set; }
        public bool HasAccidents { get; set; }
        public string? AccidentDetails { get; set; }
        public string ZipCode { get; set; } = string.Empty;
        public decimal AskingPrice { get; set; }
        public string? Description { get; set; }
    }

    public class UpdateListingRequest
    {
        public string? Color { get; set; }
        public int? Mileage { get; set; }
        public bool? HasAccidents { get; set; }
        public string? AccidentDetails { get; set; }
        public string? ZipCode { get; set; }
        public decimal? AskingPrice { get; set; }
        public string? Description { get; set; }
    }

    // Helper class for deserializing OCR data
    public class ExtractedVehicleData
    {
        public string? Vin { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public int? Year { get; set; }
    }
}