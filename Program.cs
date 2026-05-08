using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using VehiclePortal.Data;
using VehiclePortal.Models;

// MUST be before builder is created.
// Stops JWT middleware remapping "role" to the long WS-Federation URI.
// Without this [Authorize(Roles="SuperAdmin")] silently fails.
System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler.DefaultInboundClaimTypeMap.Clear();
Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler.DefaultInboundClaimTypeMap.Clear();

var builder = WebApplication.CreateBuilder(args);

// ── 1. STARTUP GUARDS ─────────────────────────────────────────────────────────
var jwtSecret = builder.Configuration["Jwt:Secret"];
if (string.IsNullOrWhiteSpace(jwtSecret))
    throw new InvalidOperationException(
        "Jwt:Secret is not set. Run: dotnet user-secrets set \"Jwt:Secret\" \"<key>\"");

if (jwtSecret.Length < 32)
    throw new InvalidOperationException(
        "Jwt:Secret must be at least 32 characters for HmacSha256.");

var jwtIssuer = builder.Configuration["Jwt:Issuer"]
    ?? throw new InvalidOperationException("Jwt:Issuer is not set.");

var jwtAudience = builder.Configuration["Jwt:Audience"]
    ?? throw new InvalidOperationException("Jwt:Audience is not set.");

// ── 2. DATABASE ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

// ── 3. IDENTITY ───────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;

    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.AllowedForNewUsers = true;

    options.User.RequireUniqueEmail = true;
})
.AddEntityFrameworkStores<AppDbContext>()
.AddDefaultTokenProviders();

// ── 4. JWT AUTHENTICATION ─────────────────────────────────────────────────────
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(
                                       Encoding.UTF8.GetBytes(jwtSecret)),
        RoleClaimType = "role",
        NameClaimType = "unique_name",
        ClockSkew = TimeSpan.Zero
    };

    if (builder.Environment.IsDevelopment())
    {
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                Console.WriteLine($"[JWT] FAILED: {ctx.Exception.GetType().Name} - {ctx.Exception.Message}");
                return Task.CompletedTask;
            },
            OnTokenValidated = ctx =>
            {
                Console.WriteLine($"[JWT] OK: {ctx.Principal?.Identity?.Name}");
                return Task.CompletedTask;
            },
            OnForbidden = ctx =>
            {
                Console.WriteLine("[JWT] FORBIDDEN - valid token but wrong role");
                return Task.CompletedTask;
            }
        };
    }
});

// ── 5. AUTHORIZATION POLICIES ─────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", p => p.RequireRole("SuperAdmin"));
    options.AddPolicy("SellerOnly", p => p.RequireRole("Seller"));
    options.AddPolicy("BuyerOnly", p => p.RequireRole("Buyer"));
    options.AddPolicy("MarketplaceUser", p => p.RequireRole("Seller", "Buyer"));
});

// ── 6. CONTROLLERS + SWAGGER ──────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── 7. BUILD ──────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── 8. MIGRATIONS + SEED ──────────────────────────────────────────────────────
await InitialiseDatabaseAsync(app);



// ── 9. MIDDLEWARE PIPELINE ────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();

// ── DATABASE INITIALISATION ───────────────────────────────────────────────────
static async Task InitialiseDatabaseAsync(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    logger.LogInformation("[DB] Applying migrations...");
    await db.Database.MigrateAsync();
    logger.LogInformation("[DB] Migrations applied.");

    string[] roles = ["SuperAdmin", "Seller", "Buyer"];
    foreach (var role in roles)
    {
        if (!await roleManager.RoleExistsAsync(role))
        {
            await roleManager.CreateAsync(new IdentityRole(role));
            logger.LogInformation("[Seed] Created role: {Role}", role);
        }
    }

    var adminEmail = config["InitialAdmin:Email"];
    var adminPass = config["InitialAdmin:Password"];

    if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPass))
    {
        logger.LogWarning("[Seed] InitialAdmin credentials not configured - skipping seed.");
        return;
    }

    var existing = await userManager.FindByEmailAsync(adminEmail);
    if (existing == null)
    {
        var admin = new ApplicationUser
        {
            UserName = adminEmail,
            Email = adminEmail,
            EmailConfirmed = true,
            FirstName = "Super",
            LastName = "Admin",
            Role = "SuperAdmin",
            IsActive = true
        };

        var result = await userManager.CreateAsync(admin, adminPass);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            logger.LogError("[Seed] Failed to create SuperAdmin: {Errors}", errors);
            return;
        }

        await userManager.AddToRoleAsync(admin, "SuperAdmin");
        logger.LogInformation("[Seed] SuperAdmin created: {Email}", adminEmail);
    }
    else
    {
        if (!await userManager.IsInRoleAsync(existing, "SuperAdmin"))
        {
            await userManager.AddToRoleAsync(existing, "SuperAdmin");
            logger.LogInformation("[Seed] SuperAdmin role restored: {Email}", adminEmail);
        }
        else
        {
            logger.LogInformation("[Seed] SuperAdmin already exists: {Email}", adminEmail);
        }
    }
}