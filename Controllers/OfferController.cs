using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
    public class OfferController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IAuditService _audit;
        private readonly ILogger<OfferController> _logger;

        public OfferController(
            AppDbContext db,
            IAuditService audit,
            ILogger<OfferController> logger)
        {
            _db = db;
            _audit = audit;
            _logger = logger;
        }

        // ── POST /api/offer ───────────────────────────────────────────────────
        // Buyer places an offer on a listing.
        [HttpPost]
        [Authorize(Policy = "BuyerOnly")]
        public async Task<IActionResult> PlaceOffer([FromBody] PlaceOfferRequest? request)
        {
            if (request == null)
                return BadRequest(new { message = "Request body is required." });

            var buyerId = GetCurrentUserId();
            if (buyerId == null) return Unauthorized();

            // ── Idempotency check ─────────────────────────────────────────────
            var idempotencyKey = Request.Headers["X-Idempotency-Key"].ToString();
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existingLog = await _db.IdempotencyLogs
                    .FirstOrDefaultAsync(i => i.IdempotencyKey == idempotencyKey
                                           && i.UserId == buyerId
                                           && i.ExpiresAt > DateTime.UtcNow);
                if (existingLog != null)
                    return StatusCode(existingLog.StatusCode,
                        JsonSerializer.Deserialize<object>(existingLog.ResponsePayload));
            }

            // ── Validate listing ──────────────────────────────────────────────
            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == request.ListingId
                                       && l.Status == ListingStatus.Active);

            if (listing == null)
                return NotFound(new { message = "Listing not found or not active." });

            // Buyer can't offer on their own listing
            if (listing.SellerId == buyerId)
                return BadRequest(new { message = "You cannot make an offer on your own listing." });

            // Check if buyer already has an active offer on this listing
            var existingOffer = await _db.Offers
                .FirstOrDefaultAsync(o => o.ListingId == request.ListingId
                                       && o.BuyerId == buyerId
                                       && (o.Status == OfferStatus.Placed
                                        || o.Status == OfferStatus.Accepted));
            if (existingOffer != null)
                return Conflict(new
                {
                    message = "You already have an active offer on this listing.",
                    existingOfferId = existingOffer.Id,
                    status = existingOffer.Status.ToString()
                });

            if (request.Amount <= 0)
                return BadRequest(new { message = "Offer amount must be greater than zero." });

            // ── Create offer + outbox in ONE transaction ───────────────────────
            var offer = new Offer
            {
                ListingId = request.ListingId,
                BuyerId = buyerId,
                SellerId = listing.SellerId,
                Amount = request.Amount,
                Message = request.Message?.Trim(),
                Status = OfferStatus.Placed,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                                    ? null : idempotencyKey
            };

            var outbox = new OutboxMessage
            {
                EventType = "offer.placed",
                Payload = JsonSerializer.Serialize(new
                {
                    OfferId = 0,
                    ListingId = request.ListingId,
                    BuyerId = buyerId,
                    SellerId = listing.SellerId,
                    Amount = request.Amount
                }),
                CreatedBy = buyerId,
                Status = OutboxStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(10)
            };

            _db.Offers.Add(offer);
            _db.OutboxMessages.Add(outbox);
            await _db.SaveChangesAsync();

            // Update outbox payload with real offer ID
            outbox.Payload = JsonSerializer.Serialize(new
            {
                OfferId = offer.Id,
                ListingId = request.ListingId,
                BuyerId = buyerId,
                SellerId = listing.SellerId,
                Amount = request.Amount
            });
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: buyerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Buyer",
                action: AuditActions.OfferPlaced,
                entityType: "Offer",
                entityId: offer.Id.ToString(),
                details: $"Offer of ${request.Amount} placed on listing #{request.ListingId}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            var responsePayload = new
            {
                offer.Id,
                offer.ListingId,
                offer.Amount,
                offer.Status,
                offer.PlacedAt,
                message = "Offer placed successfully."
            };

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                _db.IdempotencyLogs.Add(new IdempotencyLog
                {
                    IdempotencyKey = idempotencyKey,
                    UserId = buyerId,
                    Endpoint = "/api/offer",
                    ResponsePayload = JsonSerializer.Serialize(responsePayload),
                    StatusCode = 200
                });
                await _db.SaveChangesAsync();
            }

            _logger.LogInformation(
                "[Offer] Placed offer {Id} on listing {ListingId} for ${Amount}",
                offer.Id, request.ListingId, request.Amount);

            return Ok(responsePayload);
        }

        // ── GET /api/offer/my ─────────────────────────────────────────────────
        // Buyer sees all their own offers.
        [HttpGet("my")]
        [Authorize(Policy = "BuyerOnly")]
        public async Task<IActionResult> GetMyOffers()
        {
            var buyerId = GetCurrentUserId();
            if (buyerId == null) return Unauthorized();

            var offers = await _db.Offers
                .Where(o => o.BuyerId == buyerId)
                .Include(o => o.Listing)
                .OrderByDescending(o => o.PlacedAt)
                .Select(o => new
                {
                    o.Id,
                    o.ListingId,
                    o.Amount,
                    o.Message,
                    o.Status,
                    o.PlacedAt,
                    o.AcceptedAt,
                    o.DeclinedAt,
                    o.CancelledAt,
                    o.CompletedAt,
                    o.DeclineReason,
                    Listing = o.Listing == null ? null : new
                    {
                        o.Listing.Vin,
                        o.Listing.Make,
                        o.Listing.Model,
                        o.Listing.Year,
                        o.Listing.AskingPrice
                    }
                })
                .ToListAsync();

            return Ok(offers);
        }

        // ── GET /api/offer/listing/{listingId} ────────────────────────────────
        // Seller sees all offers on one of their listings.
        [HttpGet("listing/{listingId:int}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetListingOffers(int listingId)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == listingId && l.SellerId == sellerId);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            var offers = await _db.Offers
                .Where(o => o.ListingId == listingId)
                .Include(o => o.Buyer)
                .OrderByDescending(o => o.PlacedAt)
                .Select(o => new
                {
                    o.Id,
                    o.Amount,
                    o.Message,
                    o.Status,
                    o.PlacedAt,
                    o.AcceptedAt,
                    o.DeclinedAt,
                    o.CancelledAt,
                    o.CompletedAt,
                    o.DeclineReason,
                    Buyer = o.Buyer == null ? null : new
                    {
                        o.Buyer.FirstName,
                        o.Buyer.LastName,
                        o.Buyer.Email
                    }
                })
                .ToListAsync();

            return Ok(offers);
        }

        // ── POST /api/offer/{id}/accept ───────────────────────────────────────
        // Seller accepts an offer.
        [HttpPost("{id:int}/accept")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> AcceptOffer(int id)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var offer = await _db.Offers
                .Include(o => o.Listing)
                .FirstOrDefaultAsync(o => o.Id == id && o.SellerId == sellerId);

            if (offer == null)
                return NotFound(new { message = "Offer not found." });

            if (offer.Status != OfferStatus.Placed)
                return BadRequest(new { message = $"Cannot accept an offer with status: {offer.Status}." });

            offer.Status = OfferStatus.Accepted;
            offer.AcceptedAt = DateTime.UtcNow;

            // Decline all other active offers on this listing
            var otherOffers = await _db.Offers
                .Where(o => o.ListingId == offer.ListingId
                         && o.Id != offer.Id
                         && o.Status == OfferStatus.Placed)
                .ToListAsync();

            foreach (var other in otherOffers)
            {
                other.Status = OfferStatus.Declined;
                other.DeclinedAt = DateTime.UtcNow;
                other.DeclineReason = "Another offer was accepted.";
            }

            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = "offer.accepted",
                Payload = JsonSerializer.Serialize(new
                {
                    OfferId = offer.Id,
                    ListingId = offer.ListingId,
                    BuyerId = offer.BuyerId,
                    SellerId = sellerId,
                    Amount = offer.Amount
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
                action: AuditActions.OfferAccepted,
                entityType: "Offer",
                entityId: offer.Id.ToString(),
                details: $"Accepted offer of ${offer.Amount} on listing #{offer.ListingId}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            return Ok(new { message = "Offer accepted." });
        }

        // ── POST /api/offer/{id}/decline ──────────────────────────────────────
        // Seller declines an offer.
        [HttpPost("{id:int}/decline")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> DeclineOffer(int id, [FromBody] DeclineOfferRequest? request)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var offer = await _db.Offers
                .FirstOrDefaultAsync(o => o.Id == id && o.SellerId == sellerId);

            if (offer == null)
                return NotFound(new { message = "Offer not found." });

            if (offer.Status != OfferStatus.Placed)
                return BadRequest(new { message = $"Cannot decline an offer with status: {offer.Status}." });

            offer.Status = OfferStatus.Declined;
            offer.DeclinedAt = DateTime.UtcNow;
            offer.DeclineReason = request?.Reason?.Trim();

            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = "offer.declined",
                Payload = JsonSerializer.Serialize(new
                {
                    OfferId = offer.Id,
                    ListingId = offer.ListingId,
                    BuyerId = offer.BuyerId,
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
                action: AuditActions.OfferDeclined,
                entityType: "Offer",
                entityId: offer.Id.ToString(),
                details: $"Declined offer #{offer.Id} on listing #{offer.ListingId}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            return Ok(new { message = "Offer declined." });
        }

        // ── POST /api/offer/{id}/cancel ───────────────────────────────────────
        // Buyer cancels their own offer.
        [HttpPost("{id:int}/cancel")]
        [Authorize(Policy = "BuyerOnly")]
        public async Task<IActionResult> CancelOffer(int id, [FromBody] CancelOfferRequest? request)
        {
            var buyerId = GetCurrentUserId();
            if (buyerId == null) return Unauthorized();

            var offer = await _db.Offers
                .FirstOrDefaultAsync(o => o.Id == id && o.BuyerId == buyerId);

            if (offer == null)
                return NotFound(new { message = "Offer not found." });

            if (offer.Status != OfferStatus.Placed && offer.Status != OfferStatus.Accepted)
                return BadRequest(new { message = $"Cannot cancel an offer with status: {offer.Status}." });

            offer.Status = OfferStatus.Cancelled;
            offer.CancelledAt = DateTime.UtcNow;
            offer.CancelReason = request?.Reason?.Trim();

            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: buyerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Buyer",
                action: AuditActions.OfferCancelled,
                entityType: "Offer",
                entityId: offer.Id.ToString(),
                details: $"Cancelled offer #{offer.Id} on listing #{offer.ListingId}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            return Ok(new { message = "Offer cancelled." });
        }

        // ── POST /api/offer/{id}/complete ─────────────────────────────────────
        // Seller completes the purchase — transfers VIN ownership.
        [HttpPost("{id:int}/complete")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> CompleteOffer(int id)
        {
            var sellerId = GetCurrentUserId();
            if (sellerId == null) return Unauthorized();

            var offer = await _db.Offers
                .Include(o => o.Listing)
                .FirstOrDefaultAsync(o => o.Id == id && o.SellerId == sellerId);

            if (offer == null)
                return NotFound(new { message = "Offer not found." });

            if (offer.Status != OfferStatus.Accepted)
                return BadRequest(new { message = "Only accepted offers can be completed." });

            if (offer.Listing == null)
                return NotFound(new { message = "Listing not found." });

            // ── VIN ownership transfer ────────────────────────────────────────
            // Mark offer as completed
            offer.Status = OfferStatus.Completed;
            offer.CompletedAt = DateTime.UtcNow;

            // Mark listing as Sold, release VIN lock, record buyer
            offer.Listing.Status = ListingStatus.Sold;
            offer.Listing.BuyerId = offer.BuyerId;
            offer.Listing.VinLocked = false;  // VIN released from seller
            offer.Listing.UpdatedAt = DateTime.UtcNow;

            // Outbox event for VIN transfer
            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = "vin.transferred",
                Payload = JsonSerializer.Serialize(new
                {
                    OfferId = offer.Id,
                    ListingId = offer.ListingId,
                    Vin = offer.Listing.Vin,
                    BuyerId = offer.BuyerId,
                    SellerId = sellerId,
                    Amount = offer.Amount
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
                action: AuditActions.OfferCompleted,
                entityType: "Offer",
                entityId: offer.Id.ToString(),
                details: $"Purchase completed. VIN {offer.Listing.Vin} transferred to buyer {offer.BuyerId} for ${offer.Amount}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            await _audit.LogAsync(
                userId: sellerId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.VinTransferred,
                entityType: "Listing",
                entityId: offer.ListingId.ToString(),
                details: $"VIN {offer.Listing.Vin} transferred from seller {sellerId} to buyer {offer.BuyerId}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            _logger.LogInformation(
                "[Offer] Purchase complete. VIN {Vin} transferred. Offer {Id} Listing {ListingId}",
                offer.Listing.Vin, offer.Id, offer.ListingId);

            return Ok(new
            {
                message = "Purchase completed. VIN ownership has been transferred.",
                vin = offer.Listing.Vin,
                listingId = offer.ListingId,
                offerId = offer.Id
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string? GetCurrentUserId() =>
            User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

        private string GetCurrentUserEmail() =>
            User.Claims.FirstOrDefault(c => c.Type == "unique_name")?.Value ?? "unknown";
    }

    public class PlaceOfferRequest
    {
        public int ListingId { get; set; }
        public decimal Amount { get; set; }
        public string? Message { get; set; }
    }

    public class DeclineOfferRequest
    {
        public string? Reason { get; set; }
    }

    public class CancelOfferRequest
    {
        public string? Reason { get; set; }
    }
}