namespace AgentIsland.Alarm;

/// Which rate-limit window a quota-exhausted alarm is about.
public enum QuotaWindowKind
{
    FiveHour,
    Weekly,
}

/// Why the full-screen alarm is showing. YourTurn is the thread-finished
/// alarm; QuotaExhausted is the distinct "you're out of quota until <time>"
/// alarm, which has no thread to open — only an acknowledge. Mirrors the
/// macOS TurnAlarmKind enum (TurnAlarmView.swift).
public abstract record TurnAlarmKind
{
    public sealed record YourTurn : TurnAlarmKind;

    public sealed record QuotaExhausted(QuotaWindowKind Window, DateTimeOffset? ResetAt) : TurnAlarmKind;

    /// The macOS rawValue spelling, used inside alarm/dedup keys so the two
    /// platforms produce identical key shapes.
    public static string RawValue(QuotaWindowKind window) =>
        window == QuotaWindowKind.FiveHour ? "fiveHour" : "weekly";
}
