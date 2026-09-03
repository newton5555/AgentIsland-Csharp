using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using AgentIsland.Core;

namespace AgentIsland.Update;

/// Turns "a newer release exists" into "the app is now running it". The
/// distribution is a single self-contained exe, so an update is one file
/// swap: stream the release zip into %APPDATA%\AgentIsland\updates, unpack
/// AgentIsland.exe next to the running one as .new, rename the running exe
/// away (Windows allows renaming a running image, never overwriting it),
/// rename .new into place, relaunch, and let the fresh instance sweep the
/// leftovers. Every failure rolls back to the original exe and falls open
/// to the releases page — the user is never left without a working app.
public static class UpdateInstaller
{
    /// Breadcrumb for scripted verification, mirroring TurnAlarmNavigator.
    public static string LastAction { get; private set; } = "";

    private static string StagingDir =>
        Path.Combine(IslandPaths.RoamingAppData, "AgentIsland", "updates");

    /// The swap names live next to the exe so both renames stay on one
    /// volume (atomic) no matter where the user unpacked the app.
    internal static (string Old, string New) PlanSwap(string exePath) =>
        (exePath + ".old", exePath + ".new");

    public static async Task RunAsync(UpdateInfo info)
    {
        if (info.AssetUrl is null)
        {
            UpdateChecker.OpenReleasesPage();
            return;
        }

        var dialog = UI.IslandDialog.ShowAppProgress(
            Localization.L10n.TrFormat("Updating to {0}…", info.Tag),
            Localization.L10n.TrFormat("Downloading update… {0}%", 0));
        try
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("no process path");
            var (oldPath, newPath) = PlanSwap(exe);

            LastAction = "download";
            var zip = await DownloadAsync(info, percent =>
                dialog.SetMessage(Localization.L10n.TrFormat("Downloading update… {0}%", percent)));

            dialog.SetMessage(Localization.L10n.Tr("Installing update…"));
            LastAction = "extract";
            await Task.Run(() => ExtractExe(zip, newPath));
            TryDelete(zip);

            LastAction = "swap";
            if (File.Exists(oldPath)) File.Delete(oldPath);
            File.Move(exe, oldPath);
            try
            {
                File.Move(newPath, exe);
            }
            catch
            {
                File.Move(oldPath, exe); // roll back: original image restored
                throw;
            }

            LastAction = "relaunch";
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            });
            LastAction = $"updated({info.Tag})";
            dialog.Close();
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            LastAction = $"failed({LastAction}: {error.Message})";
            dialog.Close();
            UI.IslandDialog.ShowUpdate(
                "Agent Island",
                Localization.L10n.Tr("Automatic update failed. You can download the new version manually from GitHub Releases."),
                primaryLabel: Localization.L10n.Tr("Open download page"),
                primaryAction: UpdateChecker.OpenReleasesPage,
                secondaryLabel: Localization.L10n.Tr("I know"));
        }
    }

    /// Stream the zip to staging with progress. A dedicated client: the
    /// shared 30s Usage timeout would abort a 65MB download on a slow link.
    private static async Task<string> DownloadAsync(UpdateInfo info, Action<int> onPercent)
    {
        Directory.CreateDirectory(StagingDir);
        var zipPath = Path.Combine(StagingDir, info.AssetName ?? "update.zip");

        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var request = new HttpRequestMessage(HttpMethod.Get, info.AssetUrl);
        request.Headers.UserAgent.ParseAdd("AgentIsland-Windows-Updater");
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.AssetSize;
        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = new FileStream(
            zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var buffer = new byte[81920];
            long done = 0;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                {
                    var percent = (int)(done * 100 / total);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        onPercent(percent);
                    }
                }
            }
        }

        // The feed publishes the exact asset size — a mismatch means a
        // truncated or tampered download, not something to run.
        if (info.AssetSize > 0 && new FileInfo(zipPath).Length != info.AssetSize)
        {
            TryDelete(zipPath);
            throw new IOException("downloaded size mismatch");
        }
        return zipPath;
    }

    private static void ExtractExe(string zipPath, string destination)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var entry = archive.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, "AgentIsland.exe", StringComparison.OrdinalIgnoreCase))
            ?? throw new IOException("zip has no AgentIsland.exe");
        // A self-contained WPF exe is >100MB; anything tiny is not our app.
        if (entry.Length < 20_000_000) throw new IOException("exe in zip is implausibly small");
        entry.ExtractToFile(destination, overwrite: true);
    }

    /// The fresh instance sweeps what the old one couldn't delete about
    /// itself: the renamed-away .old image (locked until that process fully
    /// exits — hence the retry loop), a stray .new from an interrupted swap,
    /// and any staged zips.
    public static void CleanupAtStartup()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var (oldPath, newPath) = PlanSwap(exe);
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (File.Exists(oldPath)) File.Delete(oldPath);
                    if (File.Exists(newPath)) File.Delete(newPath);
                    if (Directory.Exists(StagingDir)) Directory.Delete(StagingDir, recursive: true);
                    return;
                }
                catch
                {
                    await Task.Delay(1000);
                }
            }
        });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
