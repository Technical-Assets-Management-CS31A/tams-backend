using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Assets;
using Tams.Api.Auditing;
using Tams.Api.Data;
using Tams.Api.Facilities;
using Tams.Api.Enrollment;
using Tams.Api.Http;
using Tams.Api.Users;
using Tams.Api.Students;
using Tams.Api.Settings;
using Tams.Api.Qr;
using Tams.Api.Events;
using Tams.Api.Inventory;
using Tams.Api.Kiosk;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Violations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddTamsOpenApi();
builder.Services.AddTamsProblemDetails();
builder.Services.AddTamsAuth(builder.Configuration);
builder.Services.AddTamsPolicies();

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "ConnectionStrings__Default is not set. Copy .env.example to .env and set POSTGRES_PASSWORD.");

builder.Services.AddDbContext<TamsDbContext>(o => o.UseNpgsql(connectionString, PgEnums.MapAll));
builder.Services.AddTamsSettings();
builder.Services.AddHostedService<EnrollmentSweeper>();
builder.Services.AddHostedService<LoanSweeper>();
builder.Services.AddSignalR();

// Fingerprint templates are encrypted with Data Protection; the key ring must survive container
// rebuilds (Compose mounts the dp-keys volume at /app/keys) or stored templates become unreadable.
builder.Services.AddDataProtection()
    .SetApplicationName("tams")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["DataProtection:KeysPath"] ?? "/app/keys"));

// Only Caddy can reach the api container (Compose network), so its X-Forwarded-* headers are trusted:
// clearing the known networks/proxies lets the middleware accept them from the Docker network address.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});

var app = builder.Build();

app.UseForwardedHeaders(); // first: auth, audit IPs and kiosk IP checks need the real client address

// PRD 6.3: endpoints live under /api. PathBase strips the prefix, so /api/x and /x both route,
// which keeps the Compose healthcheck and local curl working. UseRouting must come after it.
app.UsePathBase("/api");
app.UseRouting();

app.UseTamsProblemDetails();
app.UseTamsAuth();
app.MapTamsDocs();

if (app.Environment.IsDevelopment())
{
    // Development only: bring the schema up to date on start. Production applies
    // migrations deliberately (dotnet ef database update or a migration bundle).
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<TamsDbContext>().Database.MigrateAsync();
}

await app.SeedDevelopmentAdminAsync();
app.MapAuthEndpoints();
app.MapUserEndpoints();
app.MapAssetEndpoints();
app.MapStudentEndpoints();
app.MapEnrollmentEndpoints();
app.MapQrEndpoints();
app.MapFacilityEndpoints();
app.MapSettingsEndpoints();
app.MapAuditLogEndpoints();
app.MapKioskEndpoints();
app.MapApprovalEndpoints();
app.MapDeviceEndpoints();
app.MapLoanEndpoints();
app.MapViolationEndpoints();
app.MapEventEndpoints();
app.MapInventoryAuditEndpoints();
app.MapNotificationEndpoints();
app.MapHub<LiveHub>("/hubs/live").RequireAuthorization(Policies.AnyUser); // PRD 6.4

// Compose healthcheck polls this, and it is the first thing to check after a deploy.
app.MapGet("/health", async (TamsDbContext db, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        if (await db.Database.CanConnectAsync(ct))
            return Results.Ok(new { status = "ok", database = "up" });
    }
    catch (Exception ex)
    {
        // The reason goes to the log, not to the response: this endpoint is public.
        logger.LogError(ex, "Health check could not reach PostgreSQL");
    }

    return Results.Json(
        new { status = "degraded", database = "down" },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();

if (app.Environment.IsDevelopment()) AuthorizationCoverage.LogViolations(app); // keep after the last Map* call

app.Run();
