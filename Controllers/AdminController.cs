using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VehiclePortal.Data;
using VehiclePortal.Models;

namespace VehiclePortal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SuperAdmin")]
    public class AdminController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AppDbContext _db;
        private readonly ILogger<AdminController> _logger;

        public AdminController(
            UserManager<ApplicationUser> userManager,
            AppDbContext db,
            ILogger<AdminController> logger)
        {
            _userManager = userManager;
            _db = db;
            _logger = logger;
        }

        // ── GET /api/admin/users ──────────────────────────────────────────────
        // Returns all users with their actual roles from the database.
        [HttpGet("users")]
        public async Task<IActionResult> GetUsers()
        {
            var users = await _userManager.Users.ToListAsync();

            var result = new List<object>();
            foreach (var u in users)
            {
                var roles = await _userManager.GetRolesAsync(u);
                result.Add(new
                {
                    u.Id,
                    u.FirstName,
                    u.LastName,
                    u.Email,
                    u.IsActive,
                    u.CreatedAt,
                    u.DeactivatedAt,
                    Roles = roles
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
        // Creates a new user with a specified role (SuperAdmin or Seller).
        // Buyers self-register via /api/auth/register.
        [HttpPost("users")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest? request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                string.IsNullOrWhiteSpace(request.Role))
                return BadRequest(new { message = "Email, password and role are required." });

            // Only SuperAdmin and Seller can be created this way
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

            // Write audit log
            await WriteAuditAsync(
                action: AuditActions.UserCreated,
                entityType: "User",
                entityId: user.Id,
                details: $"Created {request.Role} account for {user.Email}"
            );

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
        // Soft delete — sets IsActive = false, never hard deletes.
        [HttpDelete("users/{id}")]
        public async Task<IActionResult> DeactivateUser(string id)
        {
            // Prevent self-deactivation
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
                details: $"Deactivated account: {user.Email}"
            );

            _logger.LogInformation("[Admin] Deactivated user: {Email}", user.Email);

            return Ok(new { message = $"User {user.Email} has been deactivated." });
        }

        // ── GET /api/admin/audit ──────────────────────────────────────────────
        // Returns audit logs with optional filters.
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

            var log = new AuditLog
            {
                UserId = caller?.Id ?? "system",
                UserEmail = callerEmail,
                UserRole = string.Join(", ", callerRoles),
                Action = action,
                EntityType = entityType,
                EntityId = entityId,
                Details = details,
                IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString()
            };

            _db.AuditLogs.Add(log);
            await _db.SaveChangesAsync();
        }
    }

    // ── Request models ─────────────────────────────────────────────────────────
    public class CreateUserRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
    }
}