namespace VehiclePortal.Models
{
    public class Document
    {
        public int Id { get; set; }
        public int? ListingId { get; set; }
        public string UploadedBy { get; set; } = string.Empty;

        // ── File info ─────────────────────────────────────────────────────────
        public string FileName { get; set; } = string.Empty;
        public string StoredName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string ContentType { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }

        // MD5 hash of file contents — used for duplicate detection
        // and file integrity verification after upload
        public string? FileHash { get; set; }

        // Idempotency key sent by client — prevents duplicate uploads
        // on network retry. Nullable because legacy uploads won't have it.
        public string? IdempotencyKey { get; set; }

        // ── Document type ─────────────────────────────────────────────────────
        public DocumentType Type { get; set; } = DocumentType.Other;

        // ── OCR results ───────────────────────────────────────────────────────
        public OcrStatus OcrStatus { get; set; } = OcrStatus.Pending;
        public string? OcrRawText { get; set; }
        public string? ExtractedVin { get; set; }
        public string? ExtractedData { get; set; }
        public string? OcrError { get; set; }

        // ── SFTP support ──────────────────────────────────────────────────────
        public bool IsFromSftp { get; set; } = false;
        public string? SftpSourcePath { get; set; }

        // ── Timestamps ───────────────────────────────────────────────────────
        public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
        public DateTime? ProcessedAt { get; set; }

        // ── Navigation ───────────────────────────────────────────────────────
        public Listing? Listing { get; set; }
    }

    public enum DocumentType
    {
        VehicleTitle = 0,
        Registration = 1,
        InspectionReport = 2,
        InsuranceDocument = 3,
        MaintenanceRecord = 4,
        AccidentReport = 5,
        Other = 99
    }

    public enum OcrStatus
    {
        Pending = 0,
        Processing = 1,
        Completed = 2,
        Failed = 3,
        Skipped = 4
    }
}