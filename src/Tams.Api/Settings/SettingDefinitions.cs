namespace Tams.Api.Settings;

/// <summary>
/// One tunable business rule. Defaults live here, not in the database: system_settings
/// only holds admin overrides. Integer-only for now, every PRD 4.1 rule is a whole number.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    int Default,
    string Description,
    string Source,
    int? Min = null,
    int? Max = null,
    int[]? AllowedValues = null)
{
    public string? Validate(int value)
    {
        if (AllowedValues is not null)
            return AllowedValues.Contains(value) ? null : $"Must be one of {string.Join(", ", AllowedValues)}.";
        if (value < Min || value > Max)
            return $"Must be between {Min} and {Max}.";
        return null;
    }
}

public static class SettingDefinitions
{
    // Keys are the system_settings.key values (max 60 chars). Source is the PRD section.
    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        new("transit_window_minutes", 10, "Minutes after checkout in which the item must be tapped at its destination room.", "PRD 4.1", Min: 1, Max: 60),
        new("class_length_default_minutes", 90, "Class length pre-selected when an instructor approves a borrow. Loans allow 90 to 180.", "PRD 4.1", AllowedValues: [90, 120, 150, 180]),
        new("grace_minutes", 15, "Return grace period after class ends. Copied onto each loan at checkout.", "PRD 4.1, 7.3 loans", Min: 15, Max: 30),
        new("kiosk_poll_interval_seconds", 2, "How often the kiosk polls its session for the approval result.", "PRD 4.1", Min: 1, Max: 10),
        new("approval_timeout_seconds", 120, "Seconds the instructor has to approve before the session expires and the borrow is denied.", "PRD 4.1", Min: 30, Max: 600),
        new("kiosk_idle_timeout_seconds", 60, "Seconds of inactivity between kiosk steps before the session expires.", "PRD 4.1", Min: 15, Max: 300),
        new("heartbeat_interval_seconds", 30, "Expected interval between station heartbeats.", "PRD 4.1, FR-8.1", Min: 30, Max: 60),
        new("station_warning_missed_windows", 1, "Missed heartbeat windows before a station shows Warning.", "PRD 4.1", Min: 1, Max: 5),
        new("station_offline_missed_windows", 2, "Missed heartbeat windows before a station shows Offline. Must be at least the warning value.", "PRD 4.1, US-15", Min: 1, Max: 10),
        new("late_arrival_warning_limit", 3, "Late Arrival Warnings allowed per student; the next one triggers a mark and an instructor freeze.", "PRD 4.1, D6", Min: 0, Max: 10),
        new("enrollment_session_timeout_seconds", 120, "Seconds an enrollment session stays open at each step (fingerprint capture, then confirm) before it expires.", "PRD FR-9.4", Min: 15, Max: 3600),
        new("max_active_loans_per_student", 1, "Open loans a student may hold at once; the next checkout is refused at the fingerprint step.", "PRD FR-1.9", Min: 1, Max: 10),
        new("marks_before_block", 2, "Non-Compliance Marks after which a student is blocked from borrowing.", "PRD 4.1, FR-6.4", Min: 1, Max: 10),
    ];

    public static readonly IReadOnlyDictionary<string, SettingDefinition> ByKey =
        All.ToDictionary(d => d.Key);

    /// <summary>Cross-setting rules, checked against the effective values after a write.</summary>
    public static string? ValidateCombination(IReadOnlyDictionary<string, int> v) =>
        v["station_offline_missed_windows"] < v["station_warning_missed_windows"]
            ? "station_offline_missed_windows must be at least station_warning_missed_windows."
            : null;
}
