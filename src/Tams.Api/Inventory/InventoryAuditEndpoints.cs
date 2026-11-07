using System.Text;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Loans;
using Tams.Api.Qr;
using Tams.Api.Students;

namespace Tams.Api.Inventory;

public sealed record RosterAddRequest(List<Guid>? AssetIds);
public sealed record StartAuditRequest(Guid? RoomId);

/// <param name="ClientScanId">Generated on the phone; a re-sync of the same scan is ignored (FR-7.4).</param>
/// <param name="ScannedValue">Raw QR text, tams:asset:&lt;uuid&gt;.</param>
/// <param name="ScannedAt">Device time of the scan (US-13 keeps original timestamps).</param>
public sealed record AuditScan(Guid? ClientScanId, string? ScannedValue, DateTimeOffset? ScannedAt);
public sealed record ScanBatchRequest(List<AuditScan>? Scans);

/// <summary>
/// F7 physical inventory audits. A room's roster is every live, non-retired asset whose home room it is.
/// Scans are classified against the roster: MATCHED (belongs here), MISPLACED (belongs elsewhere) or
/// UNKNOWN; completing the audit records every unscanned roster item as MISSING.
/// </summary>
public static class InventoryAuditEndpoints
{
    static readonly string[] Auditors = [Roles.Admin, Roles.TechStaff, Roles.Custodian];
    static readonly string[] Staff = [Roles.Admin, Roles.TechStaff];
    const long MaxImportBytes = 2 * 1024 * 1024;

    public static IEndpointRouteBuilder MapInventoryAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var rooms = app.MapGroup("/rooms/{id:guid}").WithTags("Audits");
        rooms.MapGet("/roster", Roster).RequireRoles(Auditors);
        rooms.MapPost("/roster", AddToRoster).RequireRoles(Staff);
        rooms.MapGet("/checklist:export", ExportChecklist).RequireRoles(Auditors);

        var g = app.MapGroup("/audits").WithTags("Audits");
        g.MapGet("", List).RequireRoles(Auditors);
        g.MapGet("{id:guid}", Get).RequireRoles(Auditors);
        g.MapPost("", Start).RequireRoles(Auditors);
        g.MapPost("{id:guid}/scans:batch", ScanBatch).RequireRoles(Auditors);
        g.MapPost("{id:guid}/complete", Complete).RequireRoles(Auditors);
        g.MapPost("{id:guid}/sign-off", SignOff).RequireRoles(Staff);
        g.MapGet("{id:guid}/report", Report).RequireRoles(Auditors);
        g.MapPost("import", Import).RequireRoles(Staff).Accepts<IFormFile>("multipart/form-data");
        return app;
    }

    static IResult RoomNotFound() => Problems.NotFound("ROOM_NOT_FOUND", "Room not found.");
    static IResult AuditNotFound() => Problems.NotFound("AUDIT_NOT_FOUND", "Audit not found.");

    sealed record RosterItem(Guid Id, string AssetCode, string Name, string Category, string Type, string Status,
        string? SerialNo, string QrPayload);

    sealed record Line(Guid Id, AuditResult Result, Guid? AssetId, string? AssetCode, string? Name, string ScannedValue,
        DateTimeOffset ScannedAt, DateTimeOffset SyncedAt);

    static IQueryable<Asset> RosterOf(TamsDbContext db, Guid roomId) =>
        db.Assets.AsNoTracking().Where(a => a.HomeRoomId == roomId && a.DeletedAt == null && a.Status != AssetStatus.Retired);

    static async Task<List<RosterItem>> RosterItemsAsync(TamsDbContext db, Guid roomId, CancellationToken ct)
    {
        var rows = await (
            from a in RosterOf(db, roomId)
            join c in db.AssetCategories on a.CategoryId equals c.Id
            orderby a.AssetCode
            select new { a.Id, a.AssetCode, a.Name, Category = c.Name, a.Type, a.Status, a.SerialNo }).ToListAsync(ct);
        return rows.Select(r => new RosterItem(r.Id, r.AssetCode, r.Name, r.Category, Support.Label(r.Type), Support.Label(r.Status),
            r.SerialNo, QrCodes.Payload(r.Id))).ToList();
    }

    static object Dto(Audit a) => new
    {
        a.Id, a.RoomId, source = Support.Label(a.Source), status = Support.Label(a.Status), a.StartedByUserId, a.StartedAt,
        a.CompletedAt, a.SignedOffByUserId, a.SignedOffAt, a.ExpectedCount, a.MatchedCount, a.MissingCount, a.MisplacedCount,
        a.UnknownCount, a.ImportFileId,
        reportPdfUrl = $"/api/audits/{a.Id}/report?format=pdf", reportCsvUrl = $"/api/audits/{a.Id}/report?format=csv",
    };

    static Task<List<Line>> LinesAsync(TamsDbContext db, Guid auditId, CancellationToken ct) =>
        (from i in db.AuditItems.AsNoTracking()
         where i.AuditId == auditId
         from a in db.Assets.Where(a => a.Id == i.AssetId).DefaultIfEmpty()
         orderby i.Result, i.ScannedAt
         select new Line(i.Id, i.Result, i.AssetId, a == null ? null : a.AssetCode, a == null ? null : a.Name, i.ScannedValue,
             i.ScannedAt, i.SyncedAt)).ToListAsync(ct);

    static async Task<object> DetailAsync(TamsDbContext db, Audit a, CancellationToken ct) => new
    {
        audit = Dto(a),
        items = (await LinesAsync(db, a.Id, ct)).Select(l => new
        {
            l.Id, result = Support.Label(l.Result), l.AssetId, l.AssetCode, l.Name, l.ScannedValue, l.ScannedAt, l.SyncedAt,
        }),
    };

    // ---------------------------------------------------------------- rosters (FR-7.2)

    static async Task<IResult> Roster(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct);
        if (room is null) return RoomNotFound();
        return Results.Ok(new
        {
            room = new { room.Id, room.Code, room.Name }, items = await RosterItemsAsync(db, id, ct), downloadedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>Batch add: makes this room the home room of the given assets. QR labels: POST /assets/qr-sheet.</summary>
    static async Task<IResult> AddToRoster(Guid id, RosterAddRequest req, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct);
        if (room is null) return RoomNotFound();
        var ids = req.AssetIds?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > 500)
            return Problems.Validation(new Dictionary<string, string[]> { ["assetIds"] = ["1 to 500 asset ids."] });
        var assets = await db.Assets.Where(a => ids.Contains(a.Id) && a.DeletedAt == null).ToListAsync(ct);
        if (assets.Count != ids.Count)
            return Problems.BadRequest("ASSET_NOT_FOUND", $"Unknown or deleted asset ids: {string.Join(", ", ids.Except(assets.Select(a => a.Id)))}.");

        var moved = 0;
        foreach (var a in assets.Where(a => a.HomeRoomId != id))
        {
            var before = new { a.HomeRoomId, a.CurrentRoomId };
            a.HomeRoomId = id;
            // Items out on loan keep their live location; the rest are now where their roster says.
            if (a.Status is AssetStatus.InStorage or AssetStatus.Maintenance or AssetStatus.Retired) a.CurrentRoomId = id;
            a.UpdatedAt = DateTimeOffset.UtcNow;
            Support.Audit(db, http, "ASSET_ROSTER_MOVED", "asset", a.Id, before, new { a.HomeRoomId, a.CurrentRoomId });
            moved++;
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { room = new { room.Id, room.Code, room.Name }, moved, items = await RosterItemsAsync(db, id, ct) });
    }

    // ---------------------------------------------------------------- audits (FR-7.3 to FR-7.5)

    static async Task<IResult> List(TamsDbContext db, CancellationToken ct, Guid? roomId, string? status, int page = 1, int pageSize = 25)
    {
        var query = db.Audits.AsNoTracking().AsQueryable();
        if (roomId is not null) query = query.Where(a => a.RoomId == roomId);
        if (status is not null)
        {
            if (!Support.TryParse<AuditStatus>(status, out var st)) return Support.BadEnum<AuditStatus>("status");
            query = query.Where(a => a.Status == st);
        }
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.StartedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new { items = rows.Select(Dto), page, pageSize, total });
    }

    static async Task<IResult> Get(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var a = await db.Audits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is null ? AuditNotFound() : Results.Ok(await DetailAsync(db, a, ct));
    }

    /// <summary>Opens an audit and returns the roster for the phone to cache (offline audits, FR-7.4).</summary>
    static async Task<IResult> Start(StartAuditRequest req, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var room = req.RoomId is not Guid rid ? null : await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rid && r.DeletedAt == null, ct);
        if (room is null) return Problems.Validation(new Dictionary<string, string[]> { ["roomId"] = ["Required: an existing room."] });
        var roster = await RosterItemsAsync(db, room.Id, ct);
        var audit = new Audit
        {
            Id = Guid.NewGuid(), RoomId = room.Id, Source = AuditSource.Mobile, Status = AuditStatus.InProgress,
            StartedByUserId = AssetAudit.ActorId(http.User)!.Value, StartedAt = DateTimeOffset.UtcNow, ExpectedCount = roster.Count,
        };
        db.Audits.Add(audit);
        Support.Audit(db, http, "AUDIT_STARTED", "audit", audit.Id, null, new { audit.RoomId, audit.ExpectedCount });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/audits/{audit.Id}", new { audit = Dto(audit), room = new { room.Id, room.Code, room.Name }, roster });
    }

    /// <summary>Offline sync (FR-7.4): idempotent on clientScanId, so a phone can resend a whole queue safely.</summary>
    static async Task<IResult> ScanBatch(Guid id, ScanBatchRequest req, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var scans = req.Scans ?? [];
        if (scans.Count is 0 or > 1000)
            return Problems.Validation(new Dictionary<string, string[]> { ["scans"] = ["1 to 1000 scans."] });
        if (scans.Any(s => s.ClientScanId is null || s.ClientScanId == Guid.Empty || string.IsNullOrWhiteSpace(s.ScannedValue) || s.ScannedValue.Length > 200))
            return Problems.Validation(new Dictionary<string, string[]> { ["scans"] = ["Each scan needs clientScanId and scannedValue (up to 200 characters)."] });

        var audit = await db.Audits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (audit is null) return AuditNotFound();
        if (audit.Status != AuditStatus.InProgress) return Problems.Conflict("AUDIT_NOT_IN_PROGRESS", "The audit is already completed.");

        var clientIds = scans.Select(s => s.ClientScanId!.Value).Distinct().ToList();
        var known = await db.AuditItems.AsNoTracking().Where(i => clientIds.Contains(i.ClientScanId)).ToDictionaryAsync(i => i.ClientScanId, ct);
        var parsed = scans.Select(s => (Scan: s, AssetId: ParseAssetId(s.ScannedValue!))).ToList();
        var assetIds = parsed.Select(p => p.AssetId).OfType<Guid>().Distinct().ToList();
        var assets = await db.Assets.AsNoTracking().Where(a => assetIds.Contains(a.Id) && a.DeletedAt == null)
            .ToDictionaryAsync(a => a.Id, a => new { a.HomeRoomId, a.AssetCode, a.Name }, ct);

        var now = DateTimeOffset.UtcNow;
        var actor = AssetAudit.ActorId(http.User)!.Value;
        var seen = new HashSet<Guid>();
        var results = new List<object>();
        var accepted = 0;
        foreach (var (scan, assetId) in parsed)
        {
            var cid = scan.ClientScanId!.Value;
            if (known.TryGetValue(cid, out var stored))
            {
                results.Add(new { clientScanId = cid, result = Support.Label(stored.Result), assetId = stored.AssetId, duplicate = true });
                continue;
            }
            if (!seen.Add(cid)) continue; // repeated inside this batch

            var asset = assetId is Guid aid && assets.TryGetValue(aid, out var x) ? x : null;
            var result = asset is null ? AuditResult.Unknown : asset.HomeRoomId == audit.RoomId ? AuditResult.Matched : AuditResult.Misplaced;
            db.AuditItems.Add(new AuditItem
            {
                Id = Guid.NewGuid(), AuditId = id, AssetId = asset is null ? null : assetId, ScannedValue = scan.ScannedValue!.Trim(),
                Result = result, ClientScanId = cid, ScannedByUserId = actor, ScannedAt = scan.ScannedAt ?? now, SyncedAt = now,
            });
            accepted++;
            results.Add(new
            {
                clientScanId = cid, result = Support.Label(result), assetId = asset is null ? null : assetId, duplicate = false,
                assetCode = asset?.AssetCode, name = asset?.Name,
            });
        }
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (Support.IsUniqueViolation(ex))
        {
            // Two syncs of the same queue raced; the other one stored these scans.
            return Problems.Conflict("SYNC_IN_PROGRESS", "Another sync of these scans is running; retry.");
        }
        return Results.Ok(new { auditId = id, accepted, duplicates = scans.Count - accepted, results });
    }

    static async Task<IResult> Complete(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var audit = (await db.Audits.FromSqlInterpolated($"SELECT * FROM audits WHERE id = {id} FOR UPDATE").ToListAsync(ct)).FirstOrDefault();
        if (audit is null) return AuditNotFound();
        if (audit.Status != AuditStatus.InProgress) return Problems.Conflict("AUDIT_NOT_IN_PROGRESS", "The audit is already completed.");
        await FinalizeAsync(db, audit, AssetAudit.ActorId(http.User)!.Value, DateTimeOffset.UtcNow, ct);
        Support.Audit(db, http, "AUDIT_COMPLETED", "audit", audit.Id, null, Dto(audit));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(await DetailAsync(db, audit, ct));
    }

    /// <summary>FR-7.5: unscanned roster items become MISSING. Counts are distinct assets (scanned values for UNKNOWN).</summary>
    static async Task FinalizeAsync(TamsDbContext db, Audit audit, Guid actor, DateTimeOffset now, CancellationToken ct)
    {
        await db.SaveChangesAsync(ct); // imported rows first, so they are counted
        var roster = await RosterOf(db, audit.RoomId).Select(a => a.Id).ToListAsync(ct);
        var items = await db.AuditItems.AsNoTracking().Where(i => i.AuditId == audit.Id).ToListAsync(ct);
        var matched = items.Where(i => i.Result == AuditResult.Matched && i.AssetId != null).Select(i => i.AssetId!.Value).ToHashSet();
        foreach (var assetId in roster.Where(r => !matched.Contains(r)))
            db.AuditItems.Add(new AuditItem
            {
                Id = Guid.NewGuid(), AuditId = audit.Id, AssetId = assetId, ScannedValue = QrCodes.Payload(assetId),
                Result = AuditResult.Missing, ClientScanId = Guid.NewGuid(), ScannedByUserId = actor, ScannedAt = now, SyncedAt = now,
            });
        audit.ExpectedCount = roster.Count;
        audit.MatchedCount = roster.Count(matched.Contains);
        audit.MissingCount = roster.Count - audit.MatchedCount;
        audit.MisplacedCount = items.Where(i => i.Result == AuditResult.Misplaced).Select(i => i.AssetId).Distinct().Count();
        audit.UnknownCount = items.Where(i => i.Result == AuditResult.Unknown).Select(i => i.ScannedValue).Distinct().Count();
        audit.Status = AuditStatus.Completed;
        audit.CompletedAt = now;
    }

    static async Task<IResult> SignOff(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var audit = await db.Audits.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (audit is null) return AuditNotFound();
        if (audit.Status != AuditStatus.Completed) return Problems.Conflict("AUDIT_NOT_COMPLETED", "Only a completed audit can be signed off.");
        audit.Status = AuditStatus.SignedOff;
        audit.SignedOffByUserId = AssetAudit.ActorId(http.User);
        audit.SignedOffAt = DateTimeOffset.UtcNow;
        Support.Audit(db, http, "AUDIT_SIGNED_OFF", "audit", audit.Id, null, Dto(audit));
        await db.SaveChangesAsync(ct);
        return Results.Ok(Dto(audit));
    }

    // ---------------------------------------------------------------- reports and checklists (FR-7.6, FR-7.7)

    /// <summary>PDF or CSV with counts, completion time, variance and sign-off lines. Rendered on request.</summary>
    static async Task<IResult> Report(Guid id, TamsDbContext db, CancellationToken ct, string format = "pdf")
    {
        if (format is not ("pdf" or "csv"))
            return Problems.Validation(new Dictionary<string, string[]> { ["format"] = ["pdf or csv."] });
        var audit = await db.Audits.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (audit is null) return AuditNotFound();
        if (audit.Status == AuditStatus.InProgress) return Problems.Conflict("AUDIT_NOT_COMPLETED", "Complete the audit first.");
        var room = await db.Rooms.AsNoTracking().FirstAsync(r => r.Id == audit.RoomId, ct);
        var names = await db.Users.AsNoTracking().Where(u => u.Id == audit.StartedByUserId || u.Id == audit.SignedOffByUserId)
            .ToDictionaryAsync(u => u.Id, u => $"{u.FirstName} {u.LastName}", ct);
        var lines = await LinesAsync(db, id, ct);
        var stamp = LocalTime.ToLocal(audit.CompletedAt ?? audit.StartedAt).ToString("yyyyMMdd");
        return format == "csv"
            ? Results.File(Bom(ReportCsv(audit, room, lines)), "text/csv", $"audit-{room.Code}-{stamp}.csv")
            : Results.File(ReportPdf(audit, room, names.GetValueOrDefault(audit.StartedByUserId) ?? "-",
                audit.SignedOffByUserId is Guid s ? names.GetValueOrDefault(s) : null, lines), "application/pdf", $"audit-{room.Code}-{stamp}.pdf");
    }

    static string When(DateTimeOffset? t) => t is { } v ? LocalTime.ToLocal(v).ToString("yyyy-MM-dd h:mm tt") : "-";

    static byte[] ReportPdf(Audit a, Room room, string startedBy, string? signedBy, IReadOnlyList<Line> lines)
    {
        var pdf = new MiniPdf();
        pdf.NewPage();
        double y = 50;
        void Text(string text, double size = 10, bool bold = false, double gap = 14)
        {
            if (y > MiniPdf.PageHeight - 50) { pdf.NewPage(); y = 50; }
            pdf.Text(40, y, size, text.Length > 100 ? text[..100] : text, bold);
            y += gap;
        }

        Text("TAMS Physical Inventory Audit", 16, true, 26);
        Text($"Room: {room.Code} - {room.Name}");
        Text($"Started: {When(a.StartedAt)} by {startedBy}");
        Text($"Completed: {When(a.CompletedAt)}");
        Text($"Source: {Support.Label(a.Source)}    Status: {Support.Label(a.Status)}", gap: 22);
        Text($"Expected {a.ExpectedCount}    Matched {a.MatchedCount}    Missing {a.MissingCount}    Misplaced {a.MisplacedCount}    Unknown {a.UnknownCount}", 11, true);
        var found = a.ExpectedCount == 0 ? 100 : 100.0 * a.MatchedCount / a.ExpectedCount;
        Text($"Variance: {a.MatchedCount - a.ExpectedCount} ({found:0.#}% of the roster found)", gap: 24);
        foreach (var (title, result) in new[] { ("Missing", AuditResult.Missing), ("Misplaced", AuditResult.Misplaced),
                     ("Unknown", AuditResult.Unknown), ("Matched", AuditResult.Matched) })
        {
            var group = lines.Where(l => l.Result == result).ToList();
            Text($"{title} ({group.Count})", 12, true, 16);
            foreach (var l in group) Text($"   {l.AssetCode ?? "-"}   {l.Name ?? l.ScannedValue}", 9, gap: 12);
            y += 8;
        }
        y += 24;
        Text("Audited by: ________________________________    Date: ______________", gap: 30);
        Text("Verified by: _______________________________    Date: ______________", gap: 30);
        if (signedBy is not null) Text($"Signed off in TAMS by {signedBy} on {When(a.SignedOffAt)}", 9);
        return pdf.ToBytes();
    }

    static byte[] ReportCsv(Audit a, Room room, IReadOnlyList<Line> lines)
    {
        var sb = new StringBuilder();
        sb.AppendLine("room,started_at,completed_at,expected,matched,missing,misplaced,unknown");
        sb.AppendLine(string.Join(',', Csv(room.Code), a.StartedAt.ToString("O"), a.CompletedAt?.ToString("O") ?? "",
            a.ExpectedCount, a.MatchedCount, a.MissingCount, a.MisplacedCount, a.UnknownCount));
        sb.AppendLine();
        sb.AppendLine("result,asset_id,asset_code,name,scanned_value,scanned_at");
        foreach (var l in lines)
            sb.AppendLine(string.Join(',', Support.Label(l.Result), l.AssetId?.ToString() ?? "", Csv(l.AssetCode), Csv(l.Name),
                Csv(l.ScannedValue), l.ScannedAt.ToString("O")));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>FR-7.6: the room roster as a CSV (opens in Excel) with an empty found column to fill in.</summary>
    static async Task<IResult> ExportChecklist(Guid id, TamsDbContext db, CancellationToken ct)
    {
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id && r.DeletedAt == null, ct);
        if (room is null) return RoomNotFound();
        var sb = new StringBuilder();
        sb.AppendLine("asset_id,asset_code,name,category,serial_no,found,notes");
        foreach (var r in await RosterItemsAsync(db, id, ct))
            sb.AppendLine(string.Join(',', r.Id, Csv(r.AssetCode), Csv(r.Name), Csv(r.Category), Csv(r.SerialNo), "", ""));
        return Results.File(Bom(Encoding.UTF8.GetBytes(sb.ToString())), "text/csv", $"checklist-{room.Code}.csv");
    }

    /// <summary>
    /// FR-7.6 / US-17: re-import a filled checklist (multipart: file, roomId). Rows marked found are matched
    /// (or misplaced if the asset belongs elsewhere, unknown if no such asset); the rest of the roster is missing.
    /// </summary>
    static async Task<IResult> Import(HttpContext http, TamsDbContext db, IConfiguration cfg, CancellationToken ct)
    {
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(ct) : null;
        if (!Guid.TryParse(form?["roomId"], out var roomId))
            return Problems.Validation(new Dictionary<string, string[]> { ["roomId"] = ["Required form field."] });
        var bytes = await MediaStore.ReadAllAsync(form?.Files["file"], MaxImportBytes, ct);
        if (bytes is null) return Problems.BadRequest("FILE_REQUIRED", "Send the checklist CSV (up to 2 MB) in a 'file' field.");
        var room = await db.Rooms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roomId && r.DeletedAt == null, ct);
        if (room is null) return RoomNotFound();

        List<CsvReader.Record> records;
        try { records = CsvReader.Parse(Encoding.UTF8.GetString(bytes)); }
        catch (FormatException ex) { return Problems.BadRequest("CSV_MALFORMED", ex.Message); }
        if (records.Count == 0) return Problems.BadRequest("CSV_EMPTY", "The file has no header row.");
        var header = records[0].Fields.Select(f => f.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => header.IndexOf(name);
        var (idCol, codeCol, foundCol) = (Col("asset_id"), Col("asset_code"), Col("found"));
        if (foundCol < 0 || (idCol < 0 && codeCol < 0))
            return Problems.BadRequest("CSV_HEADER", "Needs a found column and asset_id or asset_code (use GET /rooms/{id}/checklist:export).");

        var actor = AssetAudit.ActorId(http.User)!.Value;
        var now = DateTimeOffset.UtcNow;
        var assets = await db.Assets.AsNoTracking().Where(a => a.DeletedAt == null)
            .Select(a => new { a.Id, a.AssetCode, a.HomeRoomId }).ToListAsync(ct);
        var byId = assets.ToDictionary(a => a.Id);
        var byCode = assets.ToDictionary(a => a.AssetCode, StringComparer.OrdinalIgnoreCase);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var media = await MediaStore.SaveAsync(db, cfg, MediaKind.AuditImport, "audit-imports", bytes, "text/csv", "csv", ct);
        var audit = new Audit
        {
            Id = Guid.NewGuid(), RoomId = room.Id, Source = AuditSource.ExternalImport, Status = AuditStatus.InProgress,
            StartedByUserId = actor, StartedAt = now, ImportFileId = media.Id,
        };
        db.Audits.Add(audit);
        string Field(CsvReader.Record r, int i) => i >= 0 && i < r.Fields.Count ? r.Fields[i].Trim() : "";
        foreach (var r in records.Skip(1))
        {
            if (Field(r, foundCol).ToUpperInvariant() is not ("Y" or "YES" or "TRUE" or "1" or "X")) continue;
            var idText = Field(r, idCol);
            var code = Field(r, codeCol);
            var asset = Guid.TryParse(idText, out var g) && byId.TryGetValue(g, out var a1) ? a1
                : code.Length > 0 && byCode.TryGetValue(code, out var a2) ? a2 : null;
            var value = idText.Length > 0 ? idText : code;
            if (value.Length == 0) continue;
            db.AuditItems.Add(new AuditItem
            {
                Id = Guid.NewGuid(), AuditId = audit.Id, AssetId = asset?.Id, ScannedValue = value.Length > 200 ? value[..200] : value,
                Result = asset is null ? AuditResult.Unknown : asset.HomeRoomId == room.Id ? AuditResult.Matched : AuditResult.Misplaced,
                ClientScanId = Guid.NewGuid(), ScannedByUserId = actor, ScannedAt = now, SyncedAt = now,
            });
        }
        await FinalizeAsync(db, audit, actor, now, ct);
        Support.Audit(db, http, "AUDIT_IMPORTED", "audit", audit.Id, null, Dto(audit));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Created($"/api/audits/{audit.Id}", await DetailAsync(db, audit, ct));
    }

    /// <summary>QR payload is tams:asset:&lt;uuid&gt; (PRD 7.1); a bare uuid is accepted too.</summary>
    static Guid? ParseAssetId(string value)
    {
        var v = value.Trim();
        const string prefix = "tams:asset:";
        if (v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) v = v[prefix.Length..];
        return Guid.TryParse(v, out var g) ? g : null;
    }

    static byte[] Bom(byte[] utf8) => [.. Encoding.UTF8.Preamble, .. utf8]; // so Excel reads UTF-8

    /// <summary>RFC 4180 quoting, and a leading quote on =, +, - and @ so a spreadsheet never runs a cell as a formula.</summary>
    static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s[0] is '=' or '+' or '-' or '@') s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
