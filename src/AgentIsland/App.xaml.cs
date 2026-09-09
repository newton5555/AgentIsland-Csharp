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
using AgentIsland.Core.Navigation;
using AgentIsland.Core.Network;
using AgentIsland.Core.Options;
using AgentIsland.Core.Dialogs;
using AgentIsland.Backend.Network;
using AgentIsland.UI.Services;

namespace AgentIsland;

public partial class App : System.Windows.Application
{
    public static App? Instance => Current as App;

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
    public IWindowService WindowService => Services.GetRequiredService<IWindowService>();
    public IDialogService DialogService => Services.GetRequiredService<IDialogService>();

    public App()
    {
        Host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<IAgentCatalog>(CreateDefaultCatalog());
                services.AddSingleton<AgentIsland.Windows.Paths.IAppPaths, AgentIsland.Windows.Paths.WindowsAppPaths>();
                services.AddSingleton<ISettingsStorage>(AtomicJsonSettingsStorage.Default);
                services.AddSingleton<SettingsManager>();
                services.AddSingleton<ISettingsManager>(sp => sp.GetRequiredService<SettingsManager>());
                services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<IslandDisplayOptions>>(sp => sp.GetRequiredService<SettingsManager>().DisplayMonitor);
                services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<PollingOptions>>(sp => sp.GetRequiredService<SettingsManager>().PollingMonitor);
                services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<AlertOptions>>(sp => sp.GetRequiredService<SettingsManager>().AlertMonitor);
                services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<ProviderVisibilityOptions>>(sp => sp.GetRequiredService<SettingsManager>().ProviderVisibilityMonitor);
                services.AddSingleton<IUiDispatcher, WpfUiDispatcher>();
                services.AddSingleton<INetworkConnectivityService, SystemNetworkConnectivityService>();
                services.AddTransient<OfflineFastFailHandler>();
                services.AddHttpClient();
                services.AddHttpClient(AgentIsland.Backend.Usage.Http.ResilientClientName)
                    .AddHttpMessageHandler<OfflineFastFailHandler>()
                    .AddStandardResilienceHandler(options =>
                    {
                        options.Retry.MaxRetryAttempts = 2;
                        options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
                        options.Retry.UseJitter = true;
                        options.Retry.Delay = TimeSpan.FromMilliseconds(500);

                        options.CircuitBreaker.FailureRatio = 0.5;
                        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                        options.CircuitBreaker.MinimumThroughput = 4;
                        options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

                        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                    });

                services.AddAgentProviders();

                // UI & Settings Stores
                services.AddSingleton<IslandModel>();
                services.AddSingleton<IIslandModel>(sp => sp.GetRequiredService<IslandModel>());
                services.AddSingleton<ScreenPref>();
                services.AddSingleton<IslandPositionStore>();
                services.AddSingleton<IslandTargetDisplayStore>();
                services.AddSingleton<IslandScaleStore>();
                services.AddSingleton<LowPowerModeStore>();
                services.AddSingleton<GlowColorStore>();
                services.AddSingleton<QuotaDisplayModeStore>();
                services.AddSingleton<AlwaysShowUsageStore>();
                services.AddSingleton<StylePreferenceStore>();
                services.AddSingleton<CostStylePreferenceStore>();
                services.AddSingleton<TokenCountModeStore>();
                services.AddSingleton<AlertThresholdStore>();
                services.AddSingleton<RefreshIntervalStore>();
                services.AddSingleton<AgentReminderStore>();
                services.AddSingleton<QuotaAlarmStore>();

                // Domain & Usage Stores
                services.AddSingleton<ProviderVisibilityStore>();
                services.AddSingleton<IProviderVisibilityStore>(sp => sp.GetRequiredService<ProviderVisibilityStore>());

                services.AddSingleton<GrokUsageStore>();
                services.AddSingleton<IGrokUsageStore>(sp => sp.GetRequiredService<GrokUsageStore>());

                services.AddSingleton<CursorUsageStore>();
                services.AddSingleton<ICursorUsageStore>(sp => sp.GetRequiredService<CursorUsageStore>());

                services.AddSingleton<AntigravityUsageStore>();
                services.AddSingleton<IAntigravityUsageStore>(sp => sp.GetRequiredService<AntigravityUsageStore>());

                services.AddSingleton<DeepSeekBalanceStore>();
                services.AddSingleton<IDeepSeekBalanceStore>(sp => sp.GetRequiredService<DeepSeekBalanceStore>());

                services.AddSingleton<CostQueryService>();
                services.AddSingleton<ICostQueryService>(sp => sp.GetRequiredService<CostQueryService>());

                services.AddSingleton<CostStore>();
                services.AddSingleton<ICostStore>(sp => sp.GetRequiredService<CostStore>());

                services.AddSingleton<UsageStore>();
                services.AddSingleton<IUsageStore>(sp => sp.GetRequiredService<UsageStore>());

                services.AddSingleton<ActivityMonitor>();
                services.AddSingleton<IActivityMonitor>(sp => sp.GetRequiredService<ActivityMonitor>());

                services.AddSingleton<TurnAlarmWindowController>(sp => new TurnAlarmWindowController(
                    sp.GetService<AgentReminderStore>(),
                    sp));
                services.AddSingleton<ITurnAlarmWindowController>(sp => sp.GetRequiredService<TurnAlarmWindowController>());

                services.AddSingleton<AgentReminderCenter>();
                services.AddSingleton<IAgentReminderCenter>(sp => sp.GetRequiredService<AgentReminderCenter>());

                services.AddSingleton<UsageExhaustionAlarm>();
                services.AddSingleton<IUsageExhaustionAlarm>(sp => sp.GetRequiredService<UsageExhaustionAlarm>());

                services.AddSingleton<AlertEngine>();
                services.AddSingleton<IAlertEngine>(sp => sp.GetRequiredService<AlertEngine>());

                services.AddSingleton<UpdateChecker>();
                services.AddSingleton<IUpdateChecker>(sp => sp.GetRequiredService<UpdateChecker>());

                // Window & Dialog Services
                services.AddSingleton<IWindowService, WpfWindowService>();
                services.AddSingleton<IDialogService, WpfDialogService>();

                // Windows & ViewModels
                services.AddTransient<IslandWindow>();
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

        // Before any store singleton or worker reads a key: settings written by pre-1.7
        // builds carry the MacIsland.* prefix and must land on AgentIsland.*.
        AgentIsland.Windows.Preferences.MigrateLegacyPrefix();
        AgentIsland.Backend.Settings.AppLanguageStore.ApplyAtStartup();

        try
        {
            Host.StartAsync().GetAwaiter().GetResult();
            Http.Configure(Services.GetRequiredService<System.Net.Http.IHttpClientFactory>());
        }
        catch { }

        var activity = Services.GetRequiredService<ActivityMonitor>();
        var usage = Services.GetRequiredService<UsageStore>();
        var cost = Services.GetRequiredService<CostStore>();
        var updates = Services.GetRequiredService<UpdateChecker>();
        var exhaustionAlarm = Services.GetRequiredService<UsageExhaustionAlarm>();
        var alertEngine = Services.GetRequiredService<AlertEngine>();

        activity.Configure(AgentCatalog);

        _island = Services.GetRequiredService<IslandWindow>();
        _island.Show();
        if (Environment.GetEnvironmentVariable("AGENTISLAND_AUTO_POPUP") == "1")
        {
            _island.PopUp();
        }

        _tray = new TrayIcon(
            showIsland: () => WindowService.ShowIsland(),
            toggleIsland: () => WindowService.ToggleIsland(),
            openSettings: () => WindowService.OpenSettings(),
            exit: () =>
            {
                _tray?.Dispose();
                Shutdown();
            });
        TrayIcon.Current = _tray;

        // Demo pinning must come AFTER Start(): Demo() snapshots the
        // monitored provider set, which stays empty until Start() computes it
        // from the visibility slots — an earlier call pinned nothing and real
        // scan states leaked into the demo (the pin never took).
        activity.Start();
        if (AppEnvironment.IsDemo)
        {
            activity.Demo(ActivityState.Working);
        }
        usage.StartAutoRefresh();
        cost.StartAutoRefresh();
        updates.Start();
        exhaustionAlarm.Start();
        AgentIsland.Backend.Updates.UpdateInstaller.CleanupAtStartup();
        alertEngine.Start();

        // Release card: once per version, shortly after the island lands

        // Weekly report moment: once per ISO week, surface the card shortly
        // after launch. Suppressed for demo/debug/snapshot runs.
        var reportSnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_REPORT_SNAPSHOT");
        var monthlySnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_MONTHLY_SNAPSHOT");
        var dailySnapshot = Environment.GetEnvironmentVariable("AGENTISLAND_DAILY_SNAPSHOT");
        if (AppEnvironment.Current == AppMode.Normal
            && reportSnapshot is null && monthlySnapshot is null && dailySnapshot is null)
        {
            UI.Report.ReportWindow.ArmWeeklyMoment();
        }

        // Headless card renders for tooling/screenshots, mirroring macOS.
        // Data-driven, not a fixed delay: a cache-version bump forces a full
        // rescan that can take well past any polite sleep (a 5s beat wrote
        // all-zero cards the day the Codex cache went v2). Render on the
        // first completed scan; a 90s failsafe keeps a wedged scan from
        // leaving the process running forever.
        if (!string.IsNullOrEmpty(reportSnapshot) || !string.IsNullOrEmpty(monthlySnapshot)
            || !string.IsNullOrEmpty(dailySnapshot))
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
                if (!string.IsNullOrEmpty(dailySnapshot))
                    UI.Report.ReportWindow.WritePng(UI.Report.ReportWindow.Kind.Daily, dailySnapshot!);
                Shutdown();
            }
            if (cost.LastUpdated is not null)
            {
                RenderAndQuit();
            }
            else
            {
                cost.PropertyChanged += (_, args) =>
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
                activity.Demo(forced);
            }
            // _EXPANDED=1 renders the open panel (header chip, tiles, footer)
            // instead of the compact bar; _SCREEN picks the carousel page
            // without persisting it over the user's parked choice.
            if (Enum.TryParse<UI.IslandScreen>(
                    Environment.GetEnvironmentVariable("AGENTISLAND_DEBUG_ISLAND_SCREEN"),
                    ignoreCase: true, out var screen))
            {
                Services.GetRequiredService<ScreenPref>().ForceForVerification(screen);
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
        _island = Services.GetRequiredService<IslandWindow>();
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
            (Services.GetService(typeof(UsageStore)) as UsageStore)?.StopAutoRefresh();
            (Services.GetService(typeof(CostStore)) as CostStore)?.StopAutoRefresh();
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
