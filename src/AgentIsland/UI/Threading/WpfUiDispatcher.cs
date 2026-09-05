using System.Windows.Threading;
using AgentIsland.Core.Threading;

namespace AgentIsland.UI.Threading;

/// <summary>
/// WPF implementation of IUiDispatcher that marshals execution to the WPF Dispatcher.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher? dispatcher = null)
    {
        _dispatcher = dispatcher 
            ?? System.Windows.Application.Current?.Dispatcher 
            ?? Dispatcher.CurrentDispatcher;
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            _dispatcher.Invoke(action);
        }
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return _dispatcher.InvokeAsync(action).Task;
    }

    public void BeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _dispatcher.BeginInvoke(action);
    }

    public bool CheckAccess() => _dispatcher.CheckAccess();
}
