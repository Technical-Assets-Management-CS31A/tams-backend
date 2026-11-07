using Microsoft.EntityFrameworkCore;
using Tams.Api.Auth;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Http;

namespace Tams.Api.Assets;

static class AssetEndpoints
{
    // PRD 7.2: checkout and return flows own every other status. Staff may only move an asset
    // between these three (IN_STORAGE <-> MAINTENANCE <-> RETIRED).
    static readonly AssetStatus[] StaffStatuses = [AssetStatus.InStorage, AssetStatus.Maintenance, AssetStatus.Retired];

    const int MaxPageSize = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/assets").WithTags("Assets");

        g.MapGet("", List).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        g.MapGet("{id:guid}", Get).RequireRoles(Roles.Admin, Roles.TechStaff, Roles.Custodian);
        g.MapPost("", Create).RequireRoles(Roles.Admin, Roles.TechStaff);
        g.MapPut("{id:guid}", Update).RequireRoles(Roles.Admin, Roles.TechStaff);
        g.MapDelete("{id:guid}", Delete).RequireRoles(Roles.Admin, Roles.TechStaff);
        // schema-v1-decisions #7: restore is ADMIN only.
        g.MapPost("{id:guid}/restore", Restore).RequireRoles(Roles.Admin);
    }

    static async Task<IResult> List(
        HttpContext http, TamsDbContext db, CancellationToken ct,
        Guid? categoryId, Guid? homeRoomId, Guid? borrowerStudentId, string? status, string? type, string? q, string? sort,
        bool includeDeleted = false, int page = 1, int pageSize = 25)
    {
        if (includeDeleted && !http.User.IsInRole(Roles.Admin) && !http.User.IsInRole(Roles.TechStaff))
            return Problems.Forbidden("INCLUDE_DELETED_FORBIDDEN", "Only ADMIN and TECH_STAFF can list deleted assets.");

        var errors = new Dictionary<string, string[]>();
        AssetStatus? statusFilter = null;
        AssetType? typeFilter = null;
        if (status is not null)
        {
            if (EnumText.TryParse<AssetStatus>(status, out var s)) statusFilter = s;
            else errors["status"] = ["Unknown asset status."];
        }
        if (type is not null)
        {
            if (EnumText.TryParse<AssetType>(type, out var t)) typeFilter = t;
            else errors["type"] = ["Unknown asset type (BORROWABLE or FIXED)."];
        }
        var sortKey = sort?.TrimStart('-').ToLowerInvariant();
        if (sortKey is not (null or "" or "assetcode" or "name" or "createdat"))
            errors["sort"] = ["Must be assetCode, name or createdAt, optionally prefixed with '-'."];
        if (errors.Count > 0) return Problems.Validation(errors);

        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Assets.AsNoTracking().AsQueryable();
        if (!includeDeleted) query = query.Where(a => a.DeletedAt == null);
        if (categoryId is not null) query = query.Where(a => a.CategoryId == categoryId);
        if (homeRoomId is not null) query = query.Where(a => a.HomeRoomId == homeRoomId);
        // FR-4.4 "borrower history": assets this student has ever borrowed or returned.
        if (borrowerStudentId is not null)
            query = query.Where(a => db.Loans.Any(l => l.AssetId == a.Id && (l.StudentId == borrowerStudentId || l.ReturnedByStudentId == borrowerStudentId)));
        if (statusFilter is not null) query = query.Where(a => a.Status == statusFilter);
        if (typeFilter is not null) query = query.Where(a => a.Type == typeFilter);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(a => EF.Functions.ILike(a.AssetCode, like) || EF.Functions.ILike(a.Name, like)
                || (a.RfidTagUid != null && EF.Functions.ILike(a.RfidTagUid, like)));
        }

        var total = await query.CountAsync(ct);
        var desc = sort?.StartsWith('-') == true;
        query = (sortKey, desc) switch
        {
            ("name", false) => query.OrderBy(a => a.Name).ThenBy(a => a.AssetCode),
            ("name", true) => query.OrderByDescending(a => a.Name).ThenBy(a => a.AssetCode),
            ("createdat", false) => query.OrderBy(a => a.CreatedAt).ThenBy(a => a.AssetCode),
            ("createdat", true) => query.OrderByDescending(a => a.CreatedAt).ThenBy(a => a.AssetCode),
            (_, true) => query.OrderByDescending(a => a.AssetCode),
            _ => query.OrderBy(a => a.AssetCode),
        };
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Results.Ok(new PagedResult<AssetDto>(items.Select(AssetDto.From).ToList(), page, pageSize, total));
    }

    static async Task<IResult> Get(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        // Deleted assets are only visible to those who may list them (same rule as rooms and stations).
        var maySeeDeleted = http.User.IsInRole(Roles.Admin) || http.User.IsInRole(Roles.TechStaff);
        var asset = await db.Assets.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id && (maySeeDeleted || a.DeletedAt == null), ct);
        return asset is null ? NotFound() : Results.Ok(AssetDto.From(asset));
    }

    static async Task<IResult> Create(AssetRequest req, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var v = await Validate(req, db, ct);
        if (v.Error is not null) return v.Error;

        var status = v.Status ?? AssetStatus.InStorage;
        if (!StaffStatuses.Contains(status))
            return Problems.Validation(new Dictionary<string, string[]> { ["status"] = ["New assets can only be IN_STORAGE, MAINTENANCE or RETIRED."] });

        if (await Collision(db, v.AssetCode, v.Uid, null, ct) is { } taken)
            return taken;

        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            AssetCode = v.AssetCode,
            Name = v.Name,
            CategoryId = v.CategoryId,
            Type = v.Type,
            Brand = Clean(req.Brand),
            Model = Clean(req.Model),
            SerialNo = Clean(req.SerialNo),
            RfidTagUid = v.Uid,
            HomeRoomId = v.HomeRoomId,
            CurrentRoomId = v.HomeRoomId,
            Status = status,
            Condition = v.Condition ?? AssetCondition.Good,
            ReplacementCost = v.Cost,
            AcquiredAt = req.AcquiredAt,
            Notes = Clean(req.Notes),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Assets.Add(asset);
        AssetAudit.Add(db, http, "ASSET_CREATED", "asset", asset.Id, null, AssetDto.From(asset));
        if (await TrySave(db, ct) is { } conflict) return conflict;
        return Results.Created($"/assets/{asset.Id}", AssetDto.From(asset));
    }

    static async Task<IResult> Update(Guid id, AssetRequest req, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (asset is null) return NotFound();
        if (await Blocked(db, asset, ct, forDelete: false) is { } blocked) return blocked;

        var v = await Validate(req, db, ct);
        if (v.Error is not null) return v.Error;

        if (v.Status is { } newStatus && newStatus != asset.Status)
        {
            if (!StaffStatuses.Contains(asset.Status))
                return Problems.Conflict("ASSET_STATUS_MANAGED_BY_SYSTEM",
                    $"Status {EnumText.ToApi(asset.Status)} is set by the checkout and return flows and cannot be changed here.");
            if (!StaffStatuses.Contains(newStatus))
                return Problems.Validation(new Dictionary<string, string[]> { ["status"] = ["Staff can only set IN_STORAGE, MAINTENANCE or RETIRED."] });
        }

        if (await Collision(db, v.AssetCode, v.Uid, id, ct) is { } taken)
            return taken;

        var before = AssetDto.From(asset);
        asset.AssetCode = v.AssetCode;
        asset.Name = v.Name;
        asset.CategoryId = v.CategoryId;
        asset.Type = v.Type;
        asset.Brand = Clean(req.Brand);
        asset.Model = Clean(req.Model);
        asset.SerialNo = Clean(req.SerialNo);
        asset.RfidTagUid = v.Uid;
        if (asset.HomeRoomId != v.HomeRoomId)
        {
            // A moved home room also moves an idle asset; one on a counter keeps its current room.
            if (asset.CurrentRoomId == asset.HomeRoomId) asset.CurrentRoomId = v.HomeRoomId;
            asset.HomeRoomId = v.HomeRoomId;
        }
        asset.Status = v.Status ?? asset.Status;
        asset.Condition = v.Condition ?? asset.Condition;
        asset.ReplacementCost = v.Cost;
        asset.AcquiredAt = req.AcquiredAt;
        asset.Notes = Clean(req.Notes);
        asset.UpdatedAt = DateTimeOffset.UtcNow;

        AssetAudit.Add(db, http, "ASSET_UPDATED", "asset", asset.Id, before, AssetDto.From(asset));
        if (await TrySave(db, ct) is { } conflict) return conflict;
        return Results.Ok(AssetDto.From(asset));
    }

    static async Task<IResult> Delete(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct);
        if (asset is null) return NotFound();
        if (await Blocked(db, asset, ct, forDelete: true) is { } blocked) return blocked;

        var before = AssetDto.From(asset);
        asset.DeletedAt = asset.UpdatedAt = DateTimeOffset.UtcNow;
        AssetAudit.Add(db, http, "ASSET_DELETED", "asset", asset.Id, before, AssetDto.From(asset));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    static async Task<IResult> Restore(Guid id, TamsDbContext db, HttpContext http, CancellationToken ct)
    {
        var asset = await db.Assets.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (asset is null) return NotFound();
        if (asset.DeletedAt is null) return Problems.Conflict("ASSET_NOT_DELETED", "Asset is not deleted.");

        // The unique indexes are partial (live rows only), so a live asset may now own the code or tag.
        if (await db.Assets.AnyAsync(a => a.Id != id && a.DeletedAt == null && a.AssetCode == asset.AssetCode, ct))
            return Problems.Conflict("RESTORE_CONFLICT", $"Cannot restore: asset_code '{asset.AssetCode}' is now used by another asset.");
        if (asset.RfidTagUid is not null
            && await db.Assets.AnyAsync(a => a.Id != id && a.DeletedAt == null && a.RfidTagUid == asset.RfidTagUid, ct))
            return Problems.Conflict("RESTORE_CONFLICT", $"Cannot restore: rfid_tag_uid '{asset.RfidTagUid}' is now assigned to another asset.");
        if (!await db.Rooms.AnyAsync(r => r.Id == asset.HomeRoomId && r.DeletedAt == null, ct))
            return Problems.Conflict("RESTORE_CONFLICT", "Cannot restore: home_room_id points at a deleted room. Restore the room first.");

        var before = AssetDto.From(asset);
        asset.DeletedAt = null;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        AssetAudit.Add(db, http, "ASSET_RESTORED", "asset", asset.Id, before, AssetDto.From(asset));
        if (await TrySave(db, ct, restore: true) is { } conflict) return conflict;
        return Results.Ok(AssetDto.From(asset));
    }

    // ---- helpers ----

    static IResult NotFound() => Problems.NotFound("ASSET_NOT_FOUND", "Asset not found.");

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Open loan (ACTIVE or OVERDUE) always blocks; deleting also needs a staff-managed status.</summary>
    static async Task<IResult?> Blocked(TamsDbContext db, Asset asset, CancellationToken ct, bool forDelete)
    {
        if (await db.Loans.AnyAsync(l => l.AssetId == asset.Id
                && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue), ct))
            return Problems.Conflict("ASSET_HAS_OPEN_LOAN", "Asset has an open loan. Return it first.");
        if (asset.Status == AssetStatus.AtCounter
            || (forDelete && !StaffStatuses.Contains(asset.Status)))
            return Problems.Conflict("ASSET_IN_USE",
                $"Asset is {EnumText.ToApi(asset.Status)} and held by a checkout or return flow.");
        return null;
    }

    /// <summary>409 when asset_code or rfid_tag_uid belongs to another live row (deleted rows do not block).</summary>
    static async Task<IResult?> Collision(TamsDbContext db, string code, string? uid, Guid? selfId, CancellationToken ct)
    {
        var codeRow = await db.Assets.Where(a => a.DeletedAt == null && a.Id != selfId && a.AssetCode == code)
            .Select(a => new { a.DeletedAt }).FirstOrDefaultAsync(ct);
        if (codeRow is not null)
            return Problems.Conflict("ASSET_CODE_TAKEN",
                $"asset_code '{code}' is already used" + (codeRow.DeletedAt is null ? "." : " by a deleted asset (restore it instead)."));

        if (uid is not null)
        {
            if (await db.AuthCards.AnyAsync(c => c.CardUid == uid, ct))
                return Problems.Conflict("TAG_ALREADY_ASSIGNED", $"UID {uid} is already registered as an authorization card.");
            var tagRow = await db.Assets.Where(a => a.DeletedAt == null && a.Id != selfId && a.RfidTagUid == uid)
                .Select(a => new { a.DeletedAt }).FirstOrDefaultAsync(ct);
            if (tagRow is not null)
                return Problems.Conflict("TAG_ALREADY_ASSIGNED",
                    $"RFID tag {uid} is already assigned to another asset" + (tagRow.DeletedAt is null ? "." : " (deleted)."));
        }
        return null;
    }

    // Backstop for races: the unique indexes still win.
    static async Task<IResult?> TrySave(TamsDbContext db, CancellationToken ct, bool restore = false)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (AssetAudit.IsUniqueViolation(ex))
        {
            var constraint = (ex.InnerException as Npgsql.PostgresException)?.ConstraintName ?? "";
            var tag = constraint.Contains("rfid_tag_uid");
            if (restore) return Problems.Conflict("RESTORE_CONFLICT", $"Cannot restore: {(tag ? "rfid_tag_uid" : "asset_code")} collides with another asset.");
            return tag
                ? Problems.Conflict("TAG_ALREADY_ASSIGNED", "RFID tag is already assigned to another asset.")
                : Problems.Conflict("ASSET_CODE_TAKEN", "asset_code is already used.");
        }
    }

    sealed record Valid(
        IResult? Error, string AssetCode, string Name, Guid CategoryId, AssetType Type, string? Uid, Guid HomeRoomId,
        AssetStatus? Status, AssetCondition? Condition, decimal Cost);

    static async Task<Valid> Validate(AssetRequest req, TamsDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        var code = req.AssetCode?.Trim() ?? "";
        var name = req.Name?.Trim() ?? "";
        if (code.Length is 0 or > 30) errors["assetCode"] = ["Asset code is required (max 30 characters)."];
        if (name.Length is 0 or > 120) errors["name"] = ["Name is required (max 120 characters)."];

        var type = default(AssetType);
        if (!EnumText.TryParse(req.Type, out type)) errors["type"] = ["Type must be BORROWABLE or FIXED."];

        AssetStatus? status = null;
        if (req.Status is not null)
        {
            if (EnumText.TryParse<AssetStatus>(req.Status, out var s)) status = s;
            else errors["status"] = ["Unknown asset status."];
        }
        AssetCondition? condition = null;
        if (req.Condition is not null)
        {
            if (EnumText.TryParse<AssetCondition>(req.Condition, out var c)) condition = c;
            else errors["condition"] = ["Condition must be GOOD, FAIR or DAMAGED."];
        }

        if (req.CategoryId is null) errors["categoryId"] = ["Category is required."];
        if (req.HomeRoomId is null) errors["homeRoomId"] = ["Home room is required."];
        if (req.ReplacementCost is null or < 0) errors["replacementCost"] = ["Replacement cost is required and cannot be negative."];
        else if (req.ReplacementCost >= 10_000_000_000m) errors["replacementCost"] = ["Replacement cost is too large."];
        foreach (var (field, value, max) in new[]
            { ("brand", req.Brand, 80), ("model", req.Model, 80), ("serialNo", req.SerialNo, 80) })
            if (value?.Trim().Length > max) errors[field] = [$"Must be {max} characters or fewer."];

        string? uid = null;
        if (!string.IsNullOrWhiteSpace(req.RfidTagUid))
        {
            if (RfidUid.TryNormalize(req.RfidTagUid, out var normalized)) uid = normalized;
            else errors["rfidTagUid"] = [$"Must be {RfidUid.MinLength}-{RfidUid.MaxLength} hex characters (even count); separators are ignored."];
        }
        else if (errors.ContainsKey("type") is false && type == AssetType.Borrowable)
            errors["rfidTagUid"] = ["Borrowable assets need an RFID tag UID."];

        if (errors.Count == 0)
        {
            if (!await db.AssetCategories.AnyAsync(c => c.Id == req.CategoryId, ct))
                errors["categoryId"] = ["Category does not exist."];
            if (!await db.Rooms.AnyAsync(r => r.Id == req.HomeRoomId && r.DeletedAt == null, ct))
                errors["homeRoomId"] = ["Room does not exist."];
        }

        if (errors.Count > 0)
            return new Valid(Problems.Validation(errors), "", "", default, default, null, default, null, null, 0);

        return new Valid(null, code, name, req.CategoryId!.Value, type, uid, req.HomeRoomId!.Value,
            status, condition, req.ReplacementCost!.Value);
    }
}
