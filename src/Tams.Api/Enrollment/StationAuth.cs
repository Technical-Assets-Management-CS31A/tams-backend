using Tams.Api.Data;
using Tams.Api.Data.Entities;

namespace Tams.Api.Enrollment;

/// <summary>
/// The one place that decides whether a kiosk request really comes from the station it names.
/// PRD 6.2/10.2: kiosks hold no credentials; the defence is station_code plus, optionally, an IP
/// allowlist (per-device keys are listed as future work). Swap the body when device keys or a
/// station auth scheme land and every kiosk route here follows.
///
/// 1. The request must carry <c>X-Station-Code</c> equal to the session's station_code (always).
/// 2. With <c>Kiosk:EnforceIp=true</c> the caller's IP must also be stations.ip_address or one of the
///    comma-separated <c>Kiosk:AllowedIps</c>. Off by default: behind Caddy the address is only
///    trustworthy because Program.cs applies X-Forwarded-For, and kiosk addresses may be DHCP.
/// </summary>
public static class StationAuth
{
    public const string StationCodeHeader = "X-Station-Code";

    public static bool IsStationAuthenticated(HttpContext http, Station station)
    {
        if (station.Type != StationType.CounterKiosk) return false;

        var presented = http.Request.Headers[StationCodeHeader].ToString().Trim();
        if (presented.Length == 0
            || !string.Equals(presented, station.StationCode, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsDeviceAllowed(http, station);
    }

    /// <summary>
    /// Room-station scans and heartbeats name the station in the path (/stations/{code}/...), so only rule 2
    /// applies on top of the station being live and active.
    /// </summary>
    public static bool IsDeviceAllowed(HttpContext http, Station station)
    {
        if (station.DeletedAt is not null || station.Status != RecordStatus.Active) return false;

        var config = http.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetValue<bool>("Kiosk:EnforceIp")) return true;

        var remote = http.Connection.RemoteIpAddress;
        var remoteText = remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
        if (remoteText is null) return false;

        var allowed = (config["Kiosk:AllowedIps"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(station.IpAddress ?? "")
            .Where(ip => ip.Length > 0);
        return allowed.Any(ip => string.Equals(ip, remoteText, StringComparison.OrdinalIgnoreCase));
    }
}
