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
        public string Action { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public string EntityId { get; set; } = string.Empty;

        // Detail — JSON snapshot of what changed
        public string? Details { get; set; }

        // When and where
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public string? IpAddress { get; set; }
    }

    public static class AuditActions
    {
        // ── User actions ──────────────────────────────────────────────────────
        public const string UserCreated = "USER_CREATED";
        public const string UserUpdated = "USER_UPDATED";
        public const string UserDeactivated = "USER_DEACTIVATED";
        public const string UserLoggedIn = "USER_LOGGED_IN";
        public const string UserLoginFailed = "USER_LOGIN_FAILED";
        public const string UserRoleChanged = "USER_ROLE_CHANGED";
        public const string UserBecameSeller = "USER_BECAME_SELLER";

        // ── Listing actions ───────────────────────────────────────────────────
        public const string ListingCreated = "LISTING_CREATED";
        public const string ListingUpdated = "LISTING_UPDATED";
        public const string ListingPublished = "LISTING_PUBLISHED";
        public const string ListingDeleted = "LISTING_DELETED";

        // ── Document actions ──────────────────────────────────────────────────
        public const string DocumentUploaded = "DOCUMENT_UPLOADED";
        public const string DocumentDeleted = "DOCUMENT_DELETED";
        public const string DocumentLinked = "DOCUMENT_LINKED";

        // ── Offer actions ─────────────────────────────────────────────────────
        public const string OfferPlaced = "OFFER_PLACED";
        public const string OfferAccepted = "OFFER_ACCEPTED";
        public const string OfferDeclined = "OFFER_DECLINED";
        public const string OfferCancelled = "OFFER_CANCELLED";
        public const string OfferCompleted = "OFFER_COMPLETED";

        // ── VIN transfer ──────────────────────────────────────────────────────
        public const string VinTransferred = "VIN_TRANSFERRED";
    }
}