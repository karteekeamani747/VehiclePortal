namespace VehiclePortal.Models
{
    public class AuditLog
    {
        public int Id { get; set; }

        // Who did it
        public string UserId { get; set; } = string.Empty;
        public string UserEmail { get; set; } = string.Empty;
        public string UserRole { get; set; } = string.Empty;

        // What they did
        public string Action { get; set; } = string.Empty; // e.g. "USER_CREATED"
        public string EntityType { get; set; } = string.Empty; // e.g. "User", "Listing"
        public string EntityId { get; set; } = string.Empty; // the ID of the affected record

        // Detail — stores a JSON snapshot of what changed
        public string? Details { get; set; }

        // When and where
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? IpAddress { get; set; }
    }

    // Strongly typed action constants — use these instead of raw strings
    // so a typo can never create a silent audit gap
    public static class AuditActions
    {
        // User actions
        public const string UserCreated = "USER_CREATED";
        public const string UserDeactivated = "USER_DEACTIVATED";
        public const string UserLoggedIn = "USER_LOGGED_IN";
        public const string UserLoginFailed = "USER_LOGIN_FAILED";

        // Listing actions (we'll add more in Phase 2)
        public const string ListingCreated = "LISTING_CREATED";
        public const string ListingUpdated = "LISTING_UPDATED";
        public const string ListingDeleted = "LISTING_DELETED";

        // Offer actions (Phase 3)
        public const string OfferPlaced = "OFFER_PLACED";
        public const string OfferAccepted = "OFFER_ACCEPTED";
        public const string VinTransferred = "VIN_TRANSFERRED";
    }
}