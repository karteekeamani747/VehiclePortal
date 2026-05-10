using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using VehiclePortal.Data;
using VehiclePortal.Models;

// MUST be before builder is created.
// Stops JWT middleware remapping "role" to the long WS-Federation URI.
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

// ── 6. SERVICES ───────────────────────────────────────────────────────────────
builder.Services.AddControllers();

// File storage — swap LocalFileStorage for S3FileStorage in production
builder.Services.AddScoped<VehiclePortal.Services.IFileStorage,
                            VehiclePortal.Services.LocalFileStorage>();

builder.Services.AddScoped<VehiclePortal.Services.IAuditService,
                            VehiclePortal.Services.AuditService>();

// RabbitMQ publisher — singleton so one connection is shared across the app
builder.Services.AddSingleton<VehiclePortal.Services.RabbitMqPublisher>();

// Outbox worker — polls every 10s and publishes pending events to RabbitMQ
builder.Services.AddHostedService<VehiclePortal.Workers.OutboxWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Vehicle Portal API",
        Version = "v1"
    });

    // Define the Bearer security scheme
    c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = Microsoft.OpenApi.ParameterLocation.Header,
        Description = "Paste your JWT token here. Do NOT include the word Bearer."
    });
});

// ── 7. BUILD ──────────────────────────────────────────────────────────────────
var app = builder.Build();

// ── 8. MIGRATIONS + SEED ──────────────────────────────────────────────────────
await InitialiseDatabaseAsync(app);

// ── 9. MIDDLEWARE PIPELINE ────────────────────────────────────────────────────
// Prevent browsers from caching HTML files
// CSS/JS/images can still be cached normally
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var path = ctx.File.Name;
        if (path.EndsWith(".html"))
        {
            ctx.Context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            ctx.Context.Response.Headers["Pragma"] = "no-cache";
            ctx.Context.Response.Headers["Expires"] = "0";
        }
    }
});

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();

    app.UseSwagger(c =>
    {
        c.PreSerializeFilters.Add((swagger, httpReq) =>
        {
            // Add Bearer security to every operation
            foreach (var path in swagger.Paths.Values)
            {
                foreach (var operation in path.Operations.Values)
                {
                    operation.Security ??= new List<Microsoft.OpenApi.OpenApiSecurityRequirement>();
                    if (!operation.Security.Any())
                    {
                        var requirement = new Microsoft.OpenApi.OpenApiSecurityRequirement();
                        requirement.Add(
                            new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer"),
                            new List<string>()
                        );
                        operation.Security.Add(requirement);
                    }
                }
            }
        });
    });

    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Vehicle Portal API v1");
        c.InjectJavascript("/js/swagger-custom.js");
    });
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
    }
    else
    {
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
            }
            else
            {
                await userManager.AddToRoleAsync(admin, "SuperAdmin");
                logger.LogInformation("[Seed] SuperAdmin created: {Email}", adminEmail);
            }
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

    // Seed test accounts in Development only
    if (app.Environment.IsDevelopment())
    {
        var sellerEmail = config["TestSeller:Email"];
        var sellerPass = config["TestSeller:Password"];
        var buyerEmail = config["TestBuyer:Email"];
        var buyerPass = config["TestBuyer:Password"];

        if (!string.IsNullOrWhiteSpace(sellerEmail) && !string.IsNullOrWhiteSpace(sellerPass))
            await SeedTestUserAsync(userManager, sellerEmail, sellerPass,
                "John", "Seller", "Seller", logger);

        if (!string.IsNullOrWhiteSpace(buyerEmail) && !string.IsNullOrWhiteSpace(buyerPass))
            await SeedTestUserAsync(userManager, buyerEmail, buyerPass,
                "Jane", "Buyer", "Buyer", logger);
    }
}

// ── TEST USER SEEDING HELPER ──────────────────────────────────────────────────
static async Task SeedTestUserAsync(
    UserManager<ApplicationUser> userManager,
    string email,
    string password,
    string firstName,
    string lastName,
    string role,
    ILogger logger)
{
    var existing = await userManager.FindByEmailAsync(email);
    if (existing != null) return;

    var user = new ApplicationUser
    {
        UserName = email,
        Email = email,
        EmailConfirmed = true,
        FirstName = firstName,
        LastName = lastName,
        Role = role,
        IsActive = true
    };

    var result = await userManager.CreateAsync(user, password);
    if (result.Succeeded)
    {
        await userManager.AddToRoleAsync(user, role);
        logger.LogInformation("[Seed] Created test {Role}: {Email}", role, email);
    }
    else
    {
        var errors = string.Join(", ", result.Errors.Select(e => e.Description));
        logger.LogError("[Seed] Failed to create test {Role}: {Errors}", role, errors);
    }
}   