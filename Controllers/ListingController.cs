using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using VehiclePortal.Data;
using VehiclePortal.Models;

namespace VehiclePortal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class ListingController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ILogger<ListingController> _logger;

        public ListingController(
            AppDbContext db,
            UserManager<ApplicationUser> userManager,
            ILogger<ListingController> logger)
        {
            _db = db;
            _userManager = userManager;
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
        [HttpGet("{id}")]
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

            // ── 1. Idempotency check ──────────────────────────────────────────
            var idempotencyKey = Request.Headers["X-Idempotency-Key"].ToString();
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = await _db.IdempotencyLogs
                    .FirstOrDefaultAsync(i => i.IdempotencyKey == idempotencyKey
                                           && i.UserId == sellerId
                                           && i.ExpiresAt > DateTime.UtcNow);
                if (existing != null)
                {
                    _logger.LogInformation(
                        "[Listing] Idempotent response for key {Key}", idempotencyKey);
                    return StatusCode(existing.StatusCode,
                        JsonSerializer.Deserialize<object>(existing.ResponsePayload));
                }
            }

            // ── 2. Validate VIN ───────────────────────────────────────────────
            if (!IsValidVin(request.Vin))
                return BadRequest(new { message = "Invalid VIN. Must be 17 characters, no I O Q." });

            // ── 3. Duplicate VIN check ────────────────────────────────────────
            // Prevent the same seller from creating two active listings for the same VIN
            var duplicateByThisSeller = await _db.Listings
                .AnyAsync(l => l.Vin == request.Vin.ToUpper()
                            && l.SellerId == sellerId
                            && l.Status != ListingStatus.Sold
                            && !l.IsDeleted);

            if (duplicateByThisSeller)
                return Conflict(new { message = "You already have an active listing for this VIN." });

            // Prevent any active listing for this VIN across all sellers
            var activeListingExists = await _db.Listings
                .AnyAsync(l => l.Vin == request.Vin.ToUpper()
                            && l.Status == ListingStatus.Active);

            if (activeListingExists)
                return Conflict(new { message = "This VIN already has an active listing on the marketplace." });

            // ── 4. Create listing + outbox in ONE transaction ─────────────────
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
                    ListingId = 0, // updated after SaveChanges
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

            // Update payload with real listing ID
            outboxMessage.Payload = JsonSerializer.Serialize(new
            {
                ListingId = listing.Id,
                Vin = listing.Vin,
                SellerId = sellerId
            });
            await _db.SaveChangesAsync();

            // ── 5. Store idempotency result ───────────────────────────────────
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
        [HttpPut("{id}")]
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

            if (!string.IsNullOrWhiteSpace(request.Color))
                listing.Color = request.Color.Trim();
            if (request.Mileage.HasValue)
                listing.Mileage = request.Mileage.Value;
            if (request.HasAccidents.HasValue)
                listing.HasAccidents = request.HasAccidents.Value;
            if (request.AccidentDetails != null)
                listing.AccidentDetails = request.AccidentDetails.Trim();
            if (!string.IsNullOrWhiteSpace(request.ZipCode))
                listing.ZipCode = request.ZipCode.Trim();
            if (request.AskingPrice.HasValue)
                listing.AskingPrice = request.AskingPrice.Value;
            if (request.Description != null)
                listing.Description = request.Description.Trim();

            listing.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            return Ok(new { message = "Listing updated." });
        }

        // ── POST /api/listing/{id}/publish ────────────────────────────────────
        [HttpPost("{id}/publish")]
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

            // Outbox event for listing published
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

            _logger.LogInformation("[Listing] Published {Id} VIN {Vin}", id, listing.Vin);

            return Ok(new { message = "Listing published successfully." });
        }

        // ── DELETE /api/listing/{id} ──────────────────────────────────────────
        [HttpDelete("{id}")]
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

            return Ok(new { message = "Listing deleted." });
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string? GetCurrentUserId() =>
            User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

        private static bool IsValidVin(string vin)
        {
            if (string.IsNullOrWhiteSpace(vin)) return false;
            vin = vin.ToUpper().Trim();
            if (vin.Length != 17) return false;
            return vin.All(c => char.IsLetterOrDigit(c) && c != 'I' && c != 'O' && c != 'Q');
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
}