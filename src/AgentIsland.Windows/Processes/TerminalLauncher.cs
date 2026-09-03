using System.Diagnostics;
using System.IO;
using AgentIsland.Windows;

namespace AgentIsland.Windows.Processes;

/// Opens a visible terminal window running a CLI command — the Windows
/// counterpart of the macOS `.command`-file-in-Terminal trick. Prefers
/// Windows Terminal when installed, falls back to a plain cmd window.
public static class TerminalLauncher
{
    public static bool RunVisible(string binary, string arguments, string title)
    {
        var command = $"\"{binary}\" {arguments}";
        try
        {
            var wt = CLILocator.Locate("wt");
            if (wt is not null)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wt,
                    Arguments = $"new-tab --title \"{title}\" cmd /k {command}",
                    UseShellExecute = false,
                });
                return true;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c start \"{title}\" cmd /k {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = IslandPaths.Home,
            });
            return true;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"AgentIsland: failed to open terminal: {error.Message}");
            return false;
        }
    }
}
