using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Tams.Api.Common;
using Tams.Api.Data;
using Tams.Api.Data.Entities;
using Tams.Api.Enrollment;
using Tams.Api.Facilities;
using Tams.Api.Http;
using Tams.Api.Live;
using Tams.Api.Loans;
using Tams.Api.Settings;

namespace Tams.Api.Kiosk;

public sealed record ScanRequest(string? TagUid);

/// <summary>FR-8.1. The IP stored is the one the API sees (what the optional allowlist checks), not a reported one.</summary>
public sealed record HeartbeatRequest(string? FirmwareVersion, short? Rssi, int? UptimeS, int? FreeHeap);

/// <summary>
/// F3 room check-in and F8 heartbeats. Device traffic: anonymous to the JWT pipeline, the station is the
/// one named in the path (StationAuth.IsDeviceAllowed). Allow-listed in Policies.AnonymousRoutes.
/// </summary>
public static class DeviceEndpoints
{
    public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/stations/{code}").WithTags("Stations (devices)").AllowAnonymous();
        g.MapPost("/scans", Scan);
        g.MapPost("/heartbeat", Heartbeat);
        return app;
    }

    /// <summary>
    /// The F3 table. Every scan is stored (FR-3.3); the response carries the result code the station maps
    /// to its LED and buzzer (FR-3.2, PRD 6.8): signal SUCCESS = green + chime, ERROR = red strobe + low tone.
    /// </summary>
    static async Task<IResult> Scan(string code, ScanRequest req, HttpContext http, TamsDbContext db, ISettings settings,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var station = await StationAsync(db, code, ct);
        if (station is null || station.Type != StationType.RoomStation || !StationAuth.IsDeviceAllowed(http, station))
            return Problems.Forbidden("STATION_NOT_AUTHENTICATED", "Unknown or inactive room station.");
        if (!RfidUid.TryNormalize(req.TagUid, out var uid))
            return Problems.Validation(new Dictionary<string, string[]> { ["tagUid"] = ["Required: the tag UID in hex."] });

        var now = DateTimeOffset.UtcNow;
        var outbox = new Outbox();
        var asset = await db.Assets.AsNoTracking().FirstOrDefaultAsync(a => a.RfidTagUid == uid && a.DeletedAt == null, ct);
        var loan = asset is null ? null : await OpenLoanAsync(db, asset.Id, ct);

        // A late tap that beat the sweeper: time the loan out first so its warning is issued (PRD 4.2).
        if (loan is { CustodyState: CustodyState.AwaitingArrival, TransitDeadlineAt: { } deadline } && deadline <= now)
        {
            await LoanRules.TransitTimeoutAsync(db, settings, outbox, loan.Id, now, ct);
            loan = await OpenLoanAsync(db, asset!.Id, ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        ScanResult result;
        if (asset is null) result = ScanResult.UnknownTag;
        else if (loan is null)
        {
            result = ScanResult.NotBorrowed;
            await RaiseAsync(db, outbox, AnomalyType.UnborrowedTap, asset, null, station, now, ct);
        }
        else if (loan.DestinationRoomId != station.RoomId)
        {
            // FR-3.4: only a staff room change (PUT /loans/{id}/room) lets the item check in elsewhere.
            result = ScanResult.WrongRoom;
            await RaiseAsync(db, outbox, AnomalyType.WrongRoom, asset, loan, station, now, ct);
        }
        else if (loan.CustodyState == CustodyState.Verified) result = ScanResult.Verified; // re-tap in the right room
        else
        {
            var late = loan.CustodyState == CustodyState.Anomaly;
            var claimed = await db.Loans.Where(l => l.Id == loan.Id && l.CustodyState == loan.CustodyState)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(l => l.CustodyState, CustodyState.Verified)
                    .SetProperty(l => l.VerifiedAt, now)
                    .SetProperty(l => l.VerifiedStationId, station.Id), ct);
            result = late ? ScanResult.VerifiedLate : ScanResult.Verified;
            if (claimed > 0)
            {
                var tracked = await db.Assets.FirstAsync(a => a.Id == asset.Id, ct);
                var from = tracked.Status;
                tracked.Status = loan.Status == LoanStatus.Overdue ? AssetStatus.Overdue : AssetStatus.InRoomVerified;
                tracked.CurrentRoomId = station.RoomId;
                tracked.UpdatedAt = now;
                if (late)
                {
                    // The anomaly is marked resolved; the Late Arrival Warning already issued stays.
                    var open = await db.Anomalies.Where(a => a.LoanId == loan.Id && a.Type == AnomalyType.TransitTimeout
                        && a.Status != AnomalyStatus.Resolved).ToListAsync(ct);
                    foreach (var a in open)
                    {
                        a.Status = AnomalyStatus.Resolved;
                        a.ResolvedAt = now;
                        a.ResolutionNote = "Late arrival, resolved";
                        outbox.Staff("anomaly.resolved", LoanRules.AnomalyEvent(a, asset.AssetCode, loan.LoanNo));
                        outbox.User(loan.ApprovedByUserId ?? loan.CoordinatorUserId, "anomaly.resolved", LoanRules.AnomalyEvent(a, asset.AssetCode, loan.LoanNo));
                    }
                }
                LoanRules.Ledger(db, LedgerEvent.RoomVerified, loan, now, station.Id, details: new { late, roomId = station.RoomId });
                outbox.Staff("asset.statusChanged", LoanRules.StatusChanged(asset.Id, from, tracked.Status, station.RoomId));
            }
        }

        db.RoomScans.Add(new RoomScan
        {
            StationId = station.Id, RoomId = station.RoomId, TagUid = uid, AssetId = asset?.Id, LoanId = loan?.Id,
            Result = result, ScannedAt = now,
        });
        await db.SaveChangesAsync(ct);
        if (result is ScanResult.Verified or ScanResult.VerifiedLate) await LoanRules.OccupancyChangedAsync(db, outbox, station.RoomId, now, ct);
        await tx.CommitAsync(ct);
        await outbox.FlushAsync(hub);

        return Results.Ok(new
        {
            result = Support.Label(result),
            signal = result is ScanResult.Verified or ScanResult.VerifiedLate ? "SUCCESS" : "ERROR",
            assetCode = asset?.AssetCode, loanNo = loan?.LoanNo,
        });
    }

    /// <summary>One open anomaly per (type, asset, room): repeated wrong taps do not flood the queue.</summary>
    static async Task RaiseAsync(TamsDbContext db, Outbox outbox, AnomalyType type, Asset asset, Loan? loan, Station station,
        DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Anomalies.AnyAsync(a => a.Type == type && a.AssetId == asset.Id && a.RoomId == station.RoomId
                && a.Status != AnomalyStatus.Resolved, ct))
            return;
        var anomaly = new Anomaly
        {
            Id = Guid.NewGuid(), Type = type, AssetId = asset.Id, LoanId = loan?.Id, RoomId = station.RoomId, StationId = station.Id,
            DetectedAt = now,
        };
        db.Anomalies.Add(anomaly);
        var evt = LoanRules.AnomalyEvent(anomaly, asset.AssetCode, loan?.LoanNo);
        outbox.Staff("anomaly.raised", evt);
        outbox.User(loan?.ApprovedByUserId ?? loan?.CoordinatorUserId, "anomaly.raised", evt);
    }

    static async Task<IResult> Heartbeat(string code, HeartbeatRequest req, HttpContext http, TamsDbContext db, ISettings settings,
        IHubContext<LiveHub> hub, CancellationToken ct)
    {
        var station = await db.Stations.FirstOrDefaultAsync(s => s.StationCode == code.Trim().ToUpperInvariant() && s.DeletedAt == null, ct);
        if (station is null || !StationAuth.IsDeviceAllowed(http, station))
            return Problems.Forbidden("STATION_NOT_AUTHENTICATED", "Unknown or inactive station.");
        var errors = new Dictionary<string, string[]>();
        if (req.FirmwareVersion is { Length: > 30 }) errors["firmwareVersion"] = ["Up to 30 characters."];
        if (req.UptimeS is < 0) errors["uptimeS"] = ["Must be 0 or more."];
        if (req.FreeHeap is < 0) errors["freeHeap"] = ["Must be 0 or more."];
        if (errors.Count > 0) return Problems.Validation(errors);

        var now = DateTimeOffset.UtcNow;
        var remote = http.Connection.RemoteIpAddress;
        db.StationHeartbeats.Add(new StationHeartbeat
        {
            StationId = station.Id, ReceivedAt = now, Rssi = req.Rssi ?? 0, UptimeS = req.UptimeS ?? 0, FreeHeap = req.FreeHeap ?? 0,
            FirmwareVersion = req.FirmwareVersion,
        });
        var was = station.Health;
        station.LastHeartbeatAt = now;
        station.Health = StationHealth.Online;
        station.FirmwareVersion = req.FirmwareVersion ?? station.FirmwareVersion;
        station.IpAddress = remote is null ? station.IpAddress : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        await db.SaveChangesAsync(ct);
        if (was != StationHealth.Online)
            await hub.Clients.Group(LiveHub.StaffGroup).SendAsync("station.healthChanged",
                new { stationId = station.Id, station.StationCode, health = Support.Label(StationHealth.Online), previous = Support.Label(was) }, ct);

        return Results.Ok(new { serverTime = now, heartbeatIntervalSeconds = await settings.GetIntAsync(SettingKeys.HeartbeatInterval, ct) });
    }

    static Task<Station?> StationAsync(TamsDbContext db, string code, CancellationToken ct)
    {
        var c = code.Trim().ToUpperInvariant();
        return db.Stations.AsNoTracking().FirstOrDefaultAsync(s => s.StationCode == c && s.DeletedAt == null, ct);
    }

    static Task<Loan?> OpenLoanAsync(TamsDbContext db, Guid assetId, CancellationToken ct) =>
        db.Loans.AsNoTracking()
            .FirstOrDefaultAsync(l => l.AssetId == assetId && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Overdue), ct);
}

/// <summary>FR-8.2 / PRD 4.1: Online, Warning after 1 missed window, Offline after 2 (both configurable).</summary>
public static class StationHealthRules
{
    // ponytail: calibration knob. A heartbeat that lands a little after its window (Wi-Fi retries, clock
    // drift on the ESP32) should not flash Warning; widen it if stations flicker.
    public const double SlackSeconds = 3;

    public static StationHealth Compute(DateTimeOffset? last, DateTimeOffset now, int intervalSeconds, int warningWindows, int offlineWindows)
    {
        if (last is null) return StationHealth.Offline;
        var missed = (int)Math.Floor(((now - last.Value).TotalSeconds - SlackSeconds) / intervalSeconds);
        return missed >= offlineWindows ? StationHealth.Offline : missed >= warningWindows ? StationHealth.Warning : StationHealth.Online;
    }
}
