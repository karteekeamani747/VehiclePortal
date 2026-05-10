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
        private readonly IAuditService _audit;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            UserManager<ApplicationUser> userManager,
            AppDbContext db,
            IFileStorage storage,
            IAuditService audit,
            ILogger<AdminController> logger)
        {
            _userManager = userManager;
            _db = db;
            _storage = storage;
            _audit = audit;
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

            await _audit.LogAsync(
                userId: GetCurrentUserId() ?? "system",
                userEmail: GetCurrentUserEmail(),
                userRole: "SuperAdmin",
                action: AuditActions.UserCreated,
                entityType: "User",
                entityId: user.Id,
                details: $"Created {request.Role} account for {user.Email}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

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

        // ── PUT /api/admin/users/{id} ─────────────────────────────────────────
        // Edit user details and roles
        [HttpPut("users/{id}")]
        public async Task<IActionResult> UpdateUser(string id, [FromBody] UpdateUserRequest? request)
        {
            if (request == null)
                return BadRequest(new { message = "Request body is required." });

            var callerId = GetCurrentUserId();
            var user = await _userManager.FindByIdAsync(id);
            if (user == null)
                return NotFound(new { message = "User not found." });

            var currentRoles = await _userManager.GetRolesAsync(user);
            var changes = new List<string>();

            // Update name
            if (!string.IsNullOrWhiteSpace(request.FirstName) && request.FirstName != user.FirstName)
            {
                changes.Add($"FirstName: {user.FirstName} → {request.FirstName}");
                user.FirstName = request.FirstName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(request.LastName) && request.LastName != user.LastName)
            {
                changes.Add($"LastName: {user.LastName} → {request.LastName}");
                user.LastName = request.LastName.Trim();
            }

            // Update email
            if (!string.IsNullOrWhiteSpace(request.Email) && request.Email != user.Email)
            {
                var emailExists = await _userManager.FindByEmailAsync(request.Email);
                if (emailExists != null && emailExists.Id != id)
                    return Conflict(new { message = "Email already in use by another account." });

                changes.Add($"Email: {user.Email} → {request.Email}");
                user.Email = request.Email.Trim();
                user.UserName = request.Email.Trim();
            }

            // Update roles
            if (request.Roles != null && request.Roles.Length > 0)
            {
                var validRoles = new[] { "SuperAdmin", "Seller", "Buyer" };
                var invalidRoles = request.Roles.Except(validRoles).ToList();
                if (invalidRoles.Any())
                    return BadRequest(new { message = $"Invalid roles: {string.Join(", ", invalidRoles)}" });

                // Block SuperAdmin from removing their own SuperAdmin role
                if (callerId == id &&
                    currentRoles.Contains("SuperAdmin") &&
                    !request.Roles.Contains("SuperAdmin"))
                    return BadRequest(new
                    {
                        message = "You cannot remove your own SuperAdmin role."
                    });

                // Remove roles no longer in the list
                var rolesToRemove = currentRoles.Except(request.Roles).ToList();
                if (rolesToRemove.Any())
                {
                    await _userManager.RemoveFromRolesAsync(user, rolesToRemove);
                    changes.Add($"Removed roles: {string.Join(", ", rolesToRemove)}");
                }

                // Add new roles
                var rolesToAdd = request.Roles.Except(currentRoles).ToList();
                if (rolesToAdd.Any())
                {
                    await _userManager.AddToRolesAsync(user, rolesToAdd);
                    changes.Add($"Added roles: {string.Join(", ", rolesToAdd)}");
                }

                // Update the Role property on the user
                user.Role = string.Join(", ", request.Roles);
            }

            var updateResult = await _userManager.UpdateAsync(user);
            if (!updateResult.Succeeded)
            {
                var errors = string.Join(" ", updateResult.Errors.Select(e => e.Description));
                return BadRequest(new { message = errors });
            }

            if (changes.Any())
            {
                await _audit.LogAsync(
                    userId: callerId ?? "system",
                    userEmail: GetCurrentUserEmail(),
                    userRole: "SuperAdmin",
                    action: AuditActions.UserUpdated,
                    entityType: "User",
                    entityId: user.Id,
                    details: $"Updated user {user.Email}: {string.Join("; ", changes)}",
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());
            }

            var updatedRoles = await _userManager.GetRolesAsync(user);

            return Ok(new
            {
                user.Id,
                user.FirstName,
                user.LastName,
                user.Email,
                user.IsActive,
                Roles = updatedRoles,
                message = "User updated successfully. Role changes take effect on next login."
            });
        }

        // ── DELETE /api/admin/users/{id} ──────────────────────────────────────
        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeactivateUser(string id)
        {
            var callerId = GetCurrentUserId();
            var caller = callerId != null ? await _userManager.FindByIdAsync(callerId) : null;

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

            await _audit.LogAsync(
                userId: callerId ?? "system",
                userEmail: GetCurrentUserEmail(),
                userRole: "SuperAdmin",
                action: AuditActions.UserDeactivated,
                entityType: "User",
                entityId: user.Id,
                details: $"Deactivated account: {user.Email}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            _logger.LogInformation("[Admin] Deactivated user: {Email}", user.Email);

            return Ok(new { message = $"User {user.Email} has been deactivated." });
        }

        // ── GET /api/admin/documents ──────────────────────────────────────────
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
        [HttpDelete("documents/{id:int}")]
        public async Task<IActionResult> DeleteDocument(int id)
        {
            var document = await _db.Documents.FirstOrDefaultAsync(d => d.Id == id);
            if (document == null)
                return NotFound(new { message = "Document not found." });

            var fileName = document.FileName;
            await _storage.DeleteAsync(document.FilePath);
            _db.Documents.Remove(document);
            await _db.SaveChangesAsync();

            await _audit.LogAsync(
                userId: GetCurrentUserId() ?? "system",
                userEmail: GetCurrentUserEmail(),
                userRole: "SuperAdmin",
                action: AuditActions.DocumentDeleted,
                entityType: "Document",
                entityId: id.ToString(),
                details: $"SuperAdmin deleted document: {fileName}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

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
        [HttpPost("dlq/{id:int}/replay")]
        public async Task<IActionResult> ReplayDeadLetter(int id)
        {
            var dlq = await _db.DeadLetterMessages.FirstOrDefaultAsync(d => d.Id == id);
            if (dlq == null)
                return NotFound(new { message = "Dead letter message not found." });

            if (dlq.IsReplayed)
                return BadRequest(new { message = "Message has already been replayed." });

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

        // ── Helpers ───────────────────────────────────────────────────────────
        private string? GetCurrentUserId() =>
            User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;

        private string GetCurrentUserEmail() =>
            User.Claims.FirstOrDefault(c => c.Type == "unique_name")?.Value ?? "unknown";
    }

    public class CreateUserRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }

    public class UpdateUserRequest
    {
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
        public string[]? Roles { get; set; }
    }
}