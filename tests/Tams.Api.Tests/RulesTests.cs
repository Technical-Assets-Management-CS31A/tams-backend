using Tams.Api.Data;
using Tams.Api.Kiosk;
using Tams.Api.Loans;

namespace Tams.Api.Tests;

public class RulesTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 7, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null, StationHealth.Offline)]   // never reported
    [InlineData(20.0, StationHealth.Online)]
    [InlineData(32.0, StationHealth.Online)]    // inside the slack after the 30 s window
    [InlineData(34.0, StationHealth.Warning)]   // one missed window (PRD 4.1)
    [InlineData(64.0, StationHealth.Offline)]   // two missed windows
    public void Station_health_follows_missed_heartbeat_windows(double? secondsSinceLast, StationHealth expected)
    {
        DateTimeOffset? last = secondsSinceLast is { } s ? T0.AddSeconds(-s) : null;
        Assert.Equal(expected, StationHealthRules.Compute(last, T0, intervalSeconds: 30, warningWindows: 1, offlineWindows: 2));
    }

    [Theory]
    [InlineData(2026, 11, 7, "2026-2027", "2026-2027-1")]
    [InlineData(2027, 2, 1, "2026-2027", "2026-2027-2")]
    [InlineData(2027, 6, 15, "2026-2027", "2026-2027-3")]
    [InlineData(2027, 8, 1, "2027-2028", "2027-2028-1")]
    public void School_term_comes_from_the_local_date(int y, int m, int d, string year, string term)
    {
        // Noon local time; Manila is UTC+8.
        var at = new DateTimeOffset(y, m, d, 4, 0, 0, TimeSpan.Zero);
        Assert.Equal((year, term), LocalTime.SchoolTerm(at));
    }

    [Fact]
    public void Lcd_due_time_is_local()
    {
        // 07:15 UTC is 3:15 PM in Manila, the PRD 6.7 example.
        Assert.Equal("3:15PM", LocalTime.Clock(new DateTimeOffset(2026, 11, 7, 7, 15, 0, TimeSpan.Zero)));
    }
}
