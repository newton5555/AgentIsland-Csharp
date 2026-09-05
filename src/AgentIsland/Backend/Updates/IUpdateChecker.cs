namespace AgentIsland.Backend.Updates;

/// <summary>
/// Contract for release feed polling and version updates.
/// </summary>
public interface IUpdateChecker
{
    Version Version { get; }
    void Start();
    Task CheckAsync(bool userInitiated = false);
}
