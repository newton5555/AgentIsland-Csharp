namespace AgentIsland.Core.Threading;

/// <summary>
/// Direct UI dispatcher executing actions synchronously on the calling thread.
/// Suitable for unit tests and headless environments.
/// </summary>
public sealed class DirectUiDispatcher : IUiDispatcher
{
    public static DirectUiDispatcher Instance { get; } = new();

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
        return Task.CompletedTask;
    }

    public void BeginInvoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public bool CheckAccess() => true;
}
