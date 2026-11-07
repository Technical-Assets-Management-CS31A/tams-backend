using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;
using Tams.Api.Settings;
using static Tams.Api.Enrollment.EnrollmentSessions;

namespace Tams.Api.Enrollment;

public sealed record StartEnrollmentRequest(Guid StudentId, Guid StationId, short FingerIndex);

/// <param name="Template">Base64 of the sensor template. Never echoed back or logged.</param>
public sealed record SubmitFingerprintRequest(string? Template, string? SensorModel, short? QualityScore);

public static class EnrollmentEndpoints
{
    static readonly string[] SensorModels = ["AS608", "R307"];
    const int MaxTemplateBytes = 4096;

    public static IEndpointRouteBuilder MapEnrollmentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(""); // the global /api PathBase is applied in Program.cs

        // Staff routes (PRD FR-9.4: admin-initiated enrollment at the kiosk).
        var staff = api.MapGroup("/enrollment/sessions").WithTags("Enrollment");
        staff.MapPost("/", StartSession).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        staff.MapGet("/{id:guid}", GetStaffSession).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        staff.MapPost("/{id:guid}/cancel", StaffCancel).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        var fingers = api.MapGroup("/students/{studentId:guid}/fingerprints").WithTags("Enrollment");
        fingers.MapGet("/", ListFingerprints).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        fingers.MapDelete("/{fingerIndex:int}", DeleteFingerprint).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);

        // Kiosk routes: no user credentials, gated by StationAuth.IsStationAuthenticated.
        // Namespaced under /kiosk/enrollment so they cannot collide with the checkout session
        // routes (/api/kiosk/sessions/...) of PRD 6.2; same pattern, ENROLLMENT sessions only.
        var kiosk = api.MapGroup("/kiosk/enrollment/sessions").WithTags("Enrollment (kiosk)").AllowAnonymous();
        kiosk.MapGet("/{id:guid}", KioskGet);
        kiosk.MapPost("/{id:guid}/fingerprint", KioskSubmit);
        kiosk.MapPost("/{id:guid}/confirm", KioskConfirm);
        kiosk.MapPost("/{id:guid}/cancel", KioskCancel);
        return app;
    }

    // ---------------------------------------------------------------- staff

    static async Task<IResult> StartSession(StartEnrollmentRequest req, ClaimsPrincipal user, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        if (ActorId(user) is not Guid actor) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        if (req.FingerIndex is < 0 or > 9)
            return Problems.BadRequest("INVALID_FINGER_INDEX", "fingerIndex must be between 0 and 9.");

        await ExpireStaleAsync(db, ct);

        var student = await db.Students.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.StudentId && s.DeletedAt == null, ct);
        if (student is null) return Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.");
        if (student.Status != RecordStatus.Active) return Problems.Conflict("STUDENT_INACTIVE", "Student is inactive.");

        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.StationId && s.DeletedAt == null, ct);
        if (station is null) return Problems.NotFound("STATION_NOT_FOUND", "Station not found.");
        if (station.Type != StationType.CounterKiosk)
            return Problems.BadRequest("STATION_NOT_COUNTER_KIOSK", "Enrollment can only run on a counter kiosk.");
        if (station.Status != RecordStatus.Active) return Problems.Conflict("STATION_INACTIVE", "Station is inactive.");

        var now = DateTimeOffset.UtcNow;
        if (await db.CheckoutSessions.AnyAsync(s => s.StudentId == student.Id && s.Type == SessionType.Enrollment
                && (s.Status == SessionStatus.AwaitingFingerprint || s.Status == SessionStatus.AwaitingConfirm), ct))
            return Problems.Conflict("ENROLLMENT_ALREADY_OPEN", "This student already has an open enrollment session.");

        var busy = await db.CheckoutSessions.Where(s => s.StationId == station.Id && s.ExpiresAt > now).Select(s => s.Status).ToListAsync(ct);
        if (busy.Any(IsOpen))
            return Problems.Conflict("STATION_BUSY", "The station already has an open session.");

        if (await db.FingerprintTemplates.AnyAsync(t => t.StudentId == student.Id && t.FingerIndex == req.FingerIndex, ct))
            return Problems.Conflict("FINGER_ALREADY_ENROLLED", "This finger is already enrolled. Delete it first to re-enroll.");

        var session = new CheckoutSession
        {
            Id = Guid.NewGuid(),
            StationId = station.Id,
            Type = SessionType.Enrollment,
            Status = SessionStatus.AwaitingFingerprint,
            InstructorUserId = actor, // the staff member who started it (PRD: card owner or coordinator)
            StudentId = student.Id,
            FingerIndex = req.FingerIndex,
            LcdLines = LcdJson.Write(Lcd(SessionStatus.AwaitingFingerprint, student, req.FingerIndex)),
            CreatedAt = now,
            ExpiresAt = now + await TimeoutAsync(settings, ct),
        };
        db.CheckoutSessions.Add(session);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            // Lost a race with another start: the partial unique indexes are the backstop for the pre-checks above.
            if (pg.ConstraintName == "ux_checkout_sessions_open_enrollment_student")
                return Problems.Conflict("ENROLLMENT_ALREADY_OPEN", "This student already has an open enrollment session.");
            if (pg.ConstraintName == "ux_checkout_sessions_open_station")
                return Problems.Conflict("STATION_BUSY", "The station already has an open session.");
            throw;
        }
        return Results.Created($"/api/enrollment/sessions/{session.Id}", StaffView(session));
    }

    static async Task<IResult> GetStaffSession(Guid id, TamsDbContext db, CancellationToken ct)
    {
        await ExpireStaleAsync(db, ct, id);
        var s = await FindAsync(db, id, ct);
        return s is null ? SessionNotFound() : Results.Ok(StaffView(s));
    }

    static async Task<IResult> StaffCancel(Guid id, TamsDbContext db, CancellationToken ct)
    {
        await ExpireStaleAsync(db, ct, id);
        var s = await FindAsync(db, id, ct);
        return s is null ? SessionNotFound() : await CancelAsync(db, s, ct, StaffView);
    }

    static async Task<IResult> ListFingerprints(Guid studentId, TamsDbContext db, CancellationToken ct)
    {
        if (!await db.Students.AnyAsync(s => s.Id == studentId && s.DeletedAt == null, ct))
            return Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.");
        // Projection excludes the template column on purpose.
        var items = await db.FingerprintTemplates.AsNoTracking()
            .Where(t => t.StudentId == studentId).OrderBy(t => t.FingerIndex)
            .Select(t => new { t.FingerIndex, t.SensorModel, t.QualityScore, t.EnrolledByUserId, t.EnrolledStationId, EnrolledAt = t.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { studentId, items });
    }

    static async Task<IResult> DeleteFingerprint(Guid studentId, int fingerIndex, ClaimsPrincipal user, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (ActorId(user) is not Guid actor) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        if (fingerIndex is < 0 or > 9)
            return Problems.BadRequest("INVALID_FINGER_INDEX", "fingerIndex must be between 0 and 9.");

        await ExpireStaleAsync(db, ct);
        if (await db.CheckoutSessions.AnyAsync(s => s.StudentId == studentId && s.Type == SessionType.Enrollment
                && (s.Status == SessionStatus.AwaitingFingerprint || s.Status == SessionStatus.AwaitingConfirm), ct))
            return Problems.Conflict("ENROLLMENT_ALREADY_OPEN", "Finish or cancel the student's open enrollment session first.");

        var t = await db.FingerprintTemplates.AsNoTracking()
            .Where(x => x.StudentId == studentId && x.FingerIndex == fingerIndex)
            .Select(x => new { x.SensorModel, x.QualityScore, x.EnrolledStationId }).FirstOrDefaultAsync(ct);
        if (t is null) return Problems.NotFound("FINGERPRINT_NOT_FOUND", "No such enrolled finger for this student.");

        await RemoveStoredTemplateAsync(db, studentId, (short)fingerIndex, ct);
        await WriteAuditAsync(db, http, actor, null, "FINGERPRINT_DELETED", studentId,
            JsonSerializer.Serialize(new { fingerIndex, t.SensorModel, t.QualityScore, stationId = t.EnrolledStationId }), null, ct);
        return Results.NoContent();
    }

    // ---------------------------------------------------------------- kiosk

    internal static async Task<IResult> KioskGet(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var (s, deny) = await KioskSessionAsync(id, http, db, ct);
        return deny ?? Results.Ok(KioskView(s!));
    }

    internal static async Task<IResult> KioskSubmit(Guid id, SubmitFingerprintRequest req, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        var (s, deny) = await KioskSessionAsync(id, http, db, ct);
        if (deny is not null) return deny;
        if (s!.Status != SessionStatus.AwaitingFingerprint) return InvalidState(s.Status, "submit a fingerprint");

        var errors = new Dictionary<string, string[]>();
        byte[] plain = [];
        if (string.IsNullOrWhiteSpace(req.Template)) errors["template"] = ["Required (base64)."];
        else
        {
            var buf = new byte[req.Template.Length];
            if (!Convert.TryFromBase64String(req.Template, buf, out var n)) errors["template"] = ["Not valid base64."];
            else if (n is 0 or > MaxTemplateBytes) errors["template"] = [$"Must be 1 to {MaxTemplateBytes} bytes."];
            else plain = buf[..n];
        }
        if (req.SensorModel is null || !SensorModels.Contains(req.SensorModel))
            errors["sensorModel"] = [$"Must be one of {string.Join(", ", SensorModels)}."];
        if (req.QualityScore is < 0 or > 1000) errors["qualityScore"] = ["Must be between 0 and 1000."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var fingerIndex = FingerOf(s);
        var studentId = s.StudentId!.Value;
        if (await db.FingerprintTemplates.AnyAsync(t => t.StudentId == studentId && t.FingerIndex == fingerIndex, ct))
            return Problems.Conflict("FINGER_ALREADY_ENROLLED", "This finger is already enrolled.");

        var student = await db.Students.AsNoTracking().FirstAsync(x => x.Id == studentId, ct);
        var lcd = LcdJson.Write(Lcd(SessionStatus.AwaitingConfirm, student, fingerIndex));
        var expires = DateTimeOffset.UtcNow + await TimeoutAsync(settings, ct); // fresh window to press OK
        var cipher = TemplateProtector.Protect(http.RequestServices, plain);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.CheckoutSessions
            .Where(x => x.Id == id && x.Status == SessionStatus.AwaitingFingerprint)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SessionStatus.AwaitingConfirm)
                .SetProperty(x => x.LcdLines, lcd)
                .SetProperty(x => x.ExpiresAt, expires), ct);
        if (claimed == 0) return InvalidState(SessionStatus.AwaitingConfirm, "submit a fingerprint");

        db.FingerprintTemplates.Add(new FingerprintTemplate
        {
            Id = Guid.NewGuid(),
            StudentId = studentId,
            FingerIndex = fingerIndex,
            Template = cipher,
            SensorModel = req.SensorModel!,
            QualityScore = req.QualityScore,
            EnrolledByUserId = s.InstructorUserId!.Value,
            EnrolledStationId = s.StationId,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problems.Conflict("FINGER_ALREADY_ENROLLED", "This finger is already enrolled."); // tx rolls back on dispose
        }

        var fresh = await FindAsync(db, id, ct);
        return Results.Ok(KioskView(fresh!));
    }

    internal static async Task<IResult> KioskConfirm(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var (s, deny) = await KioskSessionAsync(id, http, db, ct);
        if (deny is not null) return deny;
        if (s!.Status != SessionStatus.AwaitingConfirm) return InvalidState(s.Status, "confirm");

        var fingerIndex = FingerOf(s);
        var student = await db.Students.AsNoTracking().FirstAsync(x => x.Id == s.StudentId, ct);
        var lcd = LcdJson.Write(Lcd(SessionStatus.Completed, student, fingerIndex));
        var now = DateTimeOffset.UtcNow;
        var claimed = await db.CheckoutSessions
            .Where(x => x.Id == id && x.Status == SessionStatus.AwaitingConfirm)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SessionStatus.Completed)
                .SetProperty(x => x.LcdLines, lcd)
                .SetProperty(x => x.CompletedAt, now), ct);
        if (claimed == 0) return InvalidState(SessionStatus.Completed, "confirm");

        var tpl = await db.FingerprintTemplates.AsNoTracking()
            .Where(t => t.StudentId == s.StudentId && t.FingerIndex == fingerIndex)
            .Select(t => new { t.SensorModel, t.QualityScore }).FirstOrDefaultAsync(ct);
        await WriteAuditAsync(db, http, s.InstructorUserId, s.StationId, "FINGERPRINT_ENROLLED", s.StudentId!.Value, null,
            JsonSerializer.Serialize(new { fingerIndex, tpl?.SensorModel, tpl?.QualityScore, sessionId = id }), ct);
        return Results.Ok(KioskView((await FindAsync(db, id, ct))!));
    }

    internal static async Task<IResult> KioskCancel(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var (s, deny) = await KioskSessionAsync(id, http, db, ct);
        return deny ?? await CancelAsync(db, s!, ct, KioskView);
    }

    // ---------------------------------------------------------------- shared

    static async Task<IResult> CancelAsync(TamsDbContext db, CheckoutSession s, CancellationToken ct, Func<CheckoutSession, object> view)
    {
        if (s.Status is not (SessionStatus.AwaitingFingerprint or SessionStatus.AwaitingConfirm))
            return InvalidState(s.Status, "cancel");

        var from = s.Status;
        var fingerIndex = FingerOf(s);
        var student = await db.Students.AsNoTracking().FirstAsync(x => x.Id == s.StudentId, ct);
        var lcd = LcdJson.Write(Lcd(SessionStatus.Cancelled, student, fingerIndex));
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.CheckoutSessions.Where(x => x.Id == s.Id && x.Status == from)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, SessionStatus.Cancelled)
                .SetProperty(x => x.LcdLines, lcd)
                .SetProperty(x => x.CompletedAt, now), ct);
        if (claimed == 0) return InvalidState(SessionStatus.Cancelled, "cancel");
        if (from == SessionStatus.AwaitingConfirm)
            await RemoveStoredTemplateAsync(db, student.Id, fingerIndex, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(view((await FindAsync(db, s.Id, ct))!));
    }

    static async Task<(CheckoutSession? Session, IResult? Deny)> KioskSessionAsync(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var s = await FindAsync(db, id, ct);
        if (s is null) return (null, SessionNotFound());
        var station = await db.Stations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == s.StationId, ct);
        if (station is null || !StationAuth.IsStationAuthenticated(http, station))
            return (null, Problems.Forbidden("STATION_NOT_AUTHENTICATED", "This device is not allowed to use the session."));
        await ExpireStaleAsync(db, ct, id);
        return (await FindAsync(db, id, ct), null);
    }

    static Task<CheckoutSession?> FindAsync(TamsDbContext db, Guid id, CancellationToken ct) =>
        db.CheckoutSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.Type == SessionType.Enrollment, ct);

    static IResult SessionNotFound() => Problems.NotFound("SESSION_NOT_FOUND", "Enrollment session not found.");

    static IResult InvalidState(SessionStatus current, string action) =>
        Problems.Conflict("INVALID_SESSION_STATE", $"Cannot {action} while the session is {current}.");

    static Guid? ActorId(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub"), out var g) ? g : null;

    static object StaffView(CheckoutSession s) => new
    {
        id = s.Id, status = Facilities.Support.Label(s.Status), s.StudentId, s.StationId,
        fingerIndex = FingerOf(s), s.ExpiresAt, s.CreatedAt, s.CompletedAt, s.FailureCode,
    };

    static object KioskView(CheckoutSession s)
    {
        return new
        {
            sessionId = s.Id, type = Facilities.Support.Label(s.Type), status = Facilities.Support.Label(s.Status),
            nextStep = s.Status switch
            {
                SessionStatus.AwaitingFingerprint => "FINGERPRINT", SessionStatus.AwaitingConfirm => "CONFIRM", _ => "DONE",
            },
            fingerIndex = FingerOf(s), lcdLines = LcdJson.Read(s), s.ExpiresAt, finished = !IsOpen(s.Status), failureCode = s.FailureCode,
        };
    }
}
