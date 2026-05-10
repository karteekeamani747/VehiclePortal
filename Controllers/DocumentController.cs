using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using VehiclePortal.Data;
using VehiclePortal.Models;
using VehiclePortal.Services;

namespace VehiclePortal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class DocumentController : ControllerBase
    {
        private readonly AppDbContext _db;
        private readonly IFileStorage _storage;
        private readonly IAuditService _audit;
        private readonly ILogger<DocumentController> _logger;

        private static readonly string[] AllowedContentTypes =
        [
            "application/pdf",
            "image/jpeg",
            "image/jpg",
            "image/png",
            "image/webp"
        ];

        private const long MaxFileSizeBytes = 10 * 1024 * 1024;

        public DocumentController(
            AppDbContext db,
            IFileStorage storage,
            IAuditService audit,
            ILogger<DocumentController> logger)
        {
            _db = db;
            _storage = storage;
            _audit = audit;
            _logger = logger;
        }

        // ── POST /api/document/upload ─────────────────────────────────────────
        [HttpPost("upload")]
        [Authorize(Policy = "SellerOnly")]
        [RequestSizeLimit(10 * 1024 * 1024)]
        public async Task<IActionResult> Upload([FromForm] UploadDocumentRequest request)
        {
            var uploaderId = GetCurrentUserId();
            if (uploaderId == null) return Unauthorized();

            var file = request.File;
            var listingId = request.ListingId;
            var type = request.Type;

            // ── 1. Idempotency check ──────────────────────────────────────────
            var idempotencyKey = Request.Headers["X-Idempotency-Key"].ToString();
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = await _db.IdempotencyLogs
                    .FirstOrDefaultAsync(i => i.IdempotencyKey == idempotencyKey
                                           && i.UserId == uploaderId
                                           && i.ExpiresAt > DateTime.UtcNow);
                if (existing != null)
                {
                    _logger.LogInformation(
                        "[Document] Idempotent response for key {Key}", idempotencyKey);
                    return StatusCode(existing.StatusCode,
                        JsonSerializer.Deserialize<object>(existing.ResponsePayload));
                }
            }

            // ── 2. Validate file ──────────────────────────────────────────────
            if (file == null || file.Length == 0)
                return BadRequest(new { message = "No file provided." });

            if (file.Length > MaxFileSizeBytes)
                return BadRequest(new { message = "File exceeds 10MB limit." });

            if (!AllowedContentTypes.Contains(file.ContentType.ToLower()))
                return BadRequest(new { message = "Only PDF and image files are allowed." });

            // ── 3. Compute MD5 hash ───────────────────────────────────────────
            string fileHash;
            byte[] fileBytes;

            await using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                fileBytes = ms.ToArray();
                fileHash = Convert.ToHexString(MD5.HashData(fileBytes)).ToLower();
            }

            var duplicate = await _db.Documents
                .FirstOrDefaultAsync(d => d.FileHash == fileHash
                                       && d.UploadedBy == uploaderId);
            if (duplicate != null)
            {
                return Conflict(new
                {
                    message = "This file has already been uploaded.",
                    existingDocumentId = duplicate.Id,
                    existingFileName = duplicate.FileName,
                    uploadedAt = duplicate.UploadedAt
                });
            }

            // ── 4. Verify listing belongs to seller ───────────────────────────
            if (listingId.HasValue)
            {
                var listing = await _db.Listings
                    .FirstOrDefaultAsync(l => l.Id == listingId.Value
                                           && l.SellerId == uploaderId);
                if (listing == null)
                    return NotFound(new { message = "Listing not found." });
            }

            // ── 5. Save file ──────────────────────────────────────────────────
            var extension = Path.GetExtension(file.FileName).ToLower();
            var storedName = $"{Guid.NewGuid()}{extension}";

            await using var stream = new MemoryStream(fileBytes);
            var filePath = await _storage.SaveAsync(stream, storedName, file.ContentType);

            // ── 6. Save document + outbox in ONE transaction ──────────────────
            var document = new Document
            {
                ListingId = listingId,
                UploadedBy = uploaderId,
                FileName = file.FileName,
                StoredName = storedName,
                FilePath = filePath,
                ContentType = file.ContentType,
                FileSizeBytes = file.Length,
                FileHash = fileHash,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey)
                                    ? null : idempotencyKey,
                Type = type,
                OcrStatus = OcrStatus.Pending
            };

            var outboxMessage = new OutboxMessage
            {
                EventType = "doc.uploaded",
                Payload = JsonSerializer.Serialize(new
                {
                    DocumentId = 0,
                    FileName = file.FileName,
                    UploadedBy = uploaderId,
                    ListingId = listingId,
                    FileHash = fileHash
                }),
                CreatedBy = uploaderId,
                Status = OutboxStatus.Pending,
                NextRetryAt = DateTime.UtcNow.AddSeconds(10)
            };

            _db.Documents.Add(document);
            _db.OutboxMessages.Add(outboxMessage);
            await _db.SaveChangesAsync();

            outboxMessage.Payload = JsonSerializer.Serialize(new
            {
                DocumentId = document.Id,
                FileName = file.FileName,
                UploadedBy = uploaderId,
                ListingId = listingId,
                FileHash = fileHash
            });
            await _db.SaveChangesAsync();

            // ── 7. Audit log ──────────────────────────────────────────────────
            await _audit.LogAsync(
                userId: uploaderId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.DocumentUploaded,
                entityType: "Document",
                entityId: document.Id.ToString(),
                details: $"Uploaded {file.FileName} ({file.Length} bytes){(listingId.HasValue ? $" linked to listing #{listingId}" : "")}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            // ── 8. Store idempotency result ───────────────────────────────────
            var responsePayload = new
            {
                document.Id,
                document.FileName,
                document.Type,
                document.OcrStatus,
                document.UploadedAt,
                message = "Document uploaded. OCR processing will begin shortly."
            };

            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                _db.IdempotencyLogs.Add(new IdempotencyLog
                {
                    IdempotencyKey = idempotencyKey,
                    UserId = uploaderId,
                    Endpoint = "/api/document/upload",
                    ResponsePayload = JsonSerializer.Serialize(responsePayload),
                    StatusCode = 200
                });
                await _db.SaveChangesAsync();
            }

            _logger.LogInformation(
                "[Document] Uploaded {FileName} ({Size} bytes) Hash: {Hash} Doc ID: {DocId}",
                file.FileName, file.Length, fileHash, document.Id);

            return Ok(responsePayload);
        }

        // ── GET /api/document/mydocuments ─────────────────────────────────────
        [HttpGet("mydocuments")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetMyDocuments(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var uploaderId = GetCurrentUserId();
            if (uploaderId == null) return Unauthorized();

            var query = _db.Documents
                .Where(d => d.UploadedBy == uploaderId)
                .OrderByDescending(d => d.UploadedAt);

            var total = await query.CountAsync();

            var documents = await query
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new
                {
                    d.Id,
                    d.ListingId,
                    d.FileName,
                    d.ContentType,
                    d.FileSizeBytes,
                    d.FileHash,
                    d.Type,
                    d.OcrStatus,
                    d.ExtractedVin,
                    d.UploadedAt,
                    d.ProcessedAt,
                    IsLinked = d.ListingId != null
                })
                .ToListAsync();

            return Ok(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                documents
            });
        }

        // ── GET /api/document/{id} ────────────────────────────────────────────
        [HttpGet("{id:int}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetDocument(int id)
        {
            var uploaderId = GetCurrentUserId();
            if (uploaderId == null) return Unauthorized();

            var document = await _db.Documents
                .FirstOrDefaultAsync(d => d.Id == id && d.UploadedBy == uploaderId);

            if (document == null)
                return NotFound(new { message = "Document not found." });

            return Ok(new
            {
                document.Id,
                document.ListingId,
                document.FileName,
                document.ContentType,
                document.FileSizeBytes,
                document.FileHash,
                document.Type,
                document.OcrStatus,
                document.ExtractedVin,
                document.ExtractedData,
                document.OcrError,
                document.UploadedAt,
                document.ProcessedAt,
                FileUrl = await _storage.GetUrlAsync(document.FilePath)
            });
        }

        // ── GET /api/document/listing/{listingId} ─────────────────────────────
        [HttpGet("listing/{listingId:int}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetListingDocuments(int listingId)
        {
            var uploaderId = GetCurrentUserId();
            if (uploaderId == null) return Unauthorized();

            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == listingId && l.SellerId == uploaderId);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            var documents = await _db.Documents
                .Where(d => d.ListingId == listingId)
                .OrderByDescending(d => d.UploadedAt)
                .Select(d => new
                {
                    d.Id,
                    d.FileName,
                    d.ContentType,
                    d.FileSizeBytes,
                    d.FileHash,
                    d.Type,
                    d.OcrStatus,
                    d.ExtractedVin,
                    d.UploadedAt,
                    d.ProcessedAt
                })
                .ToListAsync();

            return Ok(documents);
        }

        // ── POST /api/document/{id}/link ──────────────────────────────────────
        [HttpPost("{id:int}/link")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> LinkToListing(int id, [FromBody] LinkDocumentRequest request)
        {
            var uploaderId = GetCurrentUserId();
            if (uploaderId == null) return Unauthorized();

            var document = await _db.Documents
                .FirstOrDefaultAsync(d => d.Id == id && d.UploadedBy == uploaderId);

            if (document == null)
                return NotFound(new { message = "Document not found." });

            var listing = await _db.Listings
                .FirstOrDefaultAsync(l => l.Id == request.ListingId && l.SellerId == uploaderId);

            if (listing == null)
                return NotFound(new { message = "Listing not found." });

            document.ListingId = request.ListingId;
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: uploaderId,
                userEmail: GetCurrentUserEmail(),
                userRole: "Seller",
                action: AuditActions.DocumentLinked,
                entityType: "Document",
                entityId: document.Id.ToString(),
                details: $"Linked document {document.FileName} to listing #{request.ListingId}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            return Ok(new { message = "Document linked to listing." });
        }

        // ── DELETE /api/document/{id} ─────────────────────────────────────────
        [HttpDelete("{id:int}")]
        [Authorize(Policy = "MarketplaceUser")]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            var userId = GetCurrentUserId();
            if (userId == null) return Unauthorized();

            var isSuperAdmin = User.IsInRole("SuperAdmin");

            var document = isSuperAdmin
                ? await _db.Documents.FirstOrDefaultAsync(d => d.Id == id)
                : await _db.Documents.FirstOrDefaultAsync(d => d.Id == id
                                                             && d.UploadedBy == userId);

            if (document == null)
                return NotFound(new { message = "Document not found." });

            var fileName = document.FileName;

            await _storage.DeleteAsync(document.FilePath);
            _db.Documents.Remove(document);
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: userId,
                userEmail: GetCurrentUserEmail(),
                userRole: isSuperAdmin ? "SuperAdmin" : "Seller",
                action: AuditActions.DocumentDeleted,
                entityType: "Document",
                entityId: id.ToString(),
                details: $"Deleted document: {fileName}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            _logger.LogInformation(
                "[Document] Deleted document {Id} by {UserId}", id, userId);

            return Ok(new { message = "Document deleted successfully." });
        }

        // ── GET /api/document/file/{filename} ─────────────────────────────────
        [HttpGet("file/{filename}")]
        [Authorize(Policy = "SellerOnly")]
        public async Task<IActionResult> GetFile(string filename)
        {
            var uploaderId = GetCurrentUserId();
            if (uploaderId == null) return Unauthorized();

            var document = await _db.Documents
                .FirstOrDefaultAsync(d => d.StoredName == filename
                                       && d.UploadedBy == uploaderId);

            if (document == null)
                return NotFound(new { message = "File not found." });

            var stream = await _storage.GetAsync(document.FilePath);
            if (stream == null)
                return NotFound(new { message = "File not found in storage." });

            return File(stream, document.ContentType, document.FileName);
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private string? GetCurrentUserId() =>
            User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

        private string GetCurrentUserEmail() =>
            User.Claims.FirstOrDefault(c => c.Type == "unique_name")?.Value ?? "unknown";
    }

    public class UploadDocumentRequest
    {
        public IFormFile File { get; set; } = null!;
        public int? ListingId { get; set; }
        public DocumentType Type { get; set; } = DocumentType.Other;
    }

    public class LinkDocumentRequest
    {
        public int ListingId { get; set; }
    }
}