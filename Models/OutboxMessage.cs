namespace VehiclePortal.Models
{
    /// <summary>
    /// The outbox pattern — events are written to this table in the same
    /// database transaction as the business operation. A background worker
    /// then publishes them to RabbitMQ. This guarantees no event is ever
    /// lost even if RabbitMQ is temporarily unavailable.
    /// </summary>
    public class OutboxMessage
    {
        public int Id { get; set; }

        // What kind of event this is e.g. "doc.uploaded", "listing.created"
        public string EventType { get; set; } = string.Empty;

        // JSON payload of the event
        public string Payload { get; set; } = string.Empty;

        // Who triggered this event
        public string CreatedBy { get; set; } = string.Empty;

        // Processing state
        public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

        // How many times we've tried to publish this
        public int RetryCount { get; set; } = 0;

        // Max retries before moving to DLQ
        public const int MaxRetries = 3;

        // Error from last attempt
        public string? LastError { get; set; }

        // When to try next (used for exponential backoff)
        public DateTime? NextRetryAt { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }
    }

    public enum OutboxStatus
    {
        Pending = 0,  // waiting to be published
        Sent = 1,  // successfully published to RabbitMQ
        Failed = 2,  // moved to DLQ after MaxRetries
        Cancelled = 3   // manually cancelled by SuperAdmin
    }

    /// <summary>
    /// Dead Letter Queue — messages that failed after MaxRetries.
    /// SuperAdmin can view and replay these from the admin portal.
    /// </summary>
    public class DeadLetterMessage
    {
        public int Id { get; set; }
        public int OutboxId { get; set; }  // original outbox message
        public string EventType { get; set; } = string.Empty;
        public string Payload { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = string.Empty;
        public string? FailureReason { get; set; }
        public int RetryCount { get; set; }
        public bool IsReplayed { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ReplayedAt { get; set; }
    }

    /// <summary>
    /// Idempotency log — stores the result of idempotent operations.
    /// If the same key is seen again, return the stored result.
    /// Expires after 24 hours.
    /// </summary>
    public class IdempotencyLog
    {
        public int Id { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string Endpoint { get; set; } = string.Empty;

        // JSON of the response we returned the first time
        public string ResponsePayload { get; set; } = string.Empty;
        public int StatusCode { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
    }
}