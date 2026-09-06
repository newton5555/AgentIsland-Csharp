using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentIsland.Core;
using AgentIsland.Core.Agents;
using AgentIsland.Core.Storage;
using AgentIsland.Core.Threading;
using AgentIsland.Core.Usage;
using AgentIsland.Providers.BuiltIn;
using AgentIsland.UI;
using AgentIsland.UI.Threading;
using AgentIsland.Windows.Storage;
using AgentIsland.Backend.Monitoring;
using AgentIsland.Backend.Usage;
using AgentIsland.Backend.Cost;
using AgentIsland.Backend.Alarms;
using AgentIsland.Backend.Settings;
using AgentIsland.Backend.Updates;
using AgentIsland.Backend.Providers;
using AgentIsland.UI.ViewModels;

namespace AgentIsland;

public partial class App : System.Windows.Application
{
    public static App Instance => (App)Current;

    public IHost Host { get; }
    public IServiceProvider Services => Host.Services;

    public IAgentCatalog AgentCatalog => Services.GetRequiredService<IAgentCatalog>();
    public AgentIsland.Windows.Paths.IAppPaths AppPaths => Services.GetRequiredService<AgentIsland.Windows.Paths.IAppPaths>();
    public ISettingsStorage SettingsStorage => Services.GetRequiredService<ISettingsStorage>();
    public IUiDispatcher UiDispatcher => Services.GetRequiredService<IUiDispatcher>();
    public IProviderVisibilityStore ProviderVisibility => Services.GetRequiredService<IProviderVisibilityStore>();
    public IUsageStore Usage => Services.GetRequiredService<IUsageStore>();
    public ICostStore Cost => Services.GetRequiredService<ICostStore>();
    public IActivityMonitor Activity => Services.GetRequiredService<IActivityMonitor>();
    public IIslandModel IslandModel => Services.GetRequiredService<IIslandModel>();
    public IUpdateChecker Updates => Services.GetRequiredService<IUpdateChecker>();

    public App()
    {
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IAgentCatalog>(CreateDefaultCatalog());
                services.AddSingleton<AgentIsland.Windows.Paths.IAppPaths, AgentIsland.Windows.Paths.WindowsAppPaths>();
                services.AddSingleton<ISettingsStorage>(AtomicJsonSettingsStorage.Default);
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
                services.AddHttpClient();

                services.AddAgentProviders();

                services.AddSingleton<IProviderVisibilityStore, ProviderVisibilityStore>();
                services.AddSingleton<IUsageStore, UsageStore>();
                services.AddSingleton<ICostStore, CostStore>();
                services.AddSingleton<IActivityMonitor, ActivityMonitor>();
                services.AddSingleton<IIslandModel, IslandModel>();
                services.AddSingleton<IUpdateChecker, UpdateChecker>();

                services.AddSingleton(GrokUsageStore.Shared);
                services.AddSingleton(CursorUsageStore.Shared);
                services.AddSingleton(AntigravityUsageStore.Shared);
                services.AddSingleton(DeepSeekBalanceStore.Shared);
                services.AddSingleton(UsageExhaustionAlarm.Shared);
                services.AddSingleton(AlertEngine.Shared);

                services.AddTransient<IslandViewModel>();
                services.AddTransient<UsagePageViewModel>();
                services.AddTransient<CostPageViewModel>();
                services.AddTransient<SettingsViewModel>();

                services.AddHostedService<Backend.Host.AgentIslandHostedService>();
                services.AddHostedService<Backend.Workers.ActivityMonitoringWorker>();
                services.AddHostedService<Backend.Workers.UsagePollingWorker>();
                services.AddHostedService<Backend.Workers.CostAggregationWorker>();
                services.AddHostedService<Backend.Workers.UpdateCheckWorker>();
            })
            .Build();
    }

    private static IAgentCatalog CreateDefaultCatalog()
    {
        var catalog = new BuiltInAgentCatalog();
        var providers = new IAgentProvider[]
        {
            new ClaudeAgentProvider(),
            new CodexAgentProvider(),
            new AntigravityAgentProvider(),
            new DeepSeekAgentProvider(),
            new GrokAgentProvider(),
            new CursorAgentProvider(),
        };

        foreach (var provider in providers)
        {
            catalog.Register(new BuiltInAgentModule(
                provider.Descriptor,
                SessionSensor: provider.SessionSensor,
                UsageFetcher: provider.UsageFetcher,
                CostLedgerReader: provider.CostLedgerReader,
                SessionLauncher: provider.SessionLauncher,
                ReauthHandler: provider.ReauthHandler
            ));
        }
        return catalog;
    }

    private IslandWindow? _island;
    private TrayIcon? _tray;
    private System.Threading.Mutex? _singleInstance;

    /// Two live copies sharing %APPDATA%\AgentIsland corrupt each other's
    /// preferences (each periodic save resurrects that instance's stale
    /// snapshot), so a second launch bows out. The wait is generous because
    /// the auto-updater's relaunch overlaps the old process by design —
    /// the new exe must outwait the old one's exit, not give up. Demo/debug
    /// copies skip the gate: running one beside the real app is a supported
    /// verification flow.
    private bool ClaimSingleInstance()
    {
        if (AppEnvironment.Current != AppMode.Normal) return true;
        // One-shot headless card renders run beside the live instance and
        // exit on their own; preference writes merge (P15), so this is safe.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_REPORT_SNAPSHOT") is not null
            || Environment.GetEnvironmentVariable("AGENTISLAND_MONTHLY_SNAPSHOT") is not null)
        {
            return true;
        }
        _singleInstance = new System.Threading.Mutex(
            initiallyOwned: false, @"Local\AgentIsland.SingleInstance");
        try
        {
            return _singleInstance.WaitOne(TimeSpan.FromSeconds(10));
        }
        catch (System.Threading.AbandonedMutexException)
        {
            return true; // previous holder died without releasing — ours now
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!ClaimSingleInstance())
        {
            Shutdown();
            return;
        }
        InstallCrashLogger();

        try
        {
            Host.StartAsync().GetAwaiter().GetResult();
            Http.Configure(Services.GetRequiredService<System.Net.Http.IHttpClientFactory>());
        }
        catch { }

        ActivityMonitor.Shared.Configure(AgentCatalog);
        // Before any store singleton reads a key: settings written by pre-1.7
        // builds carry the MacIsland.* prefix and must land on AgentIsland.*.
        AgentIsland.Windows.Preferences.MigrateLegacyPrefix();
        AgentIsland.Backend.Settings.AppLanguageStore.ApplyAtStartup();

        if (AppEnvironment.IsDemo)
        {
            ActivityMonitor.Shared.Demo(ActivityState.Working);
        }

        _island = new IslandWindow();
        _island.Show();

        _tray = new TrayIcon(
            showIsland: () => _island?.PopUp(),
            toggleIsland: () =>
            {
                if (_island is null) return;
                if (_island.IsVisible)
                {
                    _island.DeliberatelyHidden = true;
                    _island.Hide();
                }
                else
                {
                    _island.DeliberatelyHidden = false;
                    _island.Show();
                }
            },
            openSettings: UI.SettingsWindow.Open,
            exit: () =>
            {
                _tray?.Dispose();
                Shutdown();
            });
        TrayIcon.Current = _tray;

        AgentIsland.Backend.Alarms.UsageExhaustionAlarm.Shared.Start();
        AgentIsland.Backend.Updates.UpdateInstaller.CleanupAtStartup();
        AgentIsland.Backend.Settings.AlertEngine.Shared.Start();

        // Release card: once per version, shortly after the island lands

        // Weekly report moment: once per ISO week, surface the card shortly
        // after launch. Suppressed for demo/debug/snapshot runs.
        var reportSnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_REPORT_SNAPSHOT");
        var monthlySnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_MONTHLY_SNAPSHOT");
        if (AppEnvironment.Current == AppMode.Normal
            && reportSnapshot is null && monthlySnapshot is null)
        {
            UI.Report.ReportWindow.ArmWeeklyMoment();
        }

        // Headless card renders for tooling/screenshots, mirroring macOS.
        // Data-driven, not a fixed delay: a cache-version bump forces a full
        // rescan that can take well past any polite sleep (a 5s beat wrote
        // all-zero cards the day the Codex cache went v2). Render on the
        // first completed scan; a 90s failsafe keeps a wedged scan from
        // leaving the process running forever.
        if (!string.IsNullOrEmpty(reportSnapshot) || !string.IsNullOrEmpty(monthlySnapshot))
        {
            var rendered = false;
            void RenderAndQuit()
            {
                if (rendered) return;
                rendered = true;
                if (!string.IsNullOrEmpty(reportSnapshot))
                    UI.Report.ReportWindow.WritePng(UI.Report.ReportWindow.Kind.Weekly, reportSnapshot!);
                if (!string.IsNullOrEmpty(monthlySnapshot))
                    UI.Report.ReportWindow.WritePng(UI.Report.ReportWindow.Kind.Monthly, monthlySnapshot!);
                Shutdown();
            }
            if (AgentIsland.Backend.Cost.CostStore.Shared.LastUpdated is not null)
            {
                RenderAndQuit();
            }
            else
            {
                AgentIsland.Backend.Cost.CostStore.Shared.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(AgentIsland.Backend.Cost.CostStore.LastUpdated))
                    {
                        Dispatcher.BeginInvoke(RenderAndQuit);
                    }
                };
                var failsafe = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(90),
                };
                failsafe.Tick += (_, _) => { failsafe.Stop(); RenderAndQuit(); };
                failsafe.Start();
            }
        }

        // Scripted-verification hooks, mirroring the demo-only buttons on
        // macOS: never set in normal use.
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_OPEN_SETTINGS") == "1")
        {
            UI.SettingsWindow.Open();
        }
        // Render the island's live glow/sweep to a PNG for parity checks —
        // AGENTISLAND_DEBUG_ISLAND_STATE (working/stalled/needsYou/…) forces
        // the state first. Immune to the window occlusion a screen grab hits.
        var islandPng = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_ISLAND_PNG");
        if (!string.IsNullOrEmpty(islandPng))
        {
            var stateName = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_ISLAND_STATE");
            if (Enum.TryParse<ActivityState>(stateName, ignoreCase: true, out var forced))
            {
                ActivityMonitor.Shared.Demo(forced);
            }
            // _EXPANDED=1 renders the open panel (header chip, tiles, footer)
            // instead of the compact bar; _SCREEN picks the carousel page
            // without persisting it over the user's parked choice.
            if (Enum.TryParse<UI.IslandScreen>(
                    Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_ISLAND_SCREEN"),
                    ignoreCase: true, out var screen))
            {
                UI.ScreenPref.Shared.ForceForVerification(screen);
            }
            if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_ISLAND_EXPANDED") == "1")
            {
                _island?.PopUp();
            }
            _island?.SaveVisualSnapshot(islandPng);
        }
        // "1" pops the Sparkle-style up-to-date card; any other value is a
        // PNG path the card renders itself into (works across virtual
        // desktops, where a screen grab can't see it).
        var updateDialogPreview = Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_UPDATE_DIALOG");
        if (!string.IsNullOrEmpty(updateDialogPreview))
        {
            var dialog = IslandDialog.ShowUpdate(
                AgentIsland.UI.Localization.L10n.Tr("You're up to date!"),
                AgentIsland.UI.Localization.L10n.TrFormat(
                    "AgentIsland {0} is currently the newest version available.",
                    AgentIsland.Backend.Updates.UpdateChecker.CurrentVersionDisplay),
                primaryLabel: AgentIsland.UI.Localization.L10n.Tr("OK"),
                secondaryLabel: AgentIsland.UI.Localization.L10n.Tr("Version History"));
            if (updateDialogPreview != "1") dialog.SaveSnapshot(updateDialogPreview);
        }
        // Full-surface CI sweep: renders reports + island + every settings
        // tab into the directory, then exits (see UI.SnapshotSweep).
        var snapshotDir = Environment.GetEnvironmentVariable("AGENTISLAND_SNAPSHOT_DIR");
        if (!string.IsNullOrEmpty(snapshotDir) && _island is not null)
        {
            UI.SnapshotSweep.Run(this, _island, snapshotDir!);
        }
        if (Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_DIALOG") == "1")
        {
            IslandDialog.Show(
                TriggerTool.Claude,
                AgentIsland.UI.Localization.L10n.Tr("Re-authenticate"),
                AgentIsland.UI.Localization.L10n.Tr("Claude Code CLI not found. Log in from a terminal with: claude /login"),
                meta: new[]
                {
                    (AgentIsland.UI.Localization.L10n.Tr("Alarm provider"), "Claude"),
                    (AgentIsland.UI.Localization.L10n.Tr("Alarm thread"), "Agent Island Windows"),
                    (AgentIsland.UI.Localization.L10n.Tr("Alarm project"), "Agent Island"),
                },
                primaryLabel: AgentIsland.UI.Localization.L10n.Tr("Retry"),
                secondaryLabel: AgentIsland.UI.Localization.L10n.Tr("I know"));
        }
    }

    /// Rebuild the island (and its expanded chrome / tray menu) so a language
    /// change lands everywhere immediately — the stores and monitors keep
    /// running untouched, only the labels are re-created in the new language.
    public void RebuildForLanguageChange()
    {
        var wasVisible = _island?.IsVisible ?? true;
        _island?.Close();
        _island = new IslandWindow();
        if (wasVisible) _island.Show();

        _tray?.Dispose();
        _tray = new TrayIcon(
            showIsland: () => _island?.PopUp(),
            toggleIsland: () =>
            {
                if (_island is null) return;
                if (_island.IsVisible) _island.Hide(); else _island.Show();
            },
            openSettings: UI.SettingsWindow.Open,
            exit: () =>
            {
                _tray?.Dispose();
                Shutdown();
            });
        TrayIcon.Current = _tray;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        try
        {
            Host.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Host.Dispose();
        }
        catch { }
        base.OnExit(e);
    }

    private static void InstallCrashLogger()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash(args.ExceptionObject as Exception, "AppDomain");
        Current.DispatcherUnhandledException += (_, args) =>
            LogCrash(args.Exception, "Dispatcher");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash(args.Exception, "Task");
            args.SetObserved();
        };
    }

    private static void LogCrash(Exception? error, string source)
    {
        try
        {
            System.IO.Directory.CreateDirectory(AgentIsland.Windows.IslandPaths.AppSupportDir);
            var path = System.IO.Path.Combine(AgentIsland.Windows.IslandPaths.AppSupportDir, "crash.log");
            System.IO.File.AppendAllText(
                path,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {source}: {error}\n\n");
        }
        catch
        {
        }
    }
}
