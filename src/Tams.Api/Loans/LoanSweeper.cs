using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Facilities;
using Tams.Api.Kiosk;
using Tams.Api.Live;
using Tams.Api.Settings;

namespace Tams.Api.Loans;

/// <summary>
/// PRD 6.1 background jobs. Every 5 s: expire kiosk sessions (FR-1.7) and recompute station health, so a
/// station turns Offline within seconds of its second missed window (US-15). Every 15 s: transit timeouts
/// (FR-4.3), overdue loans (FR-6.3) and SCHEDULED events that have started. Each loan is its own unit of
/// work, so one bad row cannot stall the rest.
/// </summary>
public sealed class LoanSweeper(IServiceScopeFactory scopes, IHubContext<LiveHub> hub, ISettings settings,
    ILogger<LoanSweeper> logger) : BackgroundService
{
    static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    const int LoanTicks = 3; // 15 s

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        for (var n = 0; await WaitAsync(timer, stoppingToken); n++)
        {
            await RunAsync("kiosk sessions", (db, o, now, ct) => KioskSessions.ExpireStaleAsync(db, o, now, ct), stoppingToken);
            await RunAsync("station health", StationsAsync, stoppingToken);
            if (n % LoanTicks != 0) continue;
            await RunAsync("transit timeouts", TransitAsync, stoppingToken);
            await RunAsync("overdue loans", OverdueAsync, stoppingToken);
            await RunAsync("event start", (db, _, now, ct) => db.Events
                .Where(e => e.Status == EventStatus.Scheduled && e.StartsAt <= now && e.EndsAt > now)
                .ExecuteUpdateAsync(u => u.SetProperty(e => e.Status, EventStatus.Active).SetProperty(e => e.UpdatedAt, now), ct), stoppingToken);
        }
    }

    async Task RunAsync(string step, Func<TamsDbContext, Outbox, DateTimeOffset, CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var outbox = new Outbox();
            await work(scope.ServiceProvider.GetRequiredService<TamsDbContext>(), outbox, DateTimeOffset.UtcNow, ct);
            await outbox.FlushAsync(hub);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Sweep step '{Step}' failed; retrying next tick", step);
        }
    }

    async Task TransitAsync(TamsDbContext db, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        var ids = await db.Loans.AsNoTracking()
            .Where(l => (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue)
                && l.CustodyState == CustodyState.AwaitingArrival && l.TransitDeadlineAt <= now)
            .Select(l => l.Id).ToListAsync(ct);
        foreach (var id in ids) await EachAsync(db, id, () => LoanRules.TransitTimeoutAsync(db, settings, outbox, id, now, ct));
    }

    async Task OverdueAsync(TamsDbContext db, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        var ids = await db.Loans.AsNoTracking().Where(l => l.Status == LoanStatus.Active && l.DueAt <= now)
            .Select(l => l.Id).ToListAsync(ct);
        foreach (var id in ids) await EachAsync(db, id, () => LoanRules.OverdueAsync(db, settings, outbox, id, now, ct));
    }

    async Task EachAsync(TamsDbContext db, Guid loanId, Func<Task<bool>> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sweep failed for loan {LoanId}; it is retried next tick", loanId);
            db.ChangeTracker.Clear();
        }
    }

    async Task StationsAsync(TamsDbContext db, Outbox outbox, DateTimeOffset now, CancellationToken ct)
    {
        var interval = await settings.GetIntAsync(SettingKeys.HeartbeatInterval, ct);
        var warning = await settings.GetIntAsync(SettingKeys.WarningWindows, ct);
        var offline = await settings.GetIntAsync(SettingKeys.OfflineWindows, ct);
        var stations = await db.Stations.AsNoTracking().Where(s => s.DeletedAt == null && s.Status == RecordStatus.Active).ToListAsync(ct);
        var wentOffline = new List<Station>();
        foreach (var s in stations)
        {
            var health = StationHealthRules.Compute(s.LastHeartbeatAt, now, interval, warning, offline);
            if (health == s.Health) continue;
            // Conditional so a heartbeat that landed since the read is not overwritten.
            var changed = await db.Stations.Where(x => x.Id == s.Id && x.Health == s.Health && x.LastHeartbeatAt == s.LastHeartbeatAt)
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Health, health), ct);
            if (changed == 0) continue;
            outbox.Staff("station.healthChanged", new
            {
                stationId = s.Id, s.StationCode, health = Support.Label(health), previous = Support.Label(s.Health),
            });
            if (health == StationHealth.Offline && s.LastHeartbeatAt is not null) wentOffline.Add(s);
        }
        if (wentOffline.Count == 0) return;

        // FR-8.3: technical personnel are told when a station that was reporting goes quiet.
        var techs = await db.Users.AsNoTracking()
            .Where(u => u.Role == UserRole.TechStaff && u.Status == RecordStatus.Active && u.DeletedAt == null)
            .Select(u => u.Id).ToListAsync(ct);
        foreach (var s in wentOffline)
            foreach (var t in techs)
                db.Notifications.Add(new Notification
                {
                    Id = Guid.NewGuid(), RecipientUserId = t, Type = "STATION_OFFLINE", Title = "Station offline",
                    Body = $"{s.Name} ({s.StationCode}) has missed its heartbeats since {LocalTime.Clock(s.LastHeartbeatAt!.Value)}.",
                    Payload = LoanRules.Json(new { stationId = s.Id, s.StationCode }), CreatedAt = now,
                });
        await db.SaveChangesAsync(ct);
    }

    static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
