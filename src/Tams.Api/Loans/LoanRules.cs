using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Live;
using Tams.Api.Settings;

namespace Tams.Api.Loans;

/// <summary>Wall-clock helpers. The pilot site is in the Philippines (UTC+8, no DST).</summary>
public static class LocalTime
{
    static readonly TimeZoneInfo Zone = TimeZoneInfo.TryFindSystemTimeZoneById("Asia/Manila", out var tz)
        ? tz : TimeZoneInfo.CreateCustomTimeZone("PHT", TimeSpan.FromHours(8), "PHT", "PHT");

    public static DateTimeOffset ToLocal(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, Zone);

    /// <summary>"3:15PM", short enough for the 20-character LCD.</summary>
    public static string Clock(DateTimeOffset t) => ToLocal(t).ToString("h:mmtt", CultureInfo.InvariantCulture);

    // ponytail: calendar guess (Aug-Dec 1st semester, Jan-May 2nd, Jun-Jul summer); move the term dates
    // into settings if the registrar's calendar differs.
    /// <summary>("2026-2027", "2026-2027-1") for a date in the first semester of 2026-2027.</summary>
    public static (string SchoolYear, string Term) SchoolTerm(DateTimeOffset t)
    {
        var d = ToLocal(t);
        var start = d.Month >= 8 ? d.Year : d.Year - 1;
        var sem = d.Month >= 8 ? 1 : d.Month <= 5 ? 2 : 3;
        var year = $"{start}-{start + 1}";
        return (year, $"{year}-{sem}");
    }
}

/// <summary>system_settings keys read by the transaction flows (SettingDefinitions has the ranges).</summary>
static class SettingKeys
{
    public const string TransitWindow = "transit_window_minutes";
    public const string ClassLengthDefault = "class_length_default_minutes";
    public const string Grace = "grace_minutes";
    public const string PollInterval = "kiosk_poll_interval_seconds";
    public const string ApprovalTimeout = "approval_timeout_seconds";
    public const string IdleTimeout = "kiosk_idle_timeout_seconds";
    public const string HeartbeatInterval = "heartbeat_interval_seconds";
    public const string WarningWindows = "station_warning_missed_windows";
    public const string OfflineWindows = "station_offline_missed_windows";
    public const string WarningLimit = "late_arrival_warning_limit";
    public const string MarksBeforeBlock = "marks_before_block";
    public const string MaxActiveLoans = "max_active_loans_per_student";
}

/// <summary>
/// Loan state changes shared by the kiosk, room stations, staff actions and the sweeper (PRD 4.1, 4.2, F6).
/// Live events go into the caller's <see cref="Outbox"/> and are sent after commit.
/// Penalties are never lifted automatically except by a liability settlement (US-10); everything else
/// is a staff action on the resolution screen (FR-6.8).
/// </summary>
static class LoanRules
{
    public static readonly short[] ClassLengths = [90, 120, 150, 180];

    public static string? Json(object? o) => o is null ? null : JsonSerializer.Serialize(o, JsonSerializerOptions.Web);

    public sealed record Parties(string AssetCode, string AssetName, string? StudentName, string? StudentNo, string? SectionCode);

    public static async Task<Parties> PartiesAsync(TamsDbContext db, Loan loan, CancellationToken ct)
    {
        var asset = await db.Assets.AsNoTracking().Where(a => a.Id == loan.AssetId)
            .Select(a => new { a.AssetCode, a.Name }).FirstAsync(ct);
        var student = loan.StudentId is null ? null : await (
            from s in db.Students.AsNoTracking()
            where s.Id == loan.StudentId
            join sec in db.Sections on s.SectionId equals sec.Id
            select new { Name = s.FirstName + " " + s.LastName, s.StudentNo, sec.Code }).FirstOrDefaultAsync(ct);
        return new(asset.AssetCode, asset.Name, student?.Name, student?.StudentNo, student?.Code);
    }

    /// <summary>audit_log row for a system or staff action (FR-6.10); saved with the caller's unit of work.</summary>
    public static void Audit(TamsDbContext db, string action, string entityType, Guid entityId, object? after,
        Guid? actor = null, object? before = null, string? ip = null, Guid? stationId = null) =>
        db.AuditLog.Add(new AuditLogEntry
        {
            ActorUserId = actor, ActorStationId = stationId, Action = action, EntityType = entityType, EntityId = entityId,
            Before = Json(before), After = Json(after), IpAddress = ip, CreatedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>FR-2.4: append-only ledger row (the database rejects UPDATE and DELETE).</summary>
    public static void Ledger(TamsDbContext db, LedgerEvent evt, Loan loan, DateTimeOffset at, Guid? stationId = null,
        Guid? actor = null, object? details = null, DateTimeOffset? approvalDecidedAt = null) =>
        db.TransactionLedger.Add(new TransactionLedger
        {
            Event = evt, LoanId = loan.Id, AssetId = loan.AssetId, StudentId = loan.StudentId,
            InstructorUserId = loan.ApprovedByUserId ?? loan.CoordinatorUserId, ApprovalDecidedAt = approvalDecidedAt,
            PhotoFileId = loan.PhotoFileId, StationId = stationId, ActorUserId = actor, OccurredAt = at, Details = Json(details),
        });

    /// <summary>FR-2.6: notices about a loan go to its approving instructor (or event coordinator) only.</summary>
    public static void Notify(TamsDbContext db, Guid? to, string type, string title, string body, Loan loan, object payload, DateTimeOffset now)
    {
        if (to is not Guid id) return;
        db.Notifications.Add(new Notification
        {
            Id = Guid.NewGuid(), RecipientUserId = id, Type = type, Title = title, Body = body, Payload = Json(payload),
            LoanId = loan.Id, StudentId = loan.StudentId, CreatedAt = now,
        });
    }

    public static object StatusChanged(Guid assetId, AssetStatus from, AssetStatus to, Guid? roomId) =>
        new { assetId, oldStatus = Support.Label(from), newStatus = Support.Label(to), roomId };

    public static object AnomalyEvent(Anomaly a, string? assetCode, string? loanNo) => new
    {
        a.Id, type = Support.Label(a.Type), status = Support.Label(a.Status), a.AssetId, assetCode, a.LoanId, loanNo,
        a.RoomId, a.StationId, a.DetectedAt, a.AcknowledgedAt, a.ResolvedAt, a.ResolutionNote,
    };

    static object FreezeEvent(ApprovalFreeze f, Loan loan, Parties who) => new
    {
        instructorId = f.InstructorUserId, freezeId = f.Id, reason = Support.Label(f.Reason), loanId = loan.Id, loan.LoanNo,
        who.AssetCode, who.AssetName, who.StudentName,
    };

    /// <summary>
    /// FR-4.3, FR-6.1, FR-6.2: the transit window passed without a tap at the destination room. Raises
    /// TRANSIT_TIMEOUT, moves the asset to LOCATION_ANOMALY and, on student loans, issues a Late Arrival
    /// Warning; past the limit the warning also brings a mark and an instructor freeze. Own transaction;
    /// returns false when another caller already handled the loan.
    /// </summary>
    public static async Task<bool> TransitTimeoutAsync(TamsDbContext db, ISettings settings, Outbox outbox, Guid loanId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Loans
            .Where(l => l.Id == loanId && l.CustodyState == CustodyState.AwaitingArrival && l.TransitDeadlineAt <= now
                && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.CustodyState, CustodyState.Anomaly), ct);
        if (claimed == 0) return false;

        var loan = await db.Loans.AsNoTracking().FirstAsync(l => l.Id == loanId, ct);
        var asset = await db.Assets.FirstAsync(a => a.Id == loan.AssetId, ct);
        var from = asset.Status;
        if (asset.Status == AssetStatus.InTransit)
        {
            asset.Status = AssetStatus.LocationAnomaly;
            asset.UpdatedAt = now;
        }

        var anomaly = new Anomaly
        {
            Id = Guid.NewGuid(), Type = AnomalyType.TransitTimeout, AssetId = asset.Id, LoanId = loan.Id,
            RoomId = loan.DestinationRoomId, DetectedAt = now,
        };
        db.Anomalies.Add(anomaly);
        var who = await PartiesAsync(db, loan, ct);
        var instructor = loan.ApprovedByUserId ?? loan.CoordinatorUserId;

        LateArrivalWarning? warning = null;
        ApprovalFreeze? freeze = null;
        // One warning per loan (unique loan_id): a loan re-routed by staff that times out again gets none.
        if (loan.StudentId is Guid sid && !await db.LateArrivalWarnings.AnyAsync(w => w.LoanId == loan.Id, ct))
        {
            var limit = await settings.GetIntAsync(SettingKeys.WarningLimit, ct);
            var active = await db.LateArrivalWarnings.CountAsync(w => w.StudentId == sid && w.Status == PenaltyStatus.Active, ct);
            var (year, term) = LocalTime.SchoolTerm(now);
            warning = new LateArrivalWarning
            {
                Id = Guid.NewGuid(), StudentId = sid, LoanId = loan.Id, AnomalyId = anomaly.Id, WarningNo = (short)(active + 1),
                SchoolTerm = term, SchoolYear = year, TriggeredPenalty = active + 1 > limit, IssuedAt = now,
            };
            if (warning.TriggeredPenalty)
            {
                warning.MarkId = AddMark(db, sid, loan.Id, PenaltyReason.LateArrivalLimit, now).Id;
                if (loan.ApprovedByUserId is Guid approver)
                {
                    freeze = AddFreeze(db, approver, loan.Id, PenaltyReason.LateArrivalLimit, now);
                    warning.FreezeId = freeze.Id;
                }
            }
            db.LateArrivalWarnings.Add(warning);
            Audit(db, "WARNING_ISSUED", "late_arrival_warning", warning.Id,
                new { loanId = loan.Id, studentId = sid, warning.WarningNo, warning.SchoolTerm, warning.TriggeredPenalty });
            Ledger(db, LedgerEvent.LateArrivalWarning, loan, now,
                details: new { anomalyId = anomaly.Id, warning.WarningNo, warning.TriggeredPenalty });
        }

        var subject = who.StudentName is null ? who.AssetCode : $"{who.StudentName} ({who.SectionCode}), {who.AssetCode}";
        Notify(db, instructor, "LATE_ARRIVAL_WARNING", "Item not checked in at the room",
            warning is null
                ? $"{subject} was not tapped at the room within the transit window."
                : $"{subject} was not tapped at the room in time. Late Arrival Warning #{warning.WarningNo}"
                  + (warning.TriggeredPenalty ? "; a Non-Compliance Mark was issued and your approvals are frozen." : "."),
            loan, new
            {
                anomalyId = anomaly.Id, warningNo = warning?.WarningNo, penaltyTriggered = warning?.TriggeredPenalty ?? false,
                who.StudentName, who.SectionCode, who.AssetCode, loan.LoanNo,
            }, now);

        await db.SaveChangesAsync(ct);
        if (loan.StudentId is Guid student) await RecomputeStandingAsync(db, settings, student, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        var raised = AnomalyEvent(anomaly, who.AssetCode, loan.LoanNo);
        outbox.Staff("anomaly.raised", raised);
        outbox.User(instructor, "anomaly.raised", raised);
        if (warning is not null)
        {
            var w = new
            {
                studentId = warning.StudentId, who.StudentName, who.SectionCode, loanId = loan.Id, loan.LoanNo,
                warningNo = warning.WarningNo, penaltyTriggered = warning.TriggeredPenalty,
            };
            outbox.Staff("warning.issued", w);
            outbox.User(instructor, "warning.issued", w);
        }
        if (freeze is not null) outbox.User(freeze.InstructorUserId, "instructor.frozen", FreezeEvent(freeze, loan, who));
        if (from != asset.Status) outbox.Staff("asset.statusChanged", StatusChanged(asset.Id, from, asset.Status, asset.CurrentRoomId));
        await OccupancyChangedAsync(db, outbox, loan.DestinationRoomId, now, ct);
        return true;
    }

    /// <summary>FR-6.3 to FR-6.6: due_at passed on an open loan. Mark, hold and instructor freeze at once. Own transaction.</summary>
    public static async Task<bool> OverdueAsync(TamsDbContext db, ISettings settings, Outbox outbox, Guid loanId,
        DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Loans.Where(l => l.Id == loanId && l.Status == LoanStatus.Active && l.DueAt <= now)
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LoanStatus.Overdue).SetProperty(l => l.OverdueAt, now), ct);
        if (claimed == 0) return false;

        var loan = await db.Loans.AsNoTracking().FirstAsync(l => l.Id == loanId, ct);
        var asset = await db.Assets.FirstAsync(a => a.Id == loan.AssetId, ct);
        var from = asset.Status;
        asset.Status = AssetStatus.Overdue;
        asset.UpdatedAt = now;

        ApprovalFreeze? freeze = null;
        if (loan.StudentId is Guid sid)
        {
            if (!await db.NonComplianceMarks.AnyAsync(m => m.LoanId == loan.Id && m.Reason == PenaltyReason.Overdue, ct))
                AddMark(db, sid, loan.Id, PenaltyReason.Overdue, now);
            await EnsureHoldAsync(db, sid, loan.Id, now, ct);
        }
        if (loan.ApprovedByUserId is Guid approver && !await db.ApprovalFreezes.AnyAsync(f =>
                f.LoanId == loan.Id && f.Reason == PenaltyReason.Overdue && f.Status == PenaltyStatus.Active, ct))
            freeze = AddFreeze(db, approver, loan.Id, PenaltyReason.Overdue, now);
        Ledger(db, LedgerEvent.Overdue, loan, now, details: new { loan.DueAt });

        var who = await PartiesAsync(db, loan, ct);
        var instructor = loan.ApprovedByUserId ?? loan.CoordinatorUserId;
        Notify(db, instructor, "LOAN_OVERDUE", "Loan overdue",
            $"{(who.StudentName is null ? "" : $"{who.StudentName} ({who.SectionCode}): ")}{who.AssetCode} {who.AssetName} was due "
            + $"{LocalTime.Clock(loan.DueAt)} and has not been returned." + (freeze is null ? "" : " Your approvals are frozen until staff lift it."),
            loan, new { loan.LoanNo, who.StudentName, who.SectionCode, who.AssetCode, loan.DueAt }, now);

        await db.SaveChangesAsync(ct);
        if (loan.StudentId is Guid student) await RecomputeStandingAsync(db, settings, student, ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        var evt = new
        {
            loanId = loan.Id, loan.LoanNo, studentId = loan.StudentId, who.StudentName, who.SectionCode,
            assetId = loan.AssetId, who.AssetCode, loan.DueAt,
        };
        outbox.Staff("loan.overdue", evt);
        outbox.User(instructor, "loan.overdue", evt);
        if (freeze is not null) outbox.User(freeze.InstructorUserId, "instructor.frozen", FreezeEvent(freeze, loan, who));
        if (from != AssetStatus.Overdue) outbox.Staff("asset.statusChanged", StatusChanged(asset.Id, from, AssetStatus.Overdue, asset.CurrentRoomId));
        return true;
    }

    /// <summary>
    /// FR-6.7: staff declare an open loan MISSING. Liability at the replacement cost (or the given amount);
    /// the hold and freeze stay or are placed. Caller owns the transaction. Returns null if the loan is not open.
    /// </summary>
    public static async Task<Loan?> MarkMissingAsync(TamsDbContext db, ISettings settings, Outbox outbox, Guid loanId,
        decimal? amount, string? note, Guid actor, string? ip, DateTimeOffset now, CancellationToken ct)
    {
        var claimed = await db.Loans.Where(l => l.Id == loanId && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.Status, LoanStatus.Missing).SetProperty(l => l.MissingAt, now), ct);
        if (claimed == 0) return null;

        var loan = await db.Loans.AsNoTracking().FirstAsync(l => l.Id == loanId, ct);
        var asset = await db.Assets.FirstAsync(a => a.Id == loan.AssetId, ct);
        var from = asset.Status;
        asset.Status = AssetStatus.Missing;
        asset.UpdatedAt = now;

        ApprovalFreeze? freeze = null;
        // liabilities.student_id is required, so EVENT loans (no student) get none; staff settle those offline.
        if (loan.StudentId is Guid sid)
        {
            if (!await db.Liabilities.AnyAsync(x => x.LoanId == loan.Id, ct))
            {
                var liability = new Liability
                {
                    Id = Guid.NewGuid(), StudentId = sid, LoanId = loan.Id, AssetId = asset.Id,
                    Amount = amount ?? asset.ReplacementCost, Notes = note,
                };
                db.Liabilities.Add(liability);
                Audit(db, "LIABILITY_CREATED", "liability", liability.Id, new { loanId = loan.Id, studentId = sid, liability.Amount }, actor, ip: ip);
            }
            await EnsureHoldAsync(db, sid, loan.Id, now, ct);
        }
        if (loan.ApprovedByUserId is Guid approver
            && !await db.ApprovalFreezes.AnyAsync(f => f.LoanId == loan.Id && f.Status == PenaltyStatus.Active, ct))
            freeze = AddFreeze(db, approver, loan.Id, PenaltyReason.Missing, now);
        Ledger(db, LedgerEvent.MarkedMissing, loan, now, actor: actor, details: new { amount = amount ?? asset.ReplacementCost, note });
        Audit(db, "LOAN_MARKED_MISSING", "loan", loan.Id, new { loan.LoanNo, note }, actor, ip: ip);

        var who = await PartiesAsync(db, loan, ct);
        var instructor = loan.ApprovedByUserId ?? loan.CoordinatorUserId;
        Notify(db, instructor, "LOAN_MISSING", "Item declared missing",
            $"{(who.StudentName is null ? "" : $"{who.StudentName} ({who.SectionCode}): ")}{who.AssetCode} {who.AssetName} was declared missing by staff.",
            loan, new { loan.LoanNo, who.StudentName, who.SectionCode, who.AssetCode }, now);
        await db.SaveChangesAsync(ct);
        if (loan.StudentId is Guid student) await RecomputeStandingAsync(db, settings, student, ct);

        if (freeze is not null) outbox.User(freeze.InstructorUserId, "instructor.frozen", FreezeEvent(freeze, loan, who));
        outbox.Staff("asset.statusChanged", StatusChanged(asset.Id, from, AssetStatus.Missing, asset.CurrentRoomId));
        await OccupancyChangedAsync(db, outbox, loan.DestinationRoomId, now, ct);
        return loan;
    }

    /// <summary>
    /// US-03 / PRD 4.2: a return closes the loan from any open state, including OVERDUE and MISSING.
    /// Caller owns the transaction. Holds, freezes and liabilities stay for staff to resolve (FR-6.8).
    /// </summary>
    public static async Task<Loan?> CloseAsync(TamsDbContext db, Outbox outbox, Guid loanId, DateTimeOffset now,
        Guid? returnedByStudentId, Guid? stationId, Guid? actorUserId, Guid? returnRoomId, string? note, CancellationToken ct)
    {
        var claimed = await db.Loans
            .Where(l => l.Id == loanId && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue || l.Status == LoanStatus.Missing))
            .ExecuteUpdateAsync(u => u
                .SetProperty(l => l.Status, LoanStatus.Returned)
                .SetProperty(l => l.ReturnedAt, now)
                .SetProperty(l => l.ReturnedByStudentId, returnedByStudentId)
                .SetProperty(l => l.ReturnStationId, stationId), ct);
        if (claimed == 0) return null;

        var loan = await db.Loans.AsNoTracking().FirstAsync(l => l.Id == loanId, ct);
        var asset = await db.Assets.FirstAsync(a => a.Id == loan.AssetId, ct);
        var from = asset.Status;
        asset.Status = AssetStatus.InStorage;
        asset.CurrentRoomId = returnRoomId ?? asset.HomeRoomId;
        asset.UpdatedAt = now;
        Ledger(db, LedgerEvent.Return, loan, now, stationId, actorUserId, new { returnedByStudentId, note });
        await db.SaveChangesAsync(ct);

        outbox.Staff("asset.statusChanged", StatusChanged(asset.Id, from, AssetStatus.InStorage, asset.CurrentRoomId));
        await OccupancyChangedAsync(db, outbox, loan.DestinationRoomId, now, ct);
        return loan;
    }

    static NonComplianceMark AddMark(TamsDbContext db, Guid studentId, Guid loanId, PenaltyReason reason, DateTimeOffset now)
    {
        var m = new NonComplianceMark { Id = Guid.NewGuid(), StudentId = studentId, LoanId = loanId, Reason = reason, IssuedAt = now };
        db.NonComplianceMarks.Add(m);
        Audit(db, "MARK_ISSUED", "non_compliance_mark", m.Id, new { studentId, loanId, reason = Support.Label(reason) });
        return m;
    }

    static ApprovalFreeze AddFreeze(TamsDbContext db, Guid instructorId, Guid loanId, PenaltyReason reason, DateTimeOffset now)
    {
        var f = new ApprovalFreeze { Id = Guid.NewGuid(), InstructorUserId = instructorId, LoanId = loanId, Reason = reason, FrozenAt = now };
        db.ApprovalFreezes.Add(f);
        Audit(db, "FREEZE_PLACED", "approval_freeze", f.Id, new { instructorUserId = instructorId, loanId, reason = Support.Label(reason) });
        return f;
    }

    /// <summary>FR-6.5: one active hold per loan.</summary>
    static async Task EnsureHoldAsync(TamsDbContext db, Guid studentId, Guid loanId, DateTimeOffset now, CancellationToken ct)
    {
        if (await db.ProfileHolds.AnyAsync(h => h.LoanId == loanId && h.Status == PenaltyStatus.Active, ct)) return;
        var h = new ProfileHold { Id = Guid.NewGuid(), StudentId = studentId, LoanId = loanId, PlacedAt = now };
        db.ProfileHolds.Add(h);
        Audit(db, "HOLD_PLACED", "profile_hold", h.Id, new { studentId, loanId });
    }

    public static Task<bool> IsFrozenAsync(TamsDbContext db, Guid instructorId, CancellationToken ct) =>
        db.ApprovalFreezes.AnyAsync(f => f.InstructorUserId == instructorId && f.Status == PenaltyStatus.Active, ct);

    /// <summary>
    /// Refreshes the cached counters on students from the penalty tables. Call after every penalty change,
    /// inside the same transaction. Standing: BLOCKED at marks_before_block marks (FR-6.4), WARNED with any
    /// active warning or mark, else GOOD.
    /// </summary>
    public static async Task RecomputeStandingAsync(TamsDbContext db, ISettings settings, Guid studentId, CancellationToken ct)
    {
        var warnings = (short)await db.LateArrivalWarnings.CountAsync(w => w.StudentId == studentId && w.Status == PenaltyStatus.Active, ct);
        var marks = (short)await db.NonComplianceMarks.CountAsync(m => m.StudentId == studentId && m.Status == PenaltyStatus.Active, ct);
        var hold = await db.ProfileHolds.AnyAsync(h => h.StudentId == studentId && h.Status == PenaltyStatus.Active, ct);
        var block = await settings.GetIntAsync(SettingKeys.MarksBeforeBlock, ct);
        var standing = marks >= block ? StudentStanding.Blocked
            : marks > 0 || warnings > 0 ? StudentStanding.Warned : StudentStanding.Good;
        await db.Students.Where(s => s.Id == studentId).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.ActiveWarningCount, warnings)
            .SetProperty(s => s.ActiveMarkCount, marks)
            .SetProperty(s => s.HasActiveHold, hold)
            .SetProperty(s => s.Standing, standing), ct);
    }

    // ---------------------------------------------------------------- room occupancy (FR-4.1)

    public sealed record VerifiedItem(Guid LoanId, string LoanNo, Guid AssetId, string AssetCode, string AssetName,
        string LoanStatus, DateTimeOffset? VerifiedAt, DateTimeOffset DueAt);

    public sealed record IncomingItem(Guid LoanId, string LoanNo, Guid AssetId, string AssetCode, string AssetName,
        DateTimeOffset? TransitDeadlineAt, double? SecondsLeft);

    public sealed record RoomOccupancy(Guid RoomId, string Code, string Name, int VerifiedCount, int IncomingCount,
        int OpenAnomalyCount, IReadOnlyList<VerifiedItem> Verified, IReadOnlyList<IncomingItem> Incoming, IReadOnlyList<object> Anomalies);

    /// <summary>One card per room: verified items, incoming items with countdown, open anomalies.</summary>
    public static async Task<List<RoomOccupancy>> OccupancyAsync(TamsDbContext db, Guid? roomId, DateTimeOffset now, CancellationToken ct)
    {
        var rooms = await db.Rooms.AsNoTracking().Where(r => r.DeletedAt == null && (roomId == null || r.Id == roomId))
            .OrderBy(r => r.Code).ToListAsync(ct);
        var loans = await (
            from l in db.Loans.AsNoTracking()
            where (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue) && l.DestinationRoomId != null
                && (roomId == null || l.DestinationRoomId == roomId)
            join a in db.Assets on l.AssetId equals a.Id
            select new { Loan = l, a.AssetCode, a.Name }).ToListAsync(ct);
        var anomalies = await (
            from x in db.Anomalies.AsNoTracking()
            where x.Status != AnomalyStatus.Resolved && x.RoomId != null && (roomId == null || x.RoomId == roomId)
            join a in db.Assets on x.AssetId equals a.Id
            select new { Anomaly = x, a.AssetCode }).ToListAsync(ct);

        return rooms.Select(r =>
        {
            var mine = loans.Where(x => x.Loan.DestinationRoomId == r.Id).ToList();
            var verified = mine.Where(x => x.Loan.CustodyState == CustodyState.Verified)
                .Select(x => new VerifiedItem(x.Loan.Id, x.Loan.LoanNo, x.Loan.AssetId, x.AssetCode, x.Name,
                    Support.Label(x.Loan.Status), x.Loan.VerifiedAt, x.Loan.DueAt)).ToList();
            var incoming = mine.Where(x => x.Loan.CustodyState == CustodyState.AwaitingArrival)
                .OrderBy(x => x.Loan.TransitDeadlineAt)
                .Select(x => new IncomingItem(x.Loan.Id, x.Loan.LoanNo, x.Loan.AssetId, x.AssetCode, x.Name, x.Loan.TransitDeadlineAt,
                    x.Loan.TransitDeadlineAt is { } d ? Math.Max(0, Math.Round((d - now).TotalSeconds)) : null)).ToList();
            var open = anomalies.Where(x => x.Anomaly.RoomId == r.Id).Select(x => AnomalyEvent(x.Anomaly, x.AssetCode, null)).ToList();
            return new RoomOccupancy(r.Id, r.Code, r.Name, verified.Count, incoming.Count, open.Count, verified, incoming, open);
        }).ToList();
    }

    public static async Task OccupancyChangedAsync(TamsDbContext db, Outbox outbox, Guid? roomId, DateTimeOffset now, CancellationToken ct)
    {
        if (roomId is null) return;
        foreach (var o in await OccupancyAsync(db, roomId, now, ct)) outbox.Staff("room.occupancyChanged", o);
    }
}
