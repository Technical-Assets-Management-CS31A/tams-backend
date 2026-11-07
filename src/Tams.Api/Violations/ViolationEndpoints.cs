using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;

namespace Tams.Api.Violations;

public sealed record ReasonRequest(string? Reason);

/// <param name="Status">SETTLED_PAYMENT (needs receiptNo), SETTLED_REPLACEMENT or WAIVED.</param>
/// <param name="Amount">Optional corrected amount (FR-6.7: the liability is editable).</param>
public sealed record SettleRequest(string? Status, string? ReceiptNo, string? Notes, decimal? Amount);

/// <summary>
/// F6 resolution (FR-6.8 to FR-6.10). Only technical personnel clear penalties ("Only technical personnel
/// can clear them"); every action needs a reason and is written to the audit log. The cached counters on
/// students are recomputed after each change.
/// </summary>
public static class ViolationEndpoints
{
    static readonly string[] Staff = [Roles.Admin, Roles.TechStaff];

    public static IEndpointRouteBuilder MapViolationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("").WithTags("Violations");
        g.MapGet("/violations", Summary).RequireRoles(Staff);
        g.MapGet("/students/{id:guid}/standing", Standing).RequireRoles(Staff);
        g.MapPost("/warnings/{id:guid}/clear", ClearWarning).RequireRoles(Staff);
        g.MapPost("/marks/{id:guid}/clear", ClearMark).RequireRoles(Staff);
        g.MapPost("/holds/{id:guid}/lift", LiftHold).RequireRoles(Staff);
        g.MapPost("/freezes/{id:guid}/lift", LiftFreeze).RequireRoles(Staff);
        g.MapPost("/liabilities/{id:guid}/settle", Settle).RequireRoles(Staff);
        // FR-6.9 "admin action".
        g.MapPost("/warnings/reset-term", ResetTerm).RequireRoles(Roles.Admin);
        return app;
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    static IResult ReasonRequired() =>
        Problems.Validation(new Dictionary<string, string[]> { ["reason"] = ["Required, up to 2000 characters."] });

    static string? Ip(HttpContext http) => http.Connection.RemoteIpAddress?.ToString();

    /// <summary>The Violations and Resolution screen: students with any penalty, instructors with freezes.</summary>
    static async Task<IResult> Summary(TamsDbContext db, CancellationToken ct)
    {
        var owing = await db.Liabilities.AsNoTracking().Where(l => l.Status == LiabilityStatus.Outstanding)
            .GroupBy(l => l.StudentId).Select(g => new { StudentId = g.Key, Amount = g.Sum(x => x.Amount) }).ToListAsync(ct);
        var owingIds = owing.Select(o => o.StudentId).ToList();
        var students = await (
            from s in db.Students.AsNoTracking()
            where s.DeletedAt == null && (s.ActiveWarningCount > 0 || s.ActiveMarkCount > 0 || s.HasActiveHold
                || s.Standing != StudentStanding.Good || owingIds.Contains(s.Id))
            join sec in db.Sections on s.SectionId equals sec.Id
            orderby s.LastName, s.FirstName
            select new { s.Id, s.StudentNo, FullName = s.FirstName + " " + s.LastName, SectionCode = sec.Code, s.Standing,
                s.ActiveWarningCount, s.ActiveMarkCount, s.HasActiveHold }).ToListAsync(ct);
        var freezes = await (
            from f in db.ApprovalFreezes.AsNoTracking()
            where f.Status == PenaltyStatus.Active
            join u in db.Users on f.InstructorUserId equals u.Id
            join l in db.Loans on f.LoanId equals l.Id
            orderby f.FrozenAt descending
            select new { f.Id, f.InstructorUserId, InstructorName = u.FirstName + " " + u.LastName, f.Reason, f.FrozenAt, f.LoanId, l.LoanNo })
            .ToListAsync(ct);

        return Results.Ok(new
        {
            students = students.Select(s => new
            {
                s.Id, s.StudentNo, s.FullName, s.SectionCode, standing = Support.Label(s.Standing), s.ActiveWarningCount,
                s.ActiveMarkCount, s.HasActiveHold, outstandingLiabilities = owing.FirstOrDefault(o => o.StudentId == s.Id)?.Amount ?? 0m,
            }),
            instructors = freezes.GroupBy(f => (f.InstructorUserId, f.InstructorName)).Select(g => new
            {
                id = g.Key.InstructorUserId, fullName = g.Key.InstructorName,
                freezes = g.Select(f => new { f.Id, reason = Support.Label(f.Reason), f.FrozenAt, f.LoanId, f.LoanNo }),
            }),
        });
    }

    static async Task<IResult> Standing(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var row = await (
            from x in db.Students.AsNoTracking()
            where x.Id == id
            join sec in db.Sections on x.SectionId equals sec.Id
            select new { Student = x, SectionCode = sec.Code }).FirstOrDefaultAsync(ct);
        if (row is null) return Problems.NotFound("STUDENT_NOT_FOUND", "Student not found.");
        var s = row.Student;

        var warnings = await db.LateArrivalWarnings.AsNoTracking().Where(w => w.StudentId == id).OrderByDescending(w => w.IssuedAt).ToListAsync(ct);
        var marks = await db.NonComplianceMarks.AsNoTracking().Where(m => m.StudentId == id).OrderByDescending(m => m.IssuedAt).ToListAsync(ct);
        var holds = await db.ProfileHolds.AsNoTracking().Where(h => h.StudentId == id).OrderByDescending(h => h.PlacedAt).ToListAsync(ct);
        var liabilities = await db.Liabilities.AsNoTracking().Where(l => l.StudentId == id).ToListAsync(ct);
        var loanIds = warnings.Select(w => w.LoanId).Concat(marks.Select(m => m.LoanId)).Concat(holds.Select(h => h.LoanId))
            .Concat(liabilities.Select(l => l.LoanId)).Distinct().ToList();
        var loanNos = await db.Loans.AsNoTracking().Where(l => loanIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.LoanNo, ct);
        var open = await db.Loans.AsNoTracking().Where(l => l.StudentId == id
            && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue || l.Status == LoanStatus.Missing)).ToListAsync(ct);

        return Results.Ok(new
        {
            student = new
            {
                s.Id, s.StudentNo, fullName = $"{s.FirstName} {s.LastName}", row.SectionCode, standing = Support.Label(s.Standing),
                s.ActiveWarningCount, s.ActiveMarkCount, s.HasActiveHold,
            },
            warnings = warnings.Select(w => new
            {
                w.Id, w.LoanId, loanNo = loanNos.GetValueOrDefault(w.LoanId), w.WarningNo, w.SchoolTerm, w.SchoolYear, w.TriggeredPenalty,
                w.MarkId, w.FreezeId, status = Support.Label(w.Status), w.IssuedAt, w.ClearedAt, w.ClearedByUserId, w.ClearReason,
            }),
            marks = marks.Select(m => new
            {
                m.Id, m.LoanId, loanNo = loanNos.GetValueOrDefault(m.LoanId), reason = Support.Label(m.Reason),
                status = Support.Label(m.Status), m.IssuedAt, m.ClearedAt, m.ClearedByUserId, m.ClearReason,
            }),
            holds = holds.Select(h => new
            {
                h.Id, h.LoanId, loanNo = loanNos.GetValueOrDefault(h.LoanId), status = Support.Label(h.Status), h.PlacedAt,
                h.LiftedAt, h.LiftedByUserId, h.Reason,
            }),
            liabilities = liabilities.Select(l => new
            {
                l.Id, l.LoanId, loanNo = loanNos.GetValueOrDefault(l.LoanId), l.AssetId, l.Amount, status = Support.Label(l.Status),
                l.ReceiptNo, l.SettledAt, l.SettledByUserId, l.Notes,
            }),
            openLoans = await LoanEndpoints.DtosAsync(db, open, ct),
        });
    }

    static async Task<IResult> ClearWarning(Guid id, ReasonRequest req, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        if (Clean(req.Reason) is not { Length: <= 2000 } reason) return ReasonRequired();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await db.LateArrivalWarnings.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (w is null) return Problems.NotFound("WARNING_NOT_FOUND", "Warning not found.");
        if (w.Status != PenaltyStatus.Active) return Problems.Conflict("ALREADY_CLEARED", "The warning is already cleared.");
        var actor = AssetAudit.ActorId(http.User);
        w.Status = PenaltyStatus.Cleared;
        w.ClearedAt = DateTimeOffset.UtcNow;
        w.ClearedByUserId = actor;
        w.ClearReason = reason;
        LoanRules.Audit(db, "WARNING_CLEARED", "late_arrival_warning", w.Id, new { w.StudentId, w.LoanId, reason }, actor, ip: Ip(http));
        await db.SaveChangesAsync(ct);
        await LoanRules.RecomputeStandingAsync(db, settings, w.StudentId, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { w.Id, status = Support.Label(w.Status), w.ClearedAt, w.ClearReason });
    }

    static async Task<IResult> ClearMark(Guid id, ReasonRequest req, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        if (Clean(req.Reason) is not { Length: <= 2000 } reason) return ReasonRequired();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var m = await db.NonComplianceMarks.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (m is null) return Problems.NotFound("MARK_NOT_FOUND", "Mark not found.");
        if (m.Status != PenaltyStatus.Active) return Problems.Conflict("ALREADY_CLEARED", "The mark is already cleared.");
        var actor = AssetAudit.ActorId(http.User);
        m.Status = PenaltyStatus.Cleared;
        m.ClearedAt = DateTimeOffset.UtcNow;
        m.ClearedByUserId = actor;
        m.ClearReason = reason;
        LoanRules.Audit(db, "MARK_CLEARED", "non_compliance_mark", m.Id, new { m.StudentId, m.LoanId, reason }, actor, ip: Ip(http));
        await db.SaveChangesAsync(ct);
        await LoanRules.RecomputeStandingAsync(db, settings, m.StudentId, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { m.Id, status = Support.Label(m.Status), m.ClearedAt, m.ClearReason });
    }

    static async Task<IResult> LiftHold(Guid id, ReasonRequest req, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        if (Clean(req.Reason) is not { Length: <= 2000 } reason) return ReasonRequired();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var h = await db.ProfileHolds.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (h is null) return Problems.NotFound("HOLD_NOT_FOUND", "Hold not found.");
        if (h.Status != PenaltyStatus.Active) return Problems.Conflict("ALREADY_LIFTED", "The hold is already lifted.");
        var actor = AssetAudit.ActorId(http.User);
        h.Status = PenaltyStatus.Cleared;
        h.LiftedAt = DateTimeOffset.UtcNow;
        h.LiftedByUserId = actor;
        h.Reason = reason;
        LoanRules.Audit(db, "HOLD_LIFTED", "profile_hold", h.Id, new { h.StudentId, h.LoanId, reason }, actor, ip: Ip(http));
        await db.SaveChangesAsync(ct);
        await LoanRules.RecomputeStandingAsync(db, settings, h.StudentId, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { h.Id, status = Support.Label(h.Status), h.LiftedAt, h.Reason });
    }

    static async Task<IResult> LiftFreeze(Guid id, ReasonRequest req, HttpContext http, TamsDbContext db, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (Clean(req.Reason) is not { Length: <= 2000 } reason) return ReasonRequired();
        var f = await db.ApprovalFreezes.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null) return Problems.NotFound("FREEZE_NOT_FOUND", "Freeze not found.");
        if (f.Status != PenaltyStatus.Active) return Problems.Conflict("ALREADY_LIFTED", "The freeze is already lifted.");
        var actor = AssetAudit.ActorId(http.User);
        f.Status = PenaltyStatus.Cleared;
        f.LiftedAt = DateTimeOffset.UtcNow;
        f.LiftedByUserId = actor;
        f.LiftNote = reason;
        LoanRules.Audit(db, "FREEZE_LIFTED", "approval_freeze", f.Id, new { f.InstructorUserId, f.LoanId, reason }, actor, ip: Ip(http));
        await db.SaveChangesAsync(ct);
        await UnfrozenAsync(db, hub, [f], ct);
        return Results.Ok(new { f.Id, status = Support.Label(f.Status), f.LiftedAt, f.LiftNote });
    }

    /// <summary>US-10: recording payment or replacement lifts the hold and the instructor freeze for that loan.</summary>
    static async Task<IResult> Settle(Guid id, SettleRequest req, HttpContext http, TamsDbContext db, ISettings settings,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (!Support.TryParse<LiabilityStatus>(req.Status, out var status) || status == LiabilityStatus.Outstanding)
            errors["status"] = ["SETTLED_PAYMENT, SETTLED_REPLACEMENT or WAIVED."];
        var receipt = Clean(req.ReceiptNo);
        if (status == LiabilityStatus.SettledPayment && receipt is null) errors["receiptNo"] = ["Required for SETTLED_PAYMENT."];
        if (receipt is { Length: > 40 }) errors["receiptNo"] = ["Up to 40 characters."];
        if (req.Amount is < 0) errors["amount"] = ["Must be 0 or more."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var now = DateTimeOffset.UtcNow;
        var actor = AssetAudit.ActorId(http.User);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var l = await db.Liabilities.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null) return Problems.NotFound("LIABILITY_NOT_FOUND", "Liability not found.");
        if (l.Status != LiabilityStatus.Outstanding) return Problems.Conflict("LIABILITY_ALREADY_SETTLED", "The liability is already settled.");

        var before = new { l.Amount, status = Support.Label(l.Status) };
        if (req.Amount is decimal amount) l.Amount = amount;
        l.Status = status;
        l.ReceiptNo = receipt;
        l.SettledByUserId = actor;
        l.SettledAt = now;
        l.Notes = Clean(req.Notes) ?? l.Notes;
        var why = $"Liability {Support.Label(status)}";

        foreach (var h in await db.ProfileHolds.Where(h => h.LoanId == l.LoanId && h.Status == PenaltyStatus.Active).ToListAsync(ct))
        {
            (h.Status, h.LiftedAt, h.LiftedByUserId, h.Reason) = (PenaltyStatus.Cleared, now, actor, why);
            LoanRules.Audit(db, "HOLD_LIFTED", "profile_hold", h.Id, new { h.StudentId, h.LoanId, reason = why }, actor, ip: Ip(http));
        }
        var freezes = await db.ApprovalFreezes.Where(f => f.LoanId == l.LoanId && f.Status == PenaltyStatus.Active).ToListAsync(ct);
        foreach (var f in freezes)
        {
            (f.Status, f.LiftedAt, f.LiftedByUserId, f.LiftNote) = (PenaltyStatus.Cleared, now, actor, why);
            LoanRules.Audit(db, "FREEZE_LIFTED", "approval_freeze", f.Id, new { f.InstructorUserId, f.LoanId, reason = why }, actor, ip: Ip(http));
        }
        var loan = await db.Loans.AsNoTracking().FirstAsync(x => x.Id == l.LoanId, ct);
        LoanRules.Ledger(db, LedgerEvent.Settled, loan, now, actor: actor,
            details: new { liabilityId = l.Id, status = Support.Label(status), l.Amount, l.ReceiptNo });
        LoanRules.Audit(db, "LIABILITY_SETTLED", "liability", l.Id, new { l.Amount, status = Support.Label(status), l.ReceiptNo, l.Notes },
            actor, before, Ip(http));
        await db.SaveChangesAsync(ct);
        await LoanRules.RecomputeStandingAsync(db, settings, l.StudentId, ct);
        await tx.CommitAsync(ct);
        await UnfrozenAsync(db, hub, freezes, ct);
        return Results.Ok(new
        {
            l.Id, status = Support.Label(l.Status), l.Amount, l.ReceiptNo, l.SettledAt, liftedHolds = true, liftedFreezes = freezes.Count,
        });
    }

    /// <summary>
    /// FR-6.9. schema-v1-decisions section 3: clears active warnings from earlier terms and leaves clear_reason
    /// empty, which keeps term resets apart from staff clears. Safe to repeat; the current term is untouched.
    /// </summary>
    static async Task<IResult> ResetTerm(ReasonRequest req, HttpContext http, TamsDbContext db, ISettings settings, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (_, term) = LocalTime.SchoolTerm(now);
        var actor = AssetAudit.ActorId(http.User);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.LateArrivalWarnings.Where(w => w.Status == PenaltyStatus.Active && w.SchoolTerm != term).ToListAsync(ct);
        foreach (var w in rows)
        {
            (w.Status, w.ClearedAt, w.ClearedByUserId) = (PenaltyStatus.Cleared, now, actor);
            LoanRules.Audit(db, "WARNING_TERM_RESET", "late_arrival_warning", w.Id,
                new { w.StudentId, w.SchoolTerm, currentTerm = term, note = Clean(req.Reason) }, actor, ip: Ip(http));
        }
        await db.SaveChangesAsync(ct);
        var students = rows.Select(w => w.StudentId).Distinct().ToList();
        foreach (var sid in students) await LoanRules.RecomputeStandingAsync(db, settings, sid, ct);
        await tx.CommitAsync(ct);
        return Results.Ok(new { currentTerm = term, clearedWarnings = rows.Count, students = students.Count });
    }

    static async Task UnfrozenAsync(TamsDbContext db, IHubContext<LiveHub> hub, IEnumerable<ApprovalFreeze> lifted, CancellationToken ct)
    {
        foreach (var f in lifted)
        {
            var stillFrozen = await LoanRules.IsFrozenAsync(db, f.InstructorUserId, ct);
            await hub.Clients.Group(LiveHub.UserGroup(f.InstructorUserId)).SendAsync("instructor.unfrozen",
                new { instructorId = f.InstructorUserId, freezeId = f.Id, loanId = f.LoanId, stillFrozen }, ct);
        }
    }
}
