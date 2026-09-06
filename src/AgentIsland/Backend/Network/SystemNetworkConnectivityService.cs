using System.Net.NetworkInformation;
using AgentIsland.Core.Network;

namespace AgentIsland.Backend.Network;

/// <summary>
/// Monitors system network state using NetworkChange.NetworkAvailabilityChanged and NetworkInterface.
/// </summary>
public sealed class SystemNetworkConnectivityService : INetworkConnectivityService, IDisposable
{
    private bool _lastAvailable;
    private bool _disposed;

    public bool IsNetworkAvailable => NetworkInterface.GetIsNetworkAvailable();

    public event EventHandler<bool>? NetworkAvailabilityChanged;

    public SystemNetworkConnectivityService()
    {
        _lastAvailable = IsNetworkAvailable;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (_disposed) return;
        var current = e.IsAvailable;
        if (_lastAvailable == current) return;
        _lastAvailable = current;
        NetworkAvailabilityChanged?.Invoke(this, current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
