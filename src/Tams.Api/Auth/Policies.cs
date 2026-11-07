using Microsoft.AspNetCore.Authorization;

namespace Tams.Api.Auth;

public static class TamsClaims
{
    // "true" when users.is_event_coordinator is set. TAMS-31 must emit it when issuing the JWT.
    public const string EventCoordinator = "is_event_coordinator";
}

// Policy names for endpoints: .RequireAuthorization(Policies.AdminOnly). The role table lives in PERMISSIONS.md.
public static class Policies
{
    public const string AdminOnly = nameof(AdminOnly);
    public const string Staff = nameof(Staff);                          // ADMIN, TECH_STAFF
    public const string AuditStaff = nameof(AuditStaff);                // ADMIN, TECH_STAFF, CUSTODIAN
    public const string StaffOrInstructor = nameof(StaffOrInstructor);  // ADMIN, TECH_STAFF, INSTRUCTOR
    public const string InstructorOnly = nameof(InstructorOnly);
    public const string EventCoordinator = nameof(EventCoordinator);    // staff/instructor flagged as coordinator
    public const string AnyUser = nameof(AnyUser);                      // any signed-in account

    // Routes that may be anonymous (matched without the optional /api prefix). Single source of truth
    // for the coverage guard; endpoints must still opt in with .AllowAnonymous().
    // /kiosk/* is device traffic: the station is identified by station_code and checked per route
    // (PRD 6.2, 10.2), not by a user JWT.
    // Room stations post scans and heartbeats to /stations/{code}/... (PRD 6.3), checked by StationAuth.IsDeviceAllowed.
    public static readonly string[] AnonymousRoutes =
        ["/health", "/auth/login", "/auth/refresh", "/openapi", "/scalar", "/kiosk",
         "/stations/{code}/scans", "/stations/{code}/heartbeat"];

    public static IServiceCollection AddTamsPolicies(this IServiceCollection services) =>
        services.AddAuthorization(o =>
        {
            // Default deny: an endpoint with no explicit metadata still requires a signed-in user.
            o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

            o.AddPolicy(AdminOnly, p => p.RequireRole(Roles.Admin));
            o.AddPolicy(Staff, p => p.RequireRole(Roles.Admin, Roles.TechStaff));
            o.AddPolicy(AuditStaff, p => p.RequireRole(Roles.Admin, Roles.TechStaff, Roles.Custodian));
            o.AddPolicy(StaffOrInstructor, p => p.RequireRole(Roles.Admin, Roles.TechStaff, Roles.Instructor));
            o.AddPolicy(InstructorOnly, p => p.RequireRole(Roles.Instructor));
            o.AddPolicy(EventCoordinator, p => p
                .RequireRole(Roles.Admin, Roles.TechStaff, Roles.Instructor)
                .RequireClaim(TamsClaims.EventCoordinator, "true"));
            o.AddPolicy(AnyUser, p => p.RequireAuthenticatedUser());
        });
}
