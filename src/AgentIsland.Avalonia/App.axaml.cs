using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AgentIsland.Avalonia.Binding;
using AgentIsland.Avalonia.ViewModels;
using AgentIsland.Runtime.Refresh;
using AgentIsland.Runtime.Sources;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace AgentIsland.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            var includeOnlineBalance = args.Any(a => string.Equals(a, "--live-online", StringComparison.OrdinalIgnoreCase));
            var isLive = includeOnlineBalance
                || args.Any(a => string.Equals(a, "--live", StringComparison.OrdinalIgnoreCase));
            if (isLive)
            {
                var viewModel = new IslandViewModel();
                var sources = LocalTokenSources.CreateSources(
                    includeDeepSeekBalance: includeOnlineBalance);
                var runtime = new AgentRuntime(sources);
                var binder = new RuntimeIslandBinder(viewModel, runtime);
                binder.Attach();

                var cts = new CancellationTokenSource();
                var liveLoopTask = Task.Run(async () =>
                {
                    try
                    {
                        await runtime.RunAsync(TimeSpan.FromSeconds(15), cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Clean exit on app shutdown
                    }
                    catch (Exception ex)
                    {
                        Trace.WriteLine($"AgentRuntime live loop error: {ex}");
                    }
                });

                var mainWindow = new MainWindow(viewModel);
                var cleanedUp = 0;
                void Cleanup()
                {
                    if (Interlocked.Exchange(ref cleanedUp, 1) != 0) return;
                    try
                    {
                        cts.Cancel();
                        binder.Dispose();
                    }
                    catch
                    {
                    }

                    _ = liveLoopTask.ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            Trace.WriteLine($"AgentRuntime live loop task faulted: {t.Exception}");
                        }

                        try
                        {
                            cts.Dispose();
                        }
                        catch
                        {
                        }
                    }, TaskScheduler.Default);
                }

                desktop.Exit += (_, _) => Cleanup();
                mainWindow.Closed += (_, _) => Cleanup();
                desktop.MainWindow = mainWindow;
            }
            else
            {
                desktop.MainWindow = new MainWindow(IslandViewModel.CreateMock());
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
