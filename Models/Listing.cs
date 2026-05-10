using System.Reflection.Metadata;

namespace VehiclePortal.Models
{
    public class Listing
    {
        public int Id { get; set; }
        public string SellerId { get; set; } = string.Empty;

        // ── Vehicle identity ──────────────────────────────────────────────────
        public string Vin { get; set; } = string.Empty;
        public string Make { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int Year { get; set; }
        public string Color { get; set; } = string.Empty;

        // ── Condition ─────────────────────────────────────────────────────────
        public int Mileage { get; set; }
        public bool HasAccidents { get; set; }
        public string? AccidentDetails { get; set; }

        // ── Seller info ───────────────────────────────────────────────────────
        public string ZipCode { get; set; } = string.Empty;
        public decimal AskingPrice { get; set; }
        public string? Description { get; set; }

        // ── Status ────────────────────────────────────────────────────────────
        public ListingStatus Status { get; set; } = ListingStatus.Draft;

        // ── Ownership ─────────────────────────────────────────────────────────
        // VIN is locked to seller when listed, released on purchase
        public bool VinLocked { get; set; } = false;
        public string? BuyerId { get; set; }

        // ── Timestamps ───────────────────────────────────────────────────────
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? DeletedAt { get; set; }
        public bool IsDeleted { get; set; } = false;

        // ── Navigation ────────────────────────────────────────────────────────
        public ApplicationUser? Seller { get; set; }
        public ICollection<Document> Documents { get; set; } = new List<Document>();
    }

    public enum ListingStatus
    {
        Draft = 0,  // Seller created but not published
        Active = 1,  // Published and visible to buyers
        Sold = 2,  // Purchase completed
        Inactive = 3   // Seller deactivated the listing
    }
}