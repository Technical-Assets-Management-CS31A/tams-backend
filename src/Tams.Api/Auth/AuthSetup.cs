using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Auth;

public static class AuthSetup
{
    const int MinKeyBytes = 32;

    /// <summary>JWT bearer authentication, authorization, token service and password hasher.</summary>
    public static IServiceCollection AddTamsAuth(this IServiceCollection services, IConfiguration config)
    {
        var jwt = config.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        // Fail fast in every environment: Development gets its key from appsettings.Development.json,
        // everything else must supply Jwt__Key.
        if (Encoding.UTF8.GetByteCount(jwt.Key) < MinKeyBytes)
            throw new InvalidOperationException(
                $"Jwt__Key is missing or shorter than {MinKeyBytes} characters. Set a long random value in .env (see .env.example).");

        services.Configure<JwtOptions>(config.GetSection(JwtOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<TokenService>();
        services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(o =>
            {
                o.MapInboundClaims = false; // keep "sub" and "role" as issued
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "sub",
                    RoleClaimType = "role",
                };
                // Browsers cannot set headers on a WebSocket, so the SignalR hub takes ?access_token= (hub path only).
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        var token = ctx.Request.Query["access_token"].ToString();
                        if (token.Length > 0 && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = token;
                        return Task.CompletedTask;
                    },
                };
            });
        services.AddAuthorization();
        return services;
    }

    /// <summary>Call after UseTamsProblemDetails so bare 401/403 render as problem details.</summary>
    public static WebApplication UseTamsAuth(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// Development only: creates the first ADMIN from Bootstrap:AdminEmail / Bootstrap:AdminPassword
    /// when no user with that email exists. No-op when either is unset.
    /// </summary>
    public static async Task SeedDevelopmentAdminAsync(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment()) return;
        var email = app.Configuration["Bootstrap:AdminEmail"]?.Trim();
        var password = app.Configuration["Bootstrap:AdminPassword"];
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password)) return;

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TamsDbContext>();
        if (await db.Users.AnyAsync(u => u.Email == email)) return;

        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            UserName = email,
            NormalizedEmail = email.ToUpperInvariant(),
            NormalizedUserName = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            LockoutEnabled = true,
            EmployeeNo = "ADMIN-0001",
            FirstName = "System",
            LastName = "Administrator",
            Role = UserRole.Admin,
            Status = RecordStatus.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>().HashPassword(user, password);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        app.Logger.LogWarning("Development bootstrap: created ADMIN account {Email}", email);
    }
}
