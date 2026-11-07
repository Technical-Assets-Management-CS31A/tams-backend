using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Live;
using Tams.Api.Settings;

namespace Tams.Api.Loans;

/// <param name="Amount">Liability amount; defaults to the asset's replacement cost (FR-6.7).</param>
public sealed record MarkMissingRequest(decimal? Amount, string? Note);
public sealed record ChangeRoomRequest(Guid? DestinationRoomId, string? Note);
public sealed record StaffReturnRequest(string? Note);
public sealed record AnomalyNoteRequest(string? Note);

/// <summary>Loans, anomalies, asset history, room occupancy and the live dashboard (PRD 6.3, F4).</summary>
public static class LoanEndpoints
{
    static readonly string[] Readers = [Roles.Admin, Roles.TechStaff, Roles.Custodian, Roles.Instructor];
    static readonly string[] Watchers = [Roles.Admin, Roles.TechStaff, Roles.Custodian];
    static readonly string[] Staff = [Roles.Admin, Roles.TechStaff];

    public static IEndpointRouteBuilder MapLoanEndpoints(this IEndpointRouteBuilder app)
    {
        var loans = app.MapGroup("/loans").WithTags("Loans");
        loans.MapGet("", List).RequireRoles(Readers);
        loans.MapGet("{id:guid}", Get).RequireRoles(Readers);
        loans.MapGet("{id:guid}/photo", Photo).RequireRoles(Readers);
        loans.MapPost("{id:guid}/mark-missing", MarkMissing).RequireRoles(Staff);
        loans.MapPut("{id:guid}/room", ChangeRoom).RequireRoles(Staff);
        loans.MapPost("{id:guid}/return", StaffReturn).RequireRoles(Staff);

        var anomalies = app.MapGroup("/anomalies").WithTags("Anomalies");
        anomalies.MapGet("", ListAnomalies).RequireRoles(Readers);
        anomalies.MapPost("{id:guid}/acknowledge", Acknowledge).RequireRoles(Staff);
        anomalies.MapPost("{id:guid}/resolve", Resolve).RequireRoles(Staff);

        app.MapGet("/assets/{id:guid}/history", History).WithTags("Assets").RequireRoles(Watchers);
        app.MapGet("/rooms/{id:guid}/occupancy", Occupancy).WithTags("Rooms").RequireRoles(Watchers);
        app.MapGet("/dashboard/summary", Dashboard).WithTags("Dashboard").RequireRoles(Watchers);
        return app;
    }

    static bool IsWatcher(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.TechStaff) || u.IsInRole(Roles.Custodian);

    static IResult NotFound() => Problems.NotFound("LOAN_NOT_FOUND", "Loan not found.");

    static JsonElement? Parse(string? json) => json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    // ---------------------------------------------------------------- loans

    static async Task<IResult> List(HttpContext http, TamsDbContext db, CancellationToken ct,
        string? status, Guid? roomId, string? approvedBy, Guid? studentId, Guid? assetId, Guid? eventId, string? type,
        string? q, string? sort, int page = 1, int pageSize = 25)
    {
        var me = AssetAudit.ActorId(http.User);
        var errors = new Dictionary<string, string[]>();
        var query = db.Loans.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            if (status.Equals("open", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue);
            else if (Support.TryParse<LoanStatus>(status, out var st)) query = query.Where(l => l.Status == st);
            else errors["status"] = ["ACTIVE, OVERDUE, RETURNED, MISSING or open."];
        }
        if (type is not null)
        {
            if (Support.TryParse<LoanType>(type, out var t)) query = query.Where(l => l.Type == t);
            else errors["type"] = ["STANDARD or EVENT."];
        }
        if (approvedBy is not null)
        {
            Guid? who = approvedBy.Equals("me", StringComparison.OrdinalIgnoreCase) ? me : Guid.TryParse(approvedBy, out var g) ? g : null;
            if (who is null) errors["approvedBy"] = ["'me' or a user id."];
            else query = query.Where(l => l.ApprovedByUserId == who || l.CoordinatorUserId == who);
        }
        var sortKey = sort?.TrimStart('-').ToLowerInvariant();
        if (sortKey is not (null or "" or "checkedoutat" or "dueat")) errors["sort"] = ["checkedOutAt or dueAt, optionally prefixed with '-'."];
        if (errors.Count > 0) return Problems.Validation(errors);

        // PERMISSIONS.md row scoping: an instructor sees only loans they approved or coordinate (FR-2.5).
        if (!IsWatcher(http.User)) query = query.Where(l => l.ApprovedByUserId == me || l.CoordinatorUserId == me);
        if (roomId is not null) query = query.Where(l => l.DestinationRoomId == roomId);
        if (studentId is not null) query = query.Where(l => l.StudentId == studentId || l.ReturnedByStudentId == studentId);
        if (assetId is not null) query = query.Where(l => l.AssetId == assetId);
        if (eventId is not null) query = query.Where(l => l.EventId == eventId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(l => EF.Functions.ILike(l.LoanNo, like)
                || db.Assets.Any(a => a.Id == l.AssetId && (EF.Functions.ILike(a.AssetCode, like) || EF.Functions.ILike(a.Name, like)))
                || db.Students.Any(s => s.Id == l.StudentId && (EF.Functions.ILike(s.StudentNo, like) || EF.Functions.ILike(s.LastName, like))));
        }

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var asc = sort is not null && !sort.StartsWith('-');
        query = (sortKey, asc) switch
        {
            ("dueat", true) => query.OrderBy(l => l.DueAt),
            ("dueat", false) => query.OrderByDescending(l => l.DueAt),
            ("checkedoutat", true) => query.OrderBy(l => l.CheckedOutAt),
            _ => query.OrderByDescending(l => l.CheckedOutAt),
        };
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new { items = await DtosAsync(db, rows, ct), page, pageSize, total });
    }

    static async Task<Loan?> VisibleAsync(TamsDbContext db, ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var loan = await db.Loans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        var me = AssetAudit.ActorId(user);
        return loan is null || IsWatcher(user) || loan.ApprovedByUserId == me || loan.CoordinatorUserId == me ? loan : null;
    }

    static async Task<IResult> Get(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var loan = await VisibleAsync(db, http.User, id, ct);
        return loan is null ? NotFound() : Results.Ok(await DetailAsync(db, loan, ct));
    }

    /// <summary>Loan detail: summary, approval ledger (FR-2.4) and every ledger row.</summary>
    static async Task<object> DetailAsync(TamsDbContext db, Loan loan, CancellationToken ct)
    {
        var approval = loan.ApprovalId is null ? null : await (
            from a in db.Approvals.AsNoTracking()
            where a.Id == loan.ApprovalId
            join u in db.Users on a.InstructorUserId equals u.Id
            select new { a, FullName = u.FirstName + " " + u.LastName }).FirstOrDefaultAsync(ct);
        var ledger = await db.TransactionLedger.AsNoTracking().Where(x => x.LoanId == loan.Id)
            .OrderBy(x => x.OccurredAt).ThenBy(x => x.Id).ToListAsync(ct);
        return new
        {
            loan = (await DtosAsync(db, [loan], ct))[0],
            approval = approval is null ? null : new
            {
                approval.a.Id, instructor = new { id = approval.a.InstructorUserId, fullName = approval.FullName },
                decision = Support.Label(approval.a.Decision), approval.a.DestinationRoomId, approval.a.ClassLengthMinutes,
                approval.a.RequestedAt, approval.a.DecidedAt, approval.a.DecidedDevice,
            },
            ledger = ledger.Select(x => new
            {
                x.Id, @event = Support.Label(x.Event), x.OccurredAt, x.StudentId, x.InstructorUserId, x.ActorUserId,
                x.StationId, x.PhotoFileId, x.ApprovalDecidedAt, details = Parse(x.Details),
            }),
        };
    }

    /// <summary>Loan rows in the shape of the web Loan model (Gap Map), related rows loaded in a few queries.</summary>
    public static async Task<List<object>> DtosAsync(TamsDbContext db, IReadOnlyList<Loan> loans, CancellationToken ct)
    {
        var assetIds = loans.Select(l => l.AssetId).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AssetCode, a.Name, a.Status }).ToDictionaryAsync(a => a.Id, ct);
        var studentIds = loans.SelectMany(l => new[] { l.StudentId, l.ReturnedByStudentId }).OfType<Guid>().Distinct().ToList();
        var students = await (
            from s in db.Students.AsNoTracking()
            where studentIds.Contains(s.Id)
            join sec in db.Sections on s.SectionId equals sec.Id
            select new { s.Id, s.StudentNo, FullName = s.FirstName + " " + s.LastName, SectionCode = sec.Code }).ToDictionaryAsync(s => s.Id, ct);
        var userIds = loans.SelectMany(l => new[] { l.ApprovedByUserId, l.CoordinatorUserId }).OfType<Guid>().Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, FullName = u.FirstName + " " + u.LastName }).ToDictionaryAsync(u => u.Id, ct);
        var roomIds = loans.Select(l => l.DestinationRoomId).OfType<Guid>().Distinct().ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => roomIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Code, r.Name }).ToDictionaryAsync(r => r.Id, ct);
        var eventIds = loans.Select(l => l.EventId).OfType<Guid>().Distinct().ToList();
        var events = await db.Events.AsNoTracking().Where(e => eventIds.Contains(e.Id))
            .Select(e => new { e.Id, e.Title }).ToDictionaryAsync(e => e.Id, ct);
        var loanIds = loans.Select(l => l.Id).ToList();
        var warnings = await db.LateArrivalWarnings.AsNoTracking().Where(w => loanIds.Contains(w.LoanId))
            .ToDictionaryAsync(w => w.LoanId, w => w.WarningNo, ct);

        return loans.Select(l => (object)new
        {
            l.Id, l.LoanNo, type = Support.Label(l.Type), status = Support.Label(l.Status), custodyState = Support.Label(l.CustodyState),
            asset = assets.TryGetValue(l.AssetId, out var a) ? new { a.Id, a.AssetCode, a.Name, status = Support.Label(a.Status) } : null,
            student = l.StudentId is Guid sid && students.TryGetValue(sid, out var s) ? s : null,
            approvedBy = l.ApprovedByUserId is Guid ab && users.TryGetValue(ab, out var u1) ? u1 : null,
            coordinator = l.CoordinatorUserId is Guid co && users.TryGetValue(co, out var u2) ? u2 : null,
            @event = l.EventId is Guid ev && events.TryGetValue(ev, out var e) ? e : null,
            destinationRoom = l.DestinationRoomId is Guid rid && rooms.TryGetValue(rid, out var r) ? r : null,
            l.CheckedOutAt, l.TransitDeadlineAt, l.VerifiedAt, l.ClassLengthMinutes, l.ClassEndsAt, l.GraceMinutes, l.DueAt,
            l.OverdueAt, l.MissingAt, l.ReturnedAt,
            returnedBy = l.ReturnedByStudentId is Guid rb && students.TryGetValue(rb, out var s2) ? s2 : null,
            photoUrl = l.PhotoFileId is null ? null : $"/api/loans/{l.Id}/photo",
            lateArrivalWarningNo = warnings.TryGetValue(l.Id, out var w) ? w : (short?)null,
            l.Notes,
        }).ToList();
    }

    static async Task<IResult> Photo(Guid id, HttpContext http, TamsDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        var loan = await VisibleAsync(db, http.User, id, ct);
        if (loan is null) return NotFound();
        return MediaStore.Serve(cfg, await db.MediaFiles.AsNoTracking().FirstOrDefaultAsync(m => m.Id == loan.PhotoFileId, ct));
    }

    static async Task<IResult> MarkMissing(Guid id, MarkMissingRequest req, HttpContext http, TamsDbContext db, ISettings settings,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        if (req.Amount is < 0) return Problems.Validation(new Dictionary<string, string[]> { ["amount"] = ["Must be 0 or more."] });
        var actor = AssetAudit.ActorId(http.User)!.Value;
        var outbox = new Outbox();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await LoanRules.MarkMissingAsync(db, settings, outbox, id, req.Amount, Clean(req.Note), actor,
            http.Connection.RemoteIpAddress?.ToString(), DateTimeOffset.UtcNow, ct);
        if (loan is null) return await NotOpenAsync(db, id, ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);
        return Results.Ok(await DetailAsync(db, loan, ct));
    }

    /// <summary>FR-3.4: staff move a loan to another room; the item must then check in there.</summary>
    static async Task<IResult> ChangeRoom(Guid id, ChangeRoomRequest req, HttpContext http, TamsDbContext db, ISettings settings,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var room = req.DestinationRoomId is not Guid rid ? null : await db.Rooms.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == rid && r.DeletedAt == null && r.Status == RecordStatus.Active && !r.IsStorage, ct);
        if (room is null)
            return Problems.Validation(new Dictionary<string, string[]> { ["destinationRoomId"] = ["Required: an active room that is not the counter."] });

        var now = DateTimeOffset.UtcNow;
        var outbox = new Outbox();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = (await db.Loans.FromSqlInterpolated($"SELECT * FROM loans WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (loan is null) return NotFound();
        if (loan.Status is not (LoanStatus.Active or LoanStatus.Overdue))
            return Problems.Conflict("LOAN_NOT_OPEN", "Only an ACTIVE or OVERDUE loan can change room.");
        if (loan.DestinationRoomId == room.Id) return Results.Ok(await DetailAsync(db, loan, ct));

        var before = new { loan.DestinationRoomId, custodyState = Support.Label(loan.CustodyState) };
        var oldRoom = loan.DestinationRoomId;
        loan.DestinationRoomId = room.Id;
        if (loan.CustodyState != CustodyState.NotRequired)
        {
            loan.CustodyState = CustodyState.AwaitingArrival;
            loan.TransitDeadlineAt = now.AddMinutes(await settings.GetIntAsync(SettingKeys.TransitWindow, ct));
            loan.VerifiedAt = null;
            loan.VerifiedStationId = null;
            var asset = await db.Assets.FirstAsync(a => a.Id == loan.AssetId, ct);
            if (loan.Status == LoanStatus.Active && asset.Status != AssetStatus.InTransit)
            {
                outbox.Staff("asset.statusChanged", LoanRules.StatusChanged(asset.Id, asset.Status, AssetStatus.InTransit, room.Id));
                asset.Status = AssetStatus.InTransit;
                asset.CurrentRoomId = null;
                asset.UpdatedAt = now;
            }
        }
        LoanRules.Audit(db, "LOAN_ROOM_CHANGED", "loan", loan.Id, new { destinationRoomId = room.Id, note = Clean(req.Note) },
            AssetAudit.ActorId(http.User), before, http.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync(ct);
        await LoanRules.OccupancyChangedAsync(db, outbox, oldRoom, now, ct);
        await LoanRules.OccupancyChangedAsync(db, outbox, room.Id, now, ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);
        return Results.Ok(await DetailAsync(db, loan, ct));
    }

    /// <summary>FR-6.8 "record late return": staff close a loan when the item came back outside the kiosk.</summary>
    static async Task<IResult> StaffReturn(Guid id, StaffReturnRequest req, HttpContext http, TamsDbContext db,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var actor = AssetAudit.ActorId(http.User);
        var outbox = new Outbox();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var loan = await LoanRules.CloseAsync(db, outbox, id, DateTimeOffset.UtcNow, null, null, actor, null, Clean(req.Note), ct);
        if (loan is null) return await NotOpenAsync(db, id, ct);
        LoanRules.Audit(db, "LOAN_RETURNED_BY_STAFF", "loan", loan.Id, new { loan.LoanNo, note = Clean(req.Note) }, actor,
            ip: http.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);
        return Results.Ok(await DetailAsync(db, (await db.Loans.AsNoTracking().FirstAsync(l => l.Id == id, ct)), ct));
    }

    static async Task<IResult> NotOpenAsync(TamsDbContext db, Guid id, CancellationToken ct) =>
        await db.Loans.AnyAsync(l => l.Id == id, ct)
            ? Problems.Conflict("LOAN_NOT_OPEN", "The loan is not open.")
            : NotFound();

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ---------------------------------------------------------------- anomalies (FR-4.5)

    static async Task<IResult> ListAnomalies(HttpContext http, TamsDbContext db, CancellationToken ct,
        string? status, string? type, Guid? roomId, Guid? assetId, Guid? loanId, int page = 1, int pageSize = 25)
    {
        var errors = new Dictionary<string, string[]>();
        var query = db.Anomalies.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            // "active" = not yet resolved (the open queue keeps acknowledged items until they are resolved).
            if (status.Equals("active", StringComparison.OrdinalIgnoreCase)) query = query.Where(a => a.Status != AnomalyStatus.Resolved);
            else if (Support.TryParse<AnomalyStatus>(status, out var st)) query = query.Where(a => a.Status == st);
            else errors["status"] = ["OPEN, ACKNOWLEDGED, RESOLVED or active."];
        }
        if (type is not null)
        {
            if (Support.TryParse<AnomalyType>(type, out var t)) query = query.Where(a => a.Type == t);
            else errors["type"] = ["TRANSIT_TIMEOUT, WRONG_ROOM or UNBORROWED_TAP."];
        }
        if (errors.Count > 0) return Problems.Validation(errors);
        if (!IsWatcher(http.User))
        {
            var me = AssetAudit.ActorId(http.User);
            query = query.Where(a => db.Loans.Any(l => l.Id == a.LoanId && (l.ApprovedByUserId == me || l.CoordinatorUserId == me)));
        }
        if (roomId is not null) query = query.Where(a => a.RoomId == roomId);
        if (assetId is not null) query = query.Where(a => a.AssetId == assetId);
        if (loanId is not null) query = query.Where(a => a.LoanId == loanId);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.DetectedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);

        var assetIds = rows.Select(a => a.AssetId).Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AssetCode, a.Name }).ToDictionaryAsync(a => a.Id, ct);
        var loanIds = rows.Select(a => a.LoanId).OfType<Guid>().Distinct().ToList();
        var loans = await (
            from l in db.Loans.AsNoTracking()
            where loanIds.Contains(l.Id)
            from s in db.Students.Where(s => s.Id == l.StudentId).DefaultIfEmpty()
            select new { l.Id, l.LoanNo, StudentName = s == null ? null : s.FirstName + " " + s.LastName }).ToDictionaryAsync(l => l.Id, ct);
        var roomIds = rows.Select(a => a.RoomId).OfType<Guid>().Distinct().ToList();
        var rooms = await db.Rooms.AsNoTracking().Where(r => roomIds.Contains(r.Id))
            .Select(r => new { r.Id, r.Code, r.Name }).ToDictionaryAsync(r => r.Id, ct);

        var items = rows.Select(a => new
        {
            a.Id, type = Support.Label(a.Type), status = Support.Label(a.Status), a.DetectedAt,
            asset = assets.GetValueOrDefault(a.AssetId),
            loan = a.LoanId is Guid lid ? loans.GetValueOrDefault(lid) : null,
            room = a.RoomId is Guid rid ? rooms.GetValueOrDefault(rid) : null,
            a.StationId, a.AcknowledgedByUserId, a.AcknowledgedAt, a.ResolvedByUserId, a.ResolvedAt, a.ResolutionNote,
        });
        return Results.Ok(new { items, page, pageSize, total });
    }

    static async Task<IResult> Acknowledge(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var a = await db.Anomalies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return Problems.NotFound("ANOMALY_NOT_FOUND", "Anomaly not found.");
        if (a.Status != AnomalyStatus.Open) return Problems.Conflict("ANOMALY_NOT_OPEN", $"The anomaly is {Support.Label(a.Status)}.");
        a.Status = AnomalyStatus.Acknowledged;
        a.AcknowledgedByUserId = AssetAudit.ActorId(http.User);
        a.AcknowledgedAt = DateTimeOffset.UtcNow;
        LoanRules.Audit(db, "ANOMALY_ACKNOWLEDGED", "anomaly", a.Id, new { type = Support.Label(a.Type) }, a.AcknowledgedByUserId,
            ip: http.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Ok(LoanRules.AnomalyEvent(a, null, null));
    }

    /// <summary>US-09: the note and the resolver are stored. Loan and asset state are left to the item's next tap or return.</summary>
    static async Task<IResult> Resolve(Guid id, AnomalyNoteRequest req, HttpContext http, TamsDbContext db, IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var note = Clean(req.Note);
        if (note is null || note.Length > 2000)
            return Problems.Validation(new Dictionary<string, string[]> { ["note"] = ["Required, up to 2000 characters."] });
        var a = await db.Anomalies.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return Problems.NotFound("ANOMALY_NOT_FOUND", "Anomaly not found.");
        if (a.Status == AnomalyStatus.Resolved) return Problems.Conflict("ANOMALY_ALREADY_RESOLVED", "The anomaly is already resolved.");
        a.Status = AnomalyStatus.Resolved;
        a.ResolvedByUserId = AssetAudit.ActorId(http.User);
        a.ResolvedAt = DateTimeOffset.UtcNow;
        a.ResolutionNote = note;
        LoanRules.Audit(db, "ANOMALY_RESOLVED", "anomaly", a.Id, new { type = Support.Label(a.Type), note }, a.ResolvedByUserId,
            ip: http.Connection.RemoteIpAddress?.ToString());
        await db.SaveChangesAsync(ct);

        var loan = a.LoanId is null ? null : await db.Loans.AsNoTracking().FirstOrDefaultAsync(l => l.Id == a.LoanId, ct);
        var evt = LoanRules.AnomalyEvent(a, null, loan?.LoanNo);
        await hub.Clients.Group(LiveHub.StaffGroup).SendAsync("anomaly.resolved", evt, ct);
        if ((loan?.ApprovedByUserId ?? loan?.CoordinatorUserId) is Guid to)
            await hub.Clients.Group(LiveHub.UserGroup(to)).SendAsync("anomaly.resolved", evt, ct);
        return Results.Ok(evt);
    }

    // ---------------------------------------------------------------- history, occupancy, dashboard

    sealed record HistoryItem(DateTimeOffset At, string Kind, string Event, Guid? LoanId, string? LoanNo, Guid? StationId,
        Guid? RoomId, JsonElement? Details);

    /// <summary>Asset timeline: ledger rows, room scans and anomalies, newest first.</summary>
    static async Task<IResult> History(Guid id, TamsDbContext db, CancellationToken ct, int limit = 100)
    {
        if (!await db.Assets.AnyAsync(a => a.Id == id, ct)) return Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");
        limit = Math.Clamp(limit, 1, 500);
        var ledger = await db.TransactionLedger.AsNoTracking().Where(x => x.AssetId == id)
            .OrderByDescending(x => x.OccurredAt).Take(limit).ToListAsync(ct);
        var scans = await db.RoomScans.AsNoTracking().Where(x => x.AssetId == id)
            .OrderByDescending(x => x.ScannedAt).Take(limit).ToListAsync(ct);
        var anomalies = await db.Anomalies.AsNoTracking().Where(x => x.AssetId == id)
            .OrderByDescending(x => x.DetectedAt).Take(limit).ToListAsync(ct);
        var loanIds = ledger.Select(x => x.LoanId).Concat(scans.Select(x => x.LoanId).OfType<Guid>())
            .Concat(anomalies.Select(x => x.LoanId).OfType<Guid>()).Distinct().ToList();
        var loanNos = await db.Loans.AsNoTracking().Where(l => loanIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.LoanNo, ct);
        string? No(Guid? loanId) => loanId is Guid g ? loanNos.GetValueOrDefault(g) : null;

        var items = ledger.Select(x => new HistoryItem(x.OccurredAt, "LEDGER", Support.Label(x.Event), x.LoanId, No(x.LoanId), x.StationId, null, Parse(x.Details)))
            .Concat(scans.Select(x => new HistoryItem(x.ScannedAt, "SCAN", Support.Label(x.Result), x.LoanId, No(x.LoanId), x.StationId, x.RoomId, null)))
            .Concat(anomalies.Select(x => new HistoryItem(x.DetectedAt, "ANOMALY", Support.Label(x.Type), x.LoanId, No(x.LoanId), x.StationId, x.RoomId, null)))
            .OrderByDescending(x => x.At).Take(limit).ToList();
        return Results.Ok(new { assetId = id, items });
    }

    static async Task<IResult> Occupancy(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var rows = await LoanRules.OccupancyAsync(db, id, DateTimeOffset.UtcNow, ct);
        return rows.Count == 0 ? Problems.NotFound("ROOM_NOT_FOUND", "Room not found.") : Results.Ok(rows[0]);
    }

    /// <summary>FR-4.1, FR-4.2 and FR-8.2 in one call: status board columns, room cards and the station health strip.</summary>
    static async Task<IResult> Dashboard(TamsDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var counts = await db.Assets.AsNoTracking().Where(a => a.DeletedAt == null)
            .GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var active = await db.Assets.AsNoTracking()
            .Where(a => a.DeletedAt == null && (a.Status == AssetStatus.AtCounter || a.Status == AssetStatus.InTransit
                || a.Status == AssetStatus.InRoomVerified || a.Status == AssetStatus.LocationAnomaly || a.Status == AssetStatus.Overdue))
            .OrderBy(a => a.AssetCode).ToListAsync(ct);
        var ids = active.Select(a => a.Id).ToList();
        var open = await db.Loans.AsNoTracking()
            .Where(l => ids.Contains(l.AssetId) && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue)).ToListAsync(ct);
        var byAsset = open.Zip(await DtosAsync(db, open, ct)).ToDictionary(x => x.First.AssetId, x => x.Second);
        IEnumerable<object> Column(AssetStatus s) => active.Where(a => a.Status == s).Select(a => (object)new
        {
            asset = new { a.Id, a.AssetCode, a.Name, status = Support.Label(a.Status), a.CurrentRoomId },
            loan = byAsset.GetValueOrDefault(a.Id),
        });
        var health = await db.Stations.AsNoTracking().Where(s => s.DeletedAt == null && s.Status == RecordStatus.Active)
            .GroupBy(s => s.Health).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        int H(StationHealth h) => health.FirstOrDefault(x => x.Key == h)?.Count ?? 0;

        return Results.Ok(new
        {
            asOf = now,
            assetCounts = counts.ToDictionary(c => Support.Label(c.Key), c => c.Count),
            board = new
            {
                atCounter = Column(AssetStatus.AtCounter), inTransit = Column(AssetStatus.InTransit),
                verifiedInRoom = Column(AssetStatus.InRoomVerified), anomaly = Column(AssetStatus.LocationAnomaly),
                overdue = Column(AssetStatus.Overdue),
            },
            rooms = await LoanRules.OccupancyAsync(db, null, now, ct),
            stationHealth = new
            {
                online = H(StationHealth.Online), warning = H(StationHealth.Warning), offline = H(StationHealth.Offline),
                total = health.Sum(x => x.Count),
            },
            openAnomalyCount = await db.Anomalies.CountAsync(a => a.Status != AnomalyStatus.Resolved, ct),
        });
    }
}
