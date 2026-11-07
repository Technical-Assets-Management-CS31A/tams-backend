using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Loans;

namespace Tams.Api.Events;

/// <param name="Status">PUT only: SCHEDULED, ACTIVE or CANCELLED. Closing goes through POST /events/{id}/close.</param>
public sealed record EventBody(string? Title, string? VenueText, Guid? VenueRoomId, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    Guid? CoordinatorUserId, string? Status);

/// <summary>F5 events (FR-5.1). Staff manage them; a coordinator sees their own and unlocks batch mode at the kiosk.</summary>
public static class EventEndpoints
{
    static readonly string[] Readers = [Roles.Admin, Roles.TechStaff, Roles.Custodian, Roles.Instructor];
    static readonly string[] Writers = [Roles.Admin, Roles.TechStaff];

    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/events").WithTags("Events");
        g.MapGet("", List).RequireRoles(Readers);
        g.MapGet("{id:guid}", Get).RequireRoles(Readers);
        g.MapPost("", Create).RequireRoles(Writers);
        g.MapPut("{id:guid}", Update).RequireRoles(Writers);
        g.MapPost("{id:guid}/close", Close).RequireRoles(Writers);
        return app;
    }

    static bool IsStaff(ClaimsPrincipal u) => u.IsInRole(Roles.Admin) || u.IsInRole(Roles.TechStaff) || u.IsInRole(Roles.Custodian);

    static object Dto(Event e, string? coordinatorName) => new
    {
        e.Id, e.Title, e.VenueText, e.VenueRoomId, e.StartsAt, e.EndsAt,
        coordinator = new { id = e.CoordinatorUserId, fullName = coordinatorName },
        status = Support.Label(e.Status), e.CreatedByUserId, e.CreatedAt, e.UpdatedAt,
    };

    static async Task<IResult> List(ClaimsPrincipal user, TamsDbContext db, CancellationToken ct,
        string? status, DateTimeOffset? from, DateTimeOffset? to, string? q, int page = 1, int pageSize = 25)
    {
        var query = db.Events.AsNoTracking().AsQueryable();
        if (status is not null)
        {
            if (!Support.TryParse<EventStatus>(status, out var st)) return Support.BadEnum<EventStatus>("status");
            query = query.Where(e => e.Status == st);
        }
        if (!IsStaff(user))
        {
            var me = AssetAudit.ActorId(user);
            query = query.Where(e => e.CoordinatorUserId == me);
        }
        if (from is not null) query = query.Where(e => e.EndsAt >= from);
        if (to is not null) query = query.Where(e => e.StartsAt <= to);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = $"%{q.Trim()}%";
            query = query.Where(e => EF.Functions.ILike(e.Title, like) || EF.Functions.ILike(e.VenueText, like));
        }
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var total = await query.CountAsync(ct);
        var rows = await (
            from e in query.OrderByDescending(e => e.StartsAt).Skip((page - 1) * pageSize).Take(pageSize)
            join u in db.Users on e.CoordinatorUserId equals u.Id
            select new { e, Name = u.FirstName + " " + u.LastName }).ToListAsync(ct);
        return Results.Ok(new { items = rows.OrderByDescending(r => r.e.StartsAt).Select(r => Dto(r.e, r.Name)), page, pageSize, total });
    }

    /// <summary>The event with its loans: open ones are what bulk return still has to collect (US-12).</summary>
    static async Task<IResult> Get(Guid id, ClaimsPrincipal user, TamsDbContext db, CancellationToken ct)
    {
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null || (!IsStaff(user) && e.CoordinatorUserId != AssetAudit.ActorId(user)))
            return Problems.NotFound("EVENT_NOT_FOUND", "Event not found.");
        var name = await db.Users.Where(u => u.Id == e.CoordinatorUserId).Select(u => u.FirstName + " " + u.LastName).FirstOrDefaultAsync(ct);
        var loans = await db.Loans.AsNoTracking().Where(l => l.EventId == id).OrderBy(l => l.CheckedOutAt).ToListAsync(ct);
        var open = loans.Count(l => l.Status is LoanStatus.Active or LoanStatus.Overdue or LoanStatus.Missing);
        return Results.Ok(new
        {
            @event = Dto(e, name),
            loanCounts = new { total = loans.Count, open, returned = loans.Count(l => l.Status == LoanStatus.Returned) },
            loans = await LoanEndpoints.DtosAsync(db, loans, ct),
        });
    }

    static async Task<IResult> Create(EventBody b, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        if (await ValidateAsync(b, db, ct) is { } bad) return bad;
        var now = DateTimeOffset.UtcNow;
        var e = new Event
        {
            Id = Guid.NewGuid(), Title = b.Title!.Trim(), VenueText = b.VenueText!.Trim(), VenueRoomId = b.VenueRoomId,
            StartsAt = b.StartsAt!.Value, EndsAt = b.EndsAt!.Value, CoordinatorUserId = b.CoordinatorUserId!.Value,
            Status = EventStatus.Scheduled, CreatedByUserId = AssetAudit.ActorId(http.User)!.Value, CreatedAt = now, UpdatedAt = now,
        };
        db.Events.Add(e);
        Support.Audit(db, http, "EVENT_CREATED", "event", e.Id, null, Dto(e, null));
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/events/{e.Id}", Dto(e, null));
    }

    static async Task<IResult> Update(Guid id, EventBody b, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var e = await db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return Problems.NotFound("EVENT_NOT_FOUND", "Event not found.");
        if (e.Status is EventStatus.Closed or EventStatus.Cancelled)
            return Problems.Conflict("EVENT_FINISHED", $"A {Support.Label(e.Status)} event cannot be edited.");
        if (await ValidateAsync(b, db, ct) is { } bad) return bad;

        var status = e.Status;
        if (b.Status is not null && (!Support.TryParse(b.Status, out status) || status == EventStatus.Closed))
            return Problems.Validation(new Dictionary<string, string[]> { ["status"] = ["SCHEDULED, ACTIVE or CANCELLED (close with POST /events/{id}/close)."] });

        var before = Dto(e, null);
        e.Title = b.Title!.Trim();
        e.VenueText = b.VenueText!.Trim();
        e.VenueRoomId = b.VenueRoomId;
        e.StartsAt = b.StartsAt!.Value;
        e.EndsAt = b.EndsAt!.Value;
        e.CoordinatorUserId = b.CoordinatorUserId!.Value;
        e.Status = status;
        e.UpdatedAt = DateTimeOffset.UtcNow;
        Support.Audit(db, http, "EVENT_UPDATED", "event", e.Id, before, Dto(e, null));
        await db.SaveChangesAsync(ct);
        return Results.Ok(Dto(e, null));
    }

    /// <summary>Ends the event. Loans still out stay open (and go overdue at end + grace) until returned.</summary>
    static async Task<IResult> Close(Guid id, HttpContext http, TamsDbContext db, CancellationToken ct)
    {
        var e = await db.Events.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (e is null) return Problems.NotFound("EVENT_NOT_FOUND", "Event not found.");
        if (e.Status is EventStatus.Closed or EventStatus.Cancelled)
            return Problems.Conflict("EVENT_FINISHED", $"The event is already {Support.Label(e.Status)}.");
        var before = Dto(e, null);
        e.Status = EventStatus.Closed;
        e.UpdatedAt = DateTimeOffset.UtcNow;
        Support.Audit(db, http, "EVENT_CLOSED", "event", e.Id, before, Dto(e, null));
        await db.SaveChangesAsync(ct);
        return Results.Ok(Dto(e, null));
    }

    static async Task<IResult?> ValidateAsync(EventBody b, TamsDbContext db, CancellationToken ct)
    {
        var e = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(b.Title) || b.Title.Trim().Length > 150) e["title"] = ["Required, up to 150 characters."];
        if (string.IsNullOrWhiteSpace(b.VenueText) || b.VenueText.Trim().Length > 150) e["venueText"] = ["Required, up to 150 characters."];
        if (b.StartsAt is null) e["startsAt"] = ["Required."];
        if (b.EndsAt is null) e["endsAt"] = ["Required."];
        if (b.StartsAt is { } s && b.EndsAt is { } end && end <= s) e["endsAt"] = ["Must be after startsAt."];
        if (b.CoordinatorUserId is null) e["coordinatorUserId"] = ["Required."];
        if (e.Count > 0) return Problems.Validation(e);

        if (!await db.Users.AnyAsync(u => u.Id == b.CoordinatorUserId && u.IsEventCoordinator && u.Status == RecordStatus.Active
                && u.DeletedAt == null, ct))
            return Problems.BadRequest("COORDINATOR_INVALID", "coordinatorUserId must be an active user flagged as event coordinator.");
        if (b.VenueRoomId is Guid room && !await db.Rooms.AnyAsync(r => r.Id == room && r.DeletedAt == null, ct))
            return Problems.BadRequest("ROOM_NOT_FOUND", "venueRoomId does not match an existing room.");
        return null;
    }
}
