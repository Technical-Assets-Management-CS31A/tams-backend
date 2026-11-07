using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;
using static Tams.Api.Enrollment.EnrollmentSessions;
using static Tams.Api.Kiosk.KioskSessions;

namespace Tams.Api.Kiosk;

/// <param name="DestinationRoomId">Defaults to the instructor's default room.</param>
/// <param name="ClassLengthMinutes">90, 120, 150 or 180; defaults to class_length_default_minutes.</param>
public sealed record ApproveRequest(Guid? DestinationRoomId, short? ClassLengthMinutes);

/// <summary>
/// F2: the card owner approves or declines a checkout on their phone. Only that user may decide, and
/// not while frozen (FR-2.3). The kiosk learns the outcome by polling its session.
/// </summary>
public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/approvals").WithTags("Approvals").RequireAuthorization(Policies.AnyUser);
        g.MapGet("/pending", Pending);
        g.MapPost("/{sessionId:guid}/approve", Approve);
        g.MapPost("/{sessionId:guid}/decline", Decline);
        g.MapGet("/{sessionId:guid}/photo", Photo);
        return app;
    }

    /// <summary>The approval.requested payload (PRD 6.4), also one item of GET /approvals/pending.</summary>
    public static async Task<object> RequestPayloadAsync(TamsDbContext db, ISettings settings, CheckoutSession s, CancellationToken ct)
    {
        var student = await (
            from x in db.Students.AsNoTracking()
            where x.Id == s.StudentId
            join sec in db.Sections on x.SectionId equals sec.Id
            select new { x.Id, x.StudentNo, fullName = x.FirstName + " " + x.LastName, sectionCode = sec.Code }).FirstAsync(ct);
        var asset = await db.Assets.AsNoTracking().Where(a => a.Id == s.AssetId)
            .Select(a => new { a.Id, a.AssetCode, a.Name }).FirstAsync(ct);
        var instructor = await db.Users.AsNoTracking().Where(u => u.Id == s.InstructorUserId).Select(u => u.DefaultRoomId).FirstOrDefaultAsync(ct);
        return new
        {
            sessionId = s.Id, student, asset, photoUrl = $"/api/approvals/{s.Id}/photo",
            roomOptions = await RoomOptionsAsync(db, ct), defaultRoomId = instructor,
            classLengthOptions = LoanRules.ClassLengths,
            defaultClassLengthMinutes = await settings.GetIntAsync(SettingKeys.ClassLengthDefault, ct),
            requestedAt = s.CreatedAt, s.ExpiresAt,
        };
    }

    static Task<List<object>> RoomOptionsAsync(TamsDbContext db, CancellationToken ct) =>
        db.Rooms.AsNoTracking().Where(r => r.DeletedAt == null && r.Status == RecordStatus.Active && !r.IsStorage)
            .OrderBy(r => r.Code).Select(r => (object)new { r.Id, r.Code, r.Name }).ToListAsync(ct);

    /// <summary>Pending requests for me, plus my freezes so the app can show the banner (FR-2.3, US-06).</summary>
    static async Task<IResult> Pending(ClaimsPrincipal user, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        if (AssetAudit.ActorId(user) is not Guid me) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.CheckoutSessions.AsNoTracking()
            .Where(s => s.InstructorUserId == me && s.Type == SessionType.Checkout && s.Status == SessionStatus.AwaitingApproval && s.ExpiresAt > now)
            .OrderBy(s => s.CreatedAt).ToListAsync(ct);
        var items = new List<object>();
        foreach (var s in sessions) items.Add(await RequestPayloadAsync(db, settings, s, ct));

        var freezes = await (
            from f in db.ApprovalFreezes.AsNoTracking()
            where f.InstructorUserId == me && f.Status == PenaltyStatus.Active
            join l in db.Loans on f.LoanId equals l.Id
            join a in db.Assets on l.AssetId equals a.Id
            from st in db.Students.Where(x => x.Id == l.StudentId).DefaultIfEmpty()
            orderby f.FrozenAt descending
            select new
            {
                f.Id, f.Reason, f.FrozenAt, LoanId = l.Id, l.LoanNo, a.AssetCode, AssetName = a.Name,
                StudentName = st == null ? null : st.FirstName + " " + st.LastName,
            }).ToListAsync(ct);
        return Results.Ok(new
        {
            frozen = freezes.Count > 0,
            freezes = freezes.Select(f => new { f.Id, reason = Facilities.Support.Label(f.Reason), f.FrozenAt, f.LoanId, f.LoanNo, f.AssetCode, f.AssetName, f.StudentName }),
            items,
        });
    }

    static async Task<IResult> Approve(Guid sessionId, ApproveRequest req, ClaimsPrincipal user, HttpContext http,
        TamsDbContext db, ISettings settings, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (AssetAudit.ActorId(user) is not Guid me) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        var minutes = req.ClassLengthMinutes ?? (short)await settings.GetIntAsync(SettingKeys.ClassLengthDefault, ct);
        if (!LoanRules.ClassLengths.Contains(minutes))
            return Problems.Validation(new Dictionary<string, string[]> { ["classLengthMinutes"] = ["Must be 90, 120, 150 or 180."] });
        var roomId = req.DestinationRoomId ?? await db.Users.Where(u => u.Id == me).Select(u => u.DefaultRoomId).FirstOrDefaultAsync(ct);
        var room = roomId is null ? null : await db.Rooms.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == roomId && r.DeletedAt == null && r.Status == RecordStatus.Active && !r.IsStorage, ct);
        if (room is null)
            return Problems.Validation(new Dictionary<string, string[]> { ["destinationRoomId"] = ["Required: an active room that is not the counter."] });

        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, deny) = await LockMineAsync(db, sessionId, me, outbox, now, ct);
        if (deny is not null)
        {
            await db.SaveChangesAsync(ct); // keeps an expiry found on the way
            await tx.CommitAsync(ct);
            await outbox.FlushAsync(hub);
            return deny;
        }
        if (await LoanRules.IsFrozenAsync(db, me, ct))
            return Problems.Conflict(ErrorCodes.InstructorFrozen, "Your approval rights are frozen until staff lift the freeze.");

        var approval = await db.Approvals.FirstAsync(a => a.SessionId == s!.Id, ct);
        approval.Decision = ApprovalDecision.Approved;
        approval.DecidedAt = now;
        approval.DestinationRoomId = room.Id;
        approval.ClassLengthMinutes = minutes;
        approval.DecidedDevice = Device(http);

        var student = await db.Students.AsNoTracking().FirstAsync(x => x.Id == s!.StudentId, ct);
        var asset = await db.Assets.AsNoTracking().FirstAsync(a => a.Id == s!.AssetId, ct);
        var grace = await settings.GetIntAsync(SettingKeys.Grace, ct);
        s!.Status = SessionStatus.AwaitingConfirm;
        s.DestinationRoomId = room.Id;
        s.ClassLengthMinutes = minutes;
        s.ExpiresAt = now.AddSeconds(await settings.GetIntAsync(SettingKeys.IdleTimeout, ct));
        // F1 step 8. The due time shown is an estimate; the loan takes it from the moment Confirm is pressed.
        s.LcdLines = LcdJson.Write(Short(student), ItemLine(asset), $"To: {(room.Name.Length <= 16 ? room.Name : room.Code)}",
            $"Due {LocalTime.Clock(now.AddMinutes(minutes + grace))} OK/X");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { sessionId = s.Id, decision = "APPROVED", destinationRoomId = room.Id, classLengthMinutes = minutes, decidedAt = now });
    }

    static async Task<IResult> Decline(Guid sessionId, ClaimsPrincipal user, HttpContext http, TamsDbContext db,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (AssetAudit.ActorId(user) is not Guid me) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        var (now, outbox) = (DateTimeOffset.UtcNow, new Outbox());
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var (s, deny) = await LockMineAsync(db, sessionId, me, outbox, now, ct);
        if (deny is null)
        {
            var approval = await db.Approvals.FirstAsync(a => a.SessionId == s!.Id, ct);
            approval.Decision = ApprovalDecision.Declined;
            approval.DecidedAt = now;
            approval.DecidedDevice = Device(http);
            await EndAsync(db, s!, SessionStatus.Declined, "APPROVAL_DECLINED", Denied(s!, "APPROVAL_DECLINED"), outbox, now, ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);
        return deny ?? Results.Ok(new { sessionId, decision = "DECLINED", decidedAt = now });
    }

    /// <summary>The checkout photo, for the deciding instructor and staff.</summary>
    static async Task<IResult> Photo(Guid sessionId, ClaimsPrincipal user, TamsDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        var s = await db.CheckoutSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sessionId, ct);
        var staff = user.IsInRole(Roles.Admin) || user.IsInRole(Roles.TechStaff) || user.IsInRole(Roles.Custodian);
        if (s is null || (!staff && s.InstructorUserId != AssetAudit.ActorId(user)))
            return Problems.NotFound("APPROVAL_NOT_FOUND", "Approval request not found.");
        return MediaStore.Serve(cfg, await db.MediaFiles.AsNoTracking().FirstOrDefaultAsync(m => m.Id == s.PhotoFileId, ct));
    }

    /// <summary>Locks a checkout session awaiting my decision; expires it first if the 120 s ran out.</summary>
    static async Task<(CheckoutSession? S, IResult? Deny)> LockMineAsync(TamsDbContext db, Guid sessionId, Guid me,
        Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        var s = await LockAsync(db, sessionId, ct);
        if (s is null || s.Type != SessionType.Checkout || s.InstructorUserId != me)
            return (null, Problems.NotFound("APPROVAL_NOT_FOUND", "No approval request with this id is addressed to you."));
        if (await ExpireIfDueAsync(db, s, outbox, now, ct))
            return (s, Problems.Conflict("APPROVAL_EXPIRED", "The request timed out and the borrow was denied."));
        if (s.Status != SessionStatus.AwaitingApproval)
            return (s, Problems.Conflict("APPROVAL_ALREADY_DECIDED", $"The request is no longer pending ({Facilities.Support.Label(s.Status)})."));
        return (s, null);
    }

    static string? Device(HttpContext http) =>
        http.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua[..Math.Min(ua.Length, 80)] : null;
}
