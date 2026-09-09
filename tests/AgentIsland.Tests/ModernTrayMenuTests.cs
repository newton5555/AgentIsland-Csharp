using System;
using System.Threading;
using System.Windows;
using AgentIsland.Core;
using AgentIsland.UI;
using Xunit;

namespace AgentIsland.Tests;

public sealed class ModernTrayMenuTests
{
    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestEnvironment.EnsureInitialized();
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            throw new AggregateException("STA thread failure", error);
        }
    }

    [Fact]
    public void ModernTrayMenu_LifecycleAndStatusUpdate_WorksProperly()
    {
        RunOnSta(() =>
        {
            bool toggled = false;
            bool isTransparent = false;

            var menu = new ModernTrayMenu(
                showIsland: () => { },
                toggleTransparentMode: () =>
                {
                    toggled = true;
                    isTransparent = !isTransparent;
                },
                openSettings: () => { },
                exit: () => { },
                openDailyReport: () => { },
                openWeeklyReport: () => { },
                openMonthlyReport: () => { },
                isTransparentModeQuery: () => isTransparent);

            Assert.NotNull(menu);
            Assert.False(menu.IsOpen);

            menu.Show();
            Assert.True(menu.IsOpen);
            menu.Hide();
            Assert.False(menu.IsOpen);

            Assert.False(toggled);

            // Verify status updates
            menu.UpdateStatus("Claude 45% · Codex 12%", ActivityState.Working);
            menu.UpdateStatus("Quota exceeded", ActivityState.AuthRequired);
            menu.UpdateStatus("Idle", ActivityState.Idle);

            menu.Destroy();
        });
    }

    [Fact]
    public void TrayIcon_InstantiatesWithModernMenu()
    {
        RunOnSta(() =>
        {
            var tray = new TrayIcon(
                showIsland: () => { },
                toggleIsland: () => { },
                openSettings: () => { },
                exit: () => { });

            Assert.NotNull(tray);
            tray.Dispose();
        });
    }
}
