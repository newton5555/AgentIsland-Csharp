using AgentIsland.Core;
using AgentIsland.UI.Providers;
using AgentIsland.UI;

namespace AgentIsland.Tests;

/// Pins the solo-split geometry: a lone visible provider
/// never narrows the bar — the full symmetric width stays.
/// SoloProvider reports which tool is alone; both-visible and both-hidden report null.
/// A single pick is always left-justified.
[Collection("SettingsDiskTests")]
public class SoloCenterLayoutTests
{
    [WpfFact]
    public void TestSoloCenterLayout() => RunAll();

    internal static void RunAll()
    {
        var prevStorage = AgentIsland.Windows.Preferences.Storage;
        AgentIsland.Windows.Preferences.Storage = new AgentIsland.Core.Storage.MemorySettingsStorage();
        try
        {
            var storage = new AgentIsland.Core.Storage.MemorySettingsStorage();
            var visibility = new ProviderVisibilityStore(storage);
            var position = new IslandPositionStore();
            var alwaysShow = new AlwaysShowUsageStore();
            var model = new IslandModel(visibility, position, alwaysShow);

            position.Placement = IslandPlacement.TopBar;
            alwaysShow.Enabled = false;
            foreach (var provider in DisplayProviders.All)
            {
                visibility.SetEnabled(provider, false);
            }
            visibility.ClaudeVisible = true;
            visibility.CodexVisible = true;

            Expect(model.SoloProvider is null, "two visible providers are not solo");
            Expect(model.Size.Width == 92, $"symmetric top bar must be 16+38*2, got {model.Size.Width}");

            visibility.CodexVisible = false;
            Expect(model.SoloProvider == TriggerTool.Claude, "Claude alone reports solo Claude");
            Expect(model.Size.Width == 92,
                $"solo keeps the exact same compact width as dual-agent (16+38*2=92), got {model.Size.Width}");
            Console.WriteLine("PASS solo keeps the same width as dual-agent (split layout)");

            visibility.CodexVisible = true;
            visibility.ClaudeVisible = false;
            Expect(model.SoloProvider == TriggerTool.Codex && model.Size.Width == 92,
                "a single pick is width-stable and identical to dual-agent");
            Console.WriteLine("PASS solo is width-stable");

            visibility.CodexVisible = false;
            Expect(model.SoloProvider is null, "both hidden is not solo");
            Expect(model.Size.Width == 92, $"both-hidden keeps symmetric width, got {model.Size.Width}");
            Console.WriteLine("PASS both-hidden stays symmetric");

            // Peek/hover expands back to full original width
            model.State = IslandState.Peek;
            Expect(model.Size.Width == 484, $"peek/hover expands to full original width 200+(38+104)*2=484, got {model.Size.Width}");
            model.State = IslandState.Compact;

            // When AlwaysShowUsage is enabled, compact bar displays usage pills with collapsed 16px center gap (16+(38+104)*2 = 300)
            alwaysShow.Enabled = true;
            Expect(model.Size.Width == 300, $"compact with always-show-usage must be 16+(38+104)*2=300, got {model.Size.Width}");
            alwaysShow.Enabled = false;

            visibility.CodexVisible = true;
            position.Placement = IslandPlacement.Floating;
            Expect(model.Size.Width == 92, $"floating solo keeps 16+38*2, got {model.Size.Width}");
            visibility.ClaudeVisible = true;
            Expect(model.Size.Width == 92, $"symmetric floating must be 16+38*2, got {model.Size.Width}");
            Console.WriteLine("PASS floating placement widths hold, solo and duo");
            Console.WriteLine("SoloCenterLayoutTests GREEN");
        }
        finally
        {
            AgentIsland.Windows.Preferences.Storage = prevStorage;
        }
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
