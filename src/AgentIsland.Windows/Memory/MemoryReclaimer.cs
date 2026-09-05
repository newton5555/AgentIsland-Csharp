using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AgentIsland.Windows.Memory;

/// <summary>
/// Coordinates background memory reclamation when high-allocation providers
/// (like Codex or Claude log readers) are disabled or caches are invalidated.
/// Performs managed heap compaction (Gen 2 + LOH) and trims the process working set.
/// </summary>
public static class MemoryReclaimer
{
    private static readonly object Gate = new();
    private static CancellationTokenSource? _debounceCts;

    [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

    /// <summary>
    /// Schedules a debounced background memory reclamation run.
    /// Rapid consecutive calls (e.g., unticking multiple providers in settings)
    /// coalesce into a single reclamation run.
    /// </summary>
    /// <param name="delayMs">Debounce delay in milliseconds to let ongoing background tasks finish.</param>
    public static void ScheduleReclaim(int delayMs = 600)
    {
        lock (Gate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, cts.Token).ConfigureAwait(false);
                    PerformReclaim();
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer reclaim request.
                }
                catch
                {
                    // Best effort, never crash background threads.
                }
                finally
                {
                    lock (Gate)
                    {
                        if (ReferenceEquals(_debounceCts, cts))
                        {
                            _debounceCts = null;
                            cts.Dispose();
                        }
                    }
                }
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// Synchronously performs managed heap compaction and trims the Windows working set.
    /// </summary>
    public static void PerformReclaim()
    {
        try
        {
            // 1. Instruct CLR to compact the Large Object Heap on the next full GC
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

            // 2. Perform aggressive Gen 2 GC with compaction
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // 3. Trim unused physical pages back to the Windows OS
            if (OperatingSystem.IsWindows())
            {
                using var currentProcess = Process.GetCurrentProcess();
                SetProcessWorkingSetSize(currentProcess.Handle, new IntPtr(-1), new IntPtr(-1));
            }
        }
        catch
        {
            // Defensive: memory reclamation must never take down the process
        }
    }
}
