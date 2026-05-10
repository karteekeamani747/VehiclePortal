namespace VehiclePortal.Models
{
    /// <summary>
    /// Represents a buyer's offer on a listing.
    ///
    /// State machine:
    ///   Placed → Accepted → Completed (VIN transferred, listing marked Sold)
    ///   Placed → Declined  (seller declines)
    ///   Placed → Cancelled (buyer cancels before seller responds)
    ///   Accepted → Cancelled (buyer cancels after acceptance, before completion)
    /// </summary>
    public class Offer
    {
        public int Id { get; set; }
        public int ListingId { get; set; }
        public string BuyerId { get; set; } = string.Empty;
        public string SellerId { get; set; } = string.Empty;

        // ── Offer details ─────────────────────────────────────────────────────
        public decimal Amount { get; set; }
        public string? Message { get; set; }  // optional note from buyer

        // ── State machine ─────────────────────────────────────────────────────
        public OfferStatus Status { get; set; } = OfferStatus.Placed;

        // Reason provided when declining or cancelling
        public string? DeclineReason { get; set; }
        public string? CancelReason { get; set; }

        // ── Idempotency ───────────────────────────────────────────────────────
        public string? IdempotencyKey { get; set; }

        // ── Timestamps ────────────────────────────────────────────────────────
        public DateTime PlacedAt { get; set; } = DateTime.UtcNow;
        public DateTime? AcceptedAt { get; set; }
        public DateTime? DeclinedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public DateTime? CompletedAt { get; set; }

        // ── Navigation ────────────────────────────────────────────────────────
        public Listing? Listing { get; set; }
        public ApplicationUser? Buyer { get; set; }
        public ApplicationUser? Seller { get; set; }
    }

    public enum OfferStatus
    {
        Placed = 0,  // Buyer placed the offer, waiting for seller
        Accepted = 1,  // Seller accepted, waiting for completion
        Declined = 2,  // Seller declined
        Cancelled = 3,  // Buyer cancelled
        Completed = 4   // Purchase complete — VIN transferred
    }
}