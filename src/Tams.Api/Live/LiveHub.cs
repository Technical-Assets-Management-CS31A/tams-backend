using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Tams.Api.Assets;
using Tams.Api.Auth;

namespace Tams.Api.Live;

/// <summary>
/// PRD 6.4 real-time events at /hubs/live. Staff (ADMIN, TECH_STAFF, CUSTODIAN) join "staff"; every
/// account joins "user:{id}" so instructor events (approval.requested, warnings on loans they approved,
/// instructor.frozen) reach only that person. Browsers pass the JWT as ?access_token= (AuthSetup).
/// Server-to-client only: the hub has no callable methods.
/// </summary>
[Authorize]
public sealed class LiveHub : Hub
{
    public const string StaffGroup = "staff";
    public static string UserGroup(Guid id) => $"user:{id}";

    public override async Task OnConnectedAsync()
    {
        var u = Context.User!;
        if (u.IsInRole(Roles.Admin) || u.IsInRole(Roles.TechStaff) || u.IsInRole(Roles.Custodian))
            await Groups.AddToGroupAsync(Context.ConnectionId, StaffGroup);
        if (AssetAudit.ActorId(u) is Guid id)
            await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(id));
        await base.OnConnectedAsync();
    }
}

/// <summary>Events collected during a unit of work and sent after it commits, so a rollback sends nothing.</summary>
public sealed class Outbox
{
    readonly List<(string Group, string Event, object Payload)> items = [];

    public void Staff(string evt, object payload) => items.Add((LiveHub.StaffGroup, evt, payload));

    public void User(Guid? userId, string evt, object payload)
    {
        if (userId is Guid id) items.Add((LiveHub.UserGroup(id), evt, payload));
    }

    public async Task FlushAsync(IHubContext<LiveHub> hub)
    {
        foreach (var (group, evt, payload) in items) await hub.Clients.Group(group).SendAsync(evt, payload);
        items.Clear();
    }
}
