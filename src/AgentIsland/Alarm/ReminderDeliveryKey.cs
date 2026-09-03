namespace AgentIsland.Alarm;

/// Builds the dedup key for one finished turn of one thread. The transcript
/// path is the stable thread identity when available; a missing turn key
/// collapses to a stable "latest" marker so metadata-only writes can't mint
/// new reminder keys.
public static class ReminderDeliveryKey
{
    public static string Make(
        string providerRawValue,
        int stateRawValue,
        string? transcriptPath,
        string sessionId,
        string cwd,
        string label,
        string? turnKey)
    {
        var thread = ThreadKey(transcriptPath, sessionId, cwd, label);
        var turn = string.IsNullOrEmpty(turnKey) ? "latest" : turnKey;
        return $"{providerRawValue}-{stateRawValue}-{thread}-{turn}";
    }

    public static string ThreadKey(string? transcriptPath, string sessionId, string cwd, string label)
    {
        if (!string.IsNullOrEmpty(transcriptPath)) return transcriptPath!;
        if (!string.IsNullOrEmpty(sessionId)) return sessionId;
        if (!string.IsNullOrEmpty(cwd)) return $"{cwd}:{label}";
        return label;
    }
}
