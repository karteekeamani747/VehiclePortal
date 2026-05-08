using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using VehiclePortal.Models;

namespace VehiclePortal.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AuthController : ControllerBase
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly IConfiguration _config;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            UserManager<ApplicationUser> userManager,
            SignInManager<ApplicationUser> signInManager,
            IConfiguration config,
            ILogger<AuthController> logger)
        {
            _userManager = userManager;
            _signInManager = signInManager;
            _config = config;
            _logger = logger;
        }

        // ── POST /api/auth/login ───────────────────────────────────────────────
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest? request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password))
                return BadRequest(new { message = "Email and password are required." });

            // Find user by email
            var user = await _userManager.FindByEmailAsync(request.Email);

            // User not found — return generic message (don't reveal if email exists)
            if (user == null)
            {
                _logger.LogWarning("[Auth] Login failed - email not found: {Email}", request.Email);
                return Unauthorized(new { message = "Invalid email or password." });
            }

            // Check if account is deactivated
            if (!user.IsActive)
            {
                _logger.LogWarning("[Auth] Login attempt on deactivated account: {Email}", request.Email);
                return Unauthorized(new { message = "This account has been deactivated." });
            }

            // Check if account is locked out
            if (await _userManager.IsLockedOutAsync(user))
            {
                _logger.LogWarning("[Auth] Login attempt on locked account: {Email}", request.Email);
                return Unauthorized(new { message = "Account is locked. Try again in 15 minutes." });
            }

            // Verify password
            var passwordValid = await _userManager.CheckPasswordAsync(user, request.Password);
            if (!passwordValid)
            {
                // Increment failed count — triggers lockout after 5 attempts
                await _userManager.AccessFailedAsync(user);
                _logger.LogWarning("[Auth] Invalid password for: {Email}", request.Email);
                return Unauthorized(new { message = "Invalid email or password." });
            }

            // Reset failed count on successful login
            await _userManager.ResetAccessFailedCountAsync(user);

            // Get the user's roles
            var roles = await _userManager.GetRolesAsync(user);

            // Build JWT token
            var token = BuildToken(user, roles);

            _logger.LogInformation("[Auth] Login successful: {Email} Roles: {Roles}",
                user.Email, string.Join(", ", roles));

            return Ok(new
            {
                token = new JwtSecurityTokenHandler().WriteToken(token),
                expiration = token.ValidTo,
                user = new
                {
                    user.Id,
                    user.Email,
                    user.FirstName,
                    user.LastName,
                    Roles = roles
                }
            });
        }

        // ── POST /api/auth/register ───────────────────────────────────────────
        // Public registration — creates Buyer accounts only.
        // Seller and SuperAdmin accounts are created by SuperAdmin only.
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest? request)
        {
            if (request == null ||
                string.IsNullOrWhiteSpace(request.Email) ||
                string.IsNullOrWhiteSpace(request.Password) ||
                string.IsNullOrWhiteSpace(request.FirstName) ||
                string.IsNullOrWhiteSpace(request.LastName))
                return BadRequest(new { message = "All fields are required." });

            var existing = await _userManager.FindByEmailAsync(request.Email);
            if (existing != null)
                return Conflict(new { message = "An account with that email already exists." });

            var user = new ApplicationUser
            {
                UserName = request.Email,
                Email = request.Email,
                EmailConfirmed = true,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Role = "Buyer",
                IsActive = true
            };

            var result = await _userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
            {
                var errors = result.Errors.Select(e => e.Description);
                return BadRequest(new { message = string.Join(" ", errors) });
            }

            await _userManager.AddToRoleAsync(user, "Buyer");

            _logger.LogInformation("[Auth] New Buyer registered: {Email}", user.Email);

            return Ok(new { message = "Account created successfully. You can now log in." });
        }

        // ── Token builder ─────────────────────────────────────────────────────
        private JwtSecurityToken BuildToken(ApplicationUser user, IList<string> roles)
        {
            var secret = _config["Jwt:Secret"]!;
            var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));

            var claims = new List<Claim>
            {
                // unique_name maps to NameClaimType in Program.cs
                new Claim("unique_name", user.Email!),
                new Claim("sub",         user.Id),
                new Claim("email",       user.Email!),
                new Claim("firstName",   user.FirstName),
                new Claim("lastName",    user.LastName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            };

            // "role" maps to RoleClaimType in Program.cs
            foreach (var role in roles)
                claims.Add(new Claim("role", role));

            return new JwtSecurityToken(
                issuer: _config["Jwt:Issuer"],
                audience: _config["Jwt:Audience"],
                claims: claims,
                expires: DateTime.UtcNow.AddHours(8),
                signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256)
            );
        }
    }

    // ── Request models ────────────────────────────────────────────────────────
    public class LoginRequest
    {
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    public class RegisterRequest
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }
}