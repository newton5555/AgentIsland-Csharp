namespace AgentIsland.Core.Network;

/// <summary>
/// Abstraction for network connectivity monitoring and offline status detection.
/// </summary>
public interface INetworkConnectivityService
{
    /// <summary>
    /// True if there is at least one active network interface with internet/LAN connectivity.
    /// </summary>
    bool IsNetworkAvailable { get; }

    /// <summary>
    /// Fired when network connectivity transitions between online and offline.
    /// </summary>
    event EventHandler<bool>? NetworkAvailabilityChanged;
}
