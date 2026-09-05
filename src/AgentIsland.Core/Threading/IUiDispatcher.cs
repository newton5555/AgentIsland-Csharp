namespace AgentIsland.Core.Threading;

/// <summary>
/// UI Dispatcher abstraction to decouple business coordinators, background workers,
/// and domain stores from direct WPF/UI thread dependencies.
/// </summary>
public interface IUiDispatcher
{
    void Invoke(Action action);
    Task InvokeAsync(Action action);
    void BeginInvoke(Action action);
    bool CheckAccess();
}
