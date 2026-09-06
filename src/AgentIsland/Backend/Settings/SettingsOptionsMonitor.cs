using Microsoft.Extensions.Options;

namespace AgentIsland.Backend.Settings;

/// <summary>
/// Bridges application settings storage with Microsoft.Extensions.Options IOptionsMonitor&lt;T&gt;.
/// </summary>
public sealed class SettingsOptionsMonitor<T> : IOptionsMonitor<T>, IOptions<T>, IDisposable where T : class
{
    private readonly Func<T> _factory;
    private readonly List<Action<T, string?>> _listeners = new();
    private readonly object _lock = new();

    public SettingsOptionsMonitor(Func<T> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public T Value => CurrentValue;

    public T CurrentValue => _factory();

    public T Get(string? name) => CurrentValue;

    public IDisposable OnChange(Action<T, string?> listener)
    {
        lock (_lock)
        {
            _listeners.Add(listener);
        }

        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                _listeners.Remove(listener);
            }
        });
    }

    public void NotifyChanged()
    {
        List<Action<T, string?>> listeners;
        lock (_lock)
        {
            listeners = _listeners.ToList();
        }

        var current = CurrentValue;
        foreach (var listener in listeners)
        {
            try
            {
                listener(current, null);
            }
            catch
            {
                // Suppress listener exceptions to preserve event chain
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _listeners.Clear();
        }
    }

    private sealed class Unsubscriber : IDisposable
    {
        private Action? _action;
        public Unsubscriber(Action action) => _action = action;
        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
