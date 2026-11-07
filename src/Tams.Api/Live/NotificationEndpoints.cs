using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Assets;
using Tams.Api.Auth;
using Tams.Api.Data;
using Tams.Api.Http;

namespace Tams.Api.Live;

/// <summary>
/// My notifications (PRD 6.3): the instructor's Alerts screen (FR-2.6) and staff notices such as
/// STATION_OFFLINE. Rows are written by the loan rules and the sweeper; push delivery is not wired yet,
/// so delivered_at stays null and clients read them here or get the live event over SignalR.
/// </summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/notifications").WithTags("Notifications").RequireAuthorization(Policies.AnyUser);
        g.MapGet("", List);
        g.MapPost("{id:guid}/read", Read);
        return app;
    }

    static async Task<IResult> List(ClaimsPrincipal user, TamsDbContext db, CancellationToken ct,
        bool unread = false, int page = 1, int pageSize = 25)
    {
        if (AssetAudit.ActorId(user) is not Guid me) return Problems.Unauthorized("UNAUTHORIZED", "Missing user identity.");
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Notifications.AsNoTracking().Where(n => n.RecipientUserId == me);
        if (unread) query = query.Where(n => n.ReadAt == null);
        var total = await query.CountAsync(ct);
        var unreadCount = await db.Notifications.CountAsync(n => n.RecipientUserId == me && n.ReadAt == null, ct);
        var rows = await query.OrderByDescending(n => n.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var items = rows.Select(n => new
        {
            n.Id, n.Type, n.Title, n.Body, n.LoanId, n.StudentId, n.CreatedAt, n.ReadAt,
            payload = n.Payload is null ? (JsonElement?)null : JsonDocument.Parse(n.Payload).RootElement.Clone(),
        });
        return Results.Ok(new { items, page, pageSize, total, unreadCount });
    }

    static async Task<IResult> Read(Guid id, ClaimsPrincipal user, TamsDbContext db, CancellationToken ct)
    {
        var me = AssetAudit.ActorId(user);
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientUserId == me, ct);
        if (n is null) return Problems.NotFound("NOTIFICATION_NOT_FOUND", "Notification not found.");
        n.ReadAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { n.Id, n.ReadAt });
    }
}
