using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using VehiclePortal.Data;
using VehiclePortal.Models;
using VehiclePortal.Services;

namespace VehiclePortal.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class PortalController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly AppDbContext _db;
        private readonly IAuditService _audit;
        private readonly IConfiguration _config;
        private readonly ILogger<PortalController> _logger;

        public PortalController(
            UserManager<ApplicationUser> userManager,
            AppDbContext db,
            IAuditService audit,
            IConfiguration config,
            ILogger<PortalController> logger)
        {
            _userManager = userManager;
            _db = db;
            _audit = audit;
            _config = config;
            _logger = logger;
        }

        // ── POST /api/portal/register ─────────────────────────────────────────
        // Public — creates a new Buyer account with email and password.
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] PortalRegisterRequest? request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Email and password are required." });

            var existing = await _userManager.FindByEmailAsync(request.Email);
            if (existing != null)
                return Conflict(new { message = "An account with that email already exists." });

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                EmailConfirmed = true,
                FirstName = request.FirstName?.Trim() ?? string.Empty,
                LastName = request.LastName?.Trim() ?? string.Empty,
                Role = "Buyer",
                IsActive = true
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = string.Join(" ", result.Errors.Select(e => e.Description));
                return BadRequest(new { message = errors });
            }

            await _userManager.AddToRoleAsync(user, "Buyer");

            await _audit.LogAsync(
                userId: user.Id,
                userEmail: user.Email,
                userRole: "Buyer",
                action: AuditActions.UserCreated,
                entityType: "User",
                entityId: user.Id,
                details: $"New Buyer registered: {user.Email}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            _logger.LogInformation("[Portal] New Buyer registered: {Email}", user.Email);

            var roles = await _userManager.GetRolesAsync(user);
            var token = GenerateJwt(user, roles);

            return Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.FirstName,
                    user.LastName,
                    user.Email,
                    Roles = roles
                },
                message = "Account created successfully."
            });
        }

        [HttpGet("config")]
        public IActionResult GetConfig()
        {
            return Ok(new
            {
                googleClientId = _config["Google:ClientId"] ?? string.Empty
            });
        }

        // ── POST /api/portal/google ───────────────────────────────────────────
        // Public — verifies a Google ID token from One Tap.
        // If the user doesn't exist, creates a new Buyer account automatically.
        // If they do exist, logs them in.
        // Returns a JWT token either way.
        [HttpPost("google")]
        public async Task<IActionResult> GoogleSignIn([FromBody] GoogleSignInRequest? request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.IdToken))
                return BadRequest(new { message = "Google ID token is required." });

            var clientId = _config["Google:ClientId"];
            if (string.IsNullOrWhiteSpace(clientId))
                return StatusCode(500, new { message = "Google sign-in is not configured." });

            // ── 1. Verify the Google ID token ─────────────────────────────────
            GoogleJsonWebSignature.Payload googlePayload;
            try
            {
                var settings = new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = new[] { clientId }
                };
                googlePayload = await GoogleJsonWebSignature.ValidateAsync(
                    request.IdToken, settings);
            }
            catch (InvalidJwtException ex)
            {
                _logger.LogWarning("[Portal] Invalid Google token: {Error}", ex.Message);
                return Unauthorized(new { message = "Invalid Google token. Please try again." });
            }

            var email = googlePayload.Email;
            var firstName = googlePayload.GivenName ?? string.Empty;
            var lastName = googlePayload.FamilyName ?? string.Empty;

            // ── 2. Find or create the user ────────────────────────────────────
            var user = await _userManager.FindByEmailAsync(email);
            var isNewUser = user == null;

            if (isNewUser)
            {
                // First time Google sign-in — create a Buyer account
                user = new ApplicationUser
                {
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true, // Google already verified the email
                    FirstName = firstName,
                    LastName = lastName,
                    Role = "Buyer",
                    IsActive = true
                };

                // Google accounts don't need a password —
                // we set a random unguessable one so Identity is happy
                var randomPassword = $"G_{Guid.NewGuid():N}_!Aa1";
                var result = await _userManager.CreateAsync(user, randomPassword);

                if (!result.Succeeded)
                {
                    var errors = string.Join(" ", result.Errors.Select(e => e.Description));
                    _logger.LogError("[Portal] Failed to create Google user: {Errors}", errors);
                    return BadRequest(new { message = "Failed to create account." });
                }

                await _userManager.AddToRoleAsync(user, "Buyer");

                await _audit.LogAsync(
                    userId: user.Id,
                    userEmail: email,
                    userRole: "Buyer",
                    action: AuditActions.UserCreated,
                    entityType: "User",
                    entityId: user.Id,
                    details: $"New Buyer created via Google One Tap: {email}",
                    ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

                _logger.LogInformation(
                    "[Portal] New Buyer created via Google: {Email}", email);
            }
            else
            {
                // Existing user — check they're active
                if (!user!.IsActive)
                    return Unauthorized(new { message = "Your account has been deactivated." });

                _logger.LogInformation(
                    "[Portal] Existing user signed in via Google: {Email}", email);
            }

            await _audit.LogAsync(
                userId: user!.Id,
                userEmail: email,
                userRole: user.Role,
                action: AuditActions.UserLoggedIn,
                entityType: "User",
                entityId: user.Id,
                details: $"Signed in via Google One Tap{(isNewUser ? " (new account)" : "")}",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            var roles = await _userManager.GetRolesAsync(user);
            var token = GenerateJwt(user, roles);

            return Ok(new
            {
                token,
                user = new
                {
                    user.Id,
                    user.FirstName,
                    user.LastName,
                    user.Email,
                    Roles = roles,
                    IsNewUser = isNewUser
                },
                message = isNewUser
                    ? "Account created via Google. Welcome!"
                    : "Signed in via Google."
            });
        }

        // ── POST /api/portal/become-seller ────────────────────────────────────
        // Authenticated — adds Seller role to the current user's account.
        // Returns a NEW JWT with the updated roles immediately.
        [HttpPost("become-seller")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<IActionResult> BecomeSeller()
        {
            var userId = User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
            if (userId == null) return Unauthorized();

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound(new { message = "User not found." });

            if (!user.IsActive)
                return BadRequest(new { message = "Your account has been deactivated." });

            var currentRoles = await _userManager.GetRolesAsync(user);

            if (currentRoles.Contains("Seller"))
            {
                var existingToken = GenerateJwt(user, currentRoles);
                return Ok(new
                {
                    token = existingToken,
                    message = "You already have seller access.",
                    roles = currentRoles
                });
            }

            await _userManager.AddToRoleAsync(user, "Seller");

            user.Role = string.Join(", ", currentRoles.Append("Seller"));
            await _userManager.UpdateAsync(user);

            var updatedRoles = await _userManager.GetRolesAsync(user);
            var newToken = GenerateJwt(user, updatedRoles);

            await _audit.LogAsync(
                userId: user.Id,
                userEmail: user.Email ?? string.Empty,
                userRole: "Buyer",
                action: AuditActions.UserBecameSeller,
                entityType: "User",
                entityId: user.Id,
                details: $"{user.Email} upgraded to Seller role via self-service",
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString());

            _logger.LogInformation("[Portal] {Email} upgraded to Seller role", user.Email);

            return Ok(new
            {
                token = newToken,
                message = "You now have seller access. You can list vehicles for sale.",
                roles = updatedRoles
            });
        }

        // ── GET /api/portal/me ────────────────────────────────────────────────
        [HttpGet("me")]
        [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
        public async Task<IActionResult> GetMe()
        {
            var userId = User.Claims.FirstOrDefault(c => c.Type == "sub")?.Value;
            if (userId == null) return Unauthorized();

            var user = await _userManager.FindByIdAsync(userId);
            if (user == null) return NotFound(new { message = "User not found." });

            var roles = await _userManager.GetRolesAsync(user);

            return Ok(new
            {
                user.Id,
                user.FirstName,
                user.LastName,
                user.Email,
                user.IsActive,
                user.CreatedAt,
                Roles = roles,
                IsSeller = roles.Contains("Seller"),
                IsBuyer = roles.Contains("Buyer")
            });
        }

        // ── JWT generator ─────────────────────────────────────────────────────
        private string GenerateJwt(ApplicationUser user, IList<string> roles)
        {
            var secret = _config["Jwt:Secret"]!;
            var issuer = _config["Jwt:Issuer"]!;
            var audience = _config["Jwt:Audience"]!;

            var claims = new List<Claim>
            {
                new Claim("unique_name", user.Email ?? string.Empty),
                new Claim("sub",         user.Id),
                new Claim("email",       user.Email ?? string.Empty),
                new Claim("firstName",   user.FirstName),
                new Claim("lastName",    user.LastName),
                new Claim("jti",         Guid.NewGuid().ToString())
            };

            foreach (var role in roles)
                claims.Add(new Claim("role", role));

            var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
            var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddHours(8),
                signingCredentials: creds);

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class PortalRegisterRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class GoogleSignInRequest
    {
        public string IdToken { get; set; } = string.Empty;
    }
}