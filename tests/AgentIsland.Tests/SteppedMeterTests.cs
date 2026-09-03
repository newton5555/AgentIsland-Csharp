using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using AgentIsland.UI.Charts;
using AgentIsland.UI.Theme;
using AgentIsland.Usage;

namespace AgentIsland.Tests;

public static class SteppedMeterTests
{
    private static void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Console.WriteLine($"FAIL: {message}");
            Environment.Exit(1);
        }
    }

    public static void RunAll()
    {
        Console.WriteLine("--- SteppedMeterTests ---");
        var app = Application.Current;
        app ??= new Application();
        // Closing the temporary host must not shut down the shared WPF
        // dispatcher before the animation tests that follow this suite.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var meter = new SteppedMeter(Colors.DeepSkyBlue);
        var usage = new WindowUsage(
            UsedPercent: 50,
            ResetAt: DateTimeOffset.Now.AddHours(4),
            Error: null,
            PeriodSeconds: 5 * 3600);

        // This is the ordering used by UsagePage: data arrives before the
        // newly visible control has received its first layout pass.
        meter.Update(usage.UsedPercent, usage);

        var host = new Window
        {
            Width = 240,
            Height = 50,
            ShowActivated = false,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = meter,
        };

        try
        {
            host.Show();
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(
                () => { }, DispatcherPriority.Render);

            var cells = meter.Children.OfType<Rectangle>().ToArray();
            var quotaCells = cells.Where(cell => Grid.GetRow(cell) == 0).ToArray();
            var timeCells = cells.Where(cell => Grid.GetRow(cell) == 2).ToArray();

            Expect(quotaCells.Length >= 10, "stepped meter must build quota cells after first layout");
            Expect(timeCells.Length == quotaCells.Length, "quota and reset rows must have matching cell counts");
            Expect(
                quotaCells.Any(cell => ((SolidColorBrush)cell.Fill).Color == Colors.DeepSkyBlue),
                "quota row must preserve an update that arrived before layout");
            Expect(
                timeCells.Any(cell => ((SolidColorBrush)cell.Fill).Color == IslandColors.LiveTeal),
                "reset-time row must preserve an update that arrived before layout");

            Console.WriteLine("PASS stepped meter preserves both rows across update-before-layout");
        }
        finally
        {
            host.Close();
        }
    }
}
