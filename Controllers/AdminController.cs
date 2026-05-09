using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VehiclePortal.Data;
using VehiclePortal.Models;
using VehiclePortal.Services;

namespace VehiclePortal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SuperAdmin")]
    public class AdminController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AppDbContext _db;
        private readonly IFileStorage _storage;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            UserManager<ApplicationUser> userManager,
            AppDbContext db,
            IFileStorage storage,
            ILogger<AdminController> logger)
        {
            _userManager = userManager;
            _db = db;
            _storage = storage;
            _logger = logger;
        }

        // ── GET /api/admin/users ──────────────────────────────────────────────
        [HttpGet("users")]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _userManager.Users.ToListAsync();

            var result = new List<object>();
            foreach (var u in users)
            {
                var roles = await _userManager.GetRolesAsync(u);
                var documentCount = await _db.Documents
                    .CountAsync(d => d.UploadedBy == u.Id);

                result.Add(new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.Email,
                    u.IsActive,
                    u.CreatedAt,
                    u.DeactivatedAt,
                    Roles = roles,
                    DocumentCount = documentCount
                });
            }

            return Ok(result);
        }

        // ── GET /api/admin/users/{id} ─────────────────────────────────────────
        [HttpGet("users/{id}")]
        public async Task<IActionResult> GetUser(string id)
        {
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
                return NotFound(new { message = "User not found." });

            var roles = await _userManager.GetRolesAsync(user);

            return Ok(new
            {
                user.Id,
                user.FirstName,
                user.LastName,
                user.Email,
                user.IsActive,
                user.CreatedAt,
                user.DeactivatedAt,
                Roles = roles
            });
        }

        // ── POST /api/admin/users ─────────────────────────────────────────────
        [HttpPost("users")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest? request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                string.IsNullOrWhiteSpace(request.Role))
                return BadRequest(new { message = "Email, password and role are required." });

            var allowedRoles = new[] { "SuperAdmin", "Seller" };
            if (!allowedRoles.Contains(request.Role))
                return BadRequest(new { message = "Role must be SuperAdmin or Seller." });

            var existing = await _userManager.FindByEmailAsync(request.Email);
            if (existing != null)
                return Conflict(new { message = "A user with that email already exists." });

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                EmailConfirmed = true,
                FirstName = request.FirstName?.Trim() ?? string.Empty,
                LastName = request.LastName?.Trim() ?? string.Empty,
                Role = request.Role,
                IsActive = true
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = string.Join(" ", result.Errors.Select(e => e.Description));
                return BadRequest(new { message = errors });
            }

            await _userManager.AddToRoleAsync(user, request.Role);

            await WriteAuditAsync(
                action: AuditActions.UserCreated,
                entityType: "User",
                entityId: user.Id,
                details: $"Created {request.Role} account for {user.Email}");

            _logger.LogInformation("[Admin] Created {Role}: {Email}", request.Role, user.Email);

            return Ok(new
            {
                user.Id,
                user.Email,
                user.FirstName,
                user.LastName,
                Roles = new[] { request.Role }
            });
        }

        // ── DELETE /api/admin/users/{id} ──────────────────────────────────────
        // Soft delete — sets IsActive = false
        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeactivateUser(string id)
        {
            var callerEmail = User.Identity?.Name;
            var caller = callerEmail != null
                ? await _userManager.FindByEmailAsync(callerEmail)
                : null;

            if (caller?.Id == id)
                return BadRequest(new { message = "You cannot deactivate your own account." });

            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
                return NotFound(new { message = "User not found." });

            if (!user.IsActive)
                return BadRequest(new { message = "User is already deactivated." });

            user.IsActive = false;
            user.DeactivatedAt = DateTime.UtcNow;
            await _userManager.UpdateAsync(user);

            await WriteAuditAsync(
                action: AuditActions.UserDeactivated,
                entityType: "User",
                entityId: user.Id,
                details: $"Deactivated account: {user.Email}");

            _logger.LogInformation("[Admin] Deactivated user: {Email}", user.Email);

            return Ok(new { message = $"User {user.Email} has been deactivated." });
        }

        // ── GET /api/admin/documents ──────────────────────────────────────────
        // SuperAdmin can see ALL documents across all users with filters.
        [HttpGet("documents")]
        public async Task<IActionResult> GetAllDocuments(
            [FromQuery] string? uploadedBy = null,
            [FromQuery] OcrStatus? ocrStatus = null,
            [FromQuery] DocumentType? type = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _db.Documents.AsQueryable();

            if (!string.IsNullOrWhiteSpace(uploadedBy))
                query = query.Where(d => d.UploadedBy == uploadedBy);

            if (ocrStatus.HasValue)
                query = query.Where(d => d.OcrStatus == ocrStatus.Value);

            if (type.HasValue)
                query = query.Where(d => d.Type == type.Value);

            if (from.HasValue)
                query = query.Where(d => d.UploadedAt >= from.Value);

            if (to.HasValue)
                query = query.Where(d => d.UploadedAt <= to.Value);

            var total = await query.CountAsync();

            var documents = await query
                .OrderByDescending(d => d.UploadedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(d => new
                {
                    d.Id,
                    d.ListingId,
                    d.UploadedBy,
                    d.FileName,
                    d.ContentType,
                    d.FileSizeBytes,
                    d.FileHash,
                    d.Type,
                    d.OcrStatus,
                    d.ExtractedVin,
                    d.OcrError,
                    d.UploadedAt,
                    d.ProcessedAt,
                    d.IsFromSftp
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

        // ── DELETE /api/admin/documents/{id} ──────────────────────────────────
        // SuperAdmin can delete any document.
        [HttpDelete("documents/{id:int}")]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id);
            if (document == null)
                return NotFound(new { message = "Document not found." });

            await _storage.DeleteAsync(document.FilePath);
            _db.Documents.Remove(document);
            await _db.SaveChangesAsync();

            _logger.LogInformation("[Admin] Deleted document {Id}", id);

            return Ok(new { message = "Document deleted." });
        }

        // ── GET /api/admin/audit ──────────────────────────────────────────────
        [HttpGet("audit")]
        public async Task<IActionResult> GetAuditLogs(
            [FromQuery] string? userId = null,
            [FromQuery] string? action = null,
            [FromQuery] string? entityType = null,
            [FromQuery] DateTime? from = null,
            [FromQuery] DateTime? to = null,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 50)
        {
            var query = _db.AuditLogs.AsQueryable();

            if (!string.IsNullOrWhiteSpace(userId))
                query = query.Where(a => a.UserId == userId);

            if (!string.IsNullOrWhiteSpace(action))
                query = query.Where(a => a.Action == action);

            if (!string.IsNullOrWhiteSpace(entityType))
                query = query.Where(a => a.EntityType == entityType);

            if (from.HasValue)
                query = query.Where(a => a.CreatedAt >= from.Value);

            if (to.HasValue)
                query = query.Where(a => a.CreatedAt <= to.Value);

            var total = await query.CountAsync();

            var logs = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new
            {
                total,
                page,
                pageSize,
                totalPages = (int)Math.Ceiling((double)total / pageSize),
                logs
            });
        }

        // ── GET /api/admin/dlq ────────────────────────────────────────────────
        // View dead letter queue messages
        [HttpGet("dlq")]
        public async Task<IActionResult> GetDeadLetterMessages(
            [FromQuery] bool includeReplayed = false,
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20)
        {
            var query = _db.DeadLetterMessages.AsQueryable();

            if (!includeReplayed)
                query = query.Where(d => !d.IsReplayed);

            var total = await query.CountAsync();

            var messages = await query
                .OrderByDescending(d => d.CreatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            return Ok(new { total, page, pageSize, messages });
        }

        // ── POST /api/admin/dlq/{id}/replay ───────────────────────────────────
        // Replay a dead letter message by putting it back in the outbox
        [HttpPost("dlq/{id:int}/replay")]
        public async Task<IActionResult> ReplayDeadLetter(int id)
        {
            var dlq = await _db.DeadLetterMessages.FirstOrDefaultAsync(d => d.Id == id);
            if (dlq == null)
                return NotFound(new { message = "Dead letter message not found." });

            if (dlq.IsReplayed)
                return BadRequest(new { message = "Message has already been replayed." });

            // Put back in outbox for retry
            _db.OutboxMessages.Add(new OutboxMessage
            {
                EventType = dlq.EventType,
                Payload = dlq.Payload,
                CreatedBy = dlq.CreatedBy,
                Status = OutboxStatus.Pending,
                NextRetryAt = DateTime.UtcNow
            });

            dlq.IsReplayed = true;
            dlq.ReplayedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            _logger.LogInformation(
                "[Admin] Replayed DLQ message {Id} EventType: {EventType}",
                id, dlq.EventType);

            return Ok(new { message = "Message queued for replay." });
        }

        // ── Audit helper ──────────────────────────────────────────────────────
        private async Task WriteAuditAsync(
            string action,
            string entityType,
            string entityId,
            string? details = null)
        {
            var callerEmail = User.Identity?.Name ?? "system";
            var caller = await _userManager.FindByEmailAsync(callerEmail);
            var callerRoles = caller != null
                ? await _userManager.GetRolesAsync(caller)
                : new List<string>();

            _db.AuditLogs.Add(new AuditLog
            {
                UserId = caller?.Id ?? "system",
                UserEmail = callerEmail,
                UserRole = string.Join(", ", callerRoles),
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            });

            await _db.SaveChangesAsync();
        }
    }

    public class CreateUserRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }
}