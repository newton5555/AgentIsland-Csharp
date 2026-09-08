using System.Reflection;
using System.Runtime.CompilerServices;
using AgentIsland.Backend.Usage;
using AgentIsland.UI.Report;

namespace AgentIsland.Tests;

public sealed class RefreshAndReportBoundaryTests
{
    [Fact]
    public void OldCompletionCannotClearNewGuestRefresh()
    {
        foreach (var type in new[] { typeof(AntigravityUsageStore), typeof(GrokUsageStore),
                     typeof(CursorUsageStore), typeof(DeepSeekBalanceStore) })
        {
            // Exercise the queued completion without reading real credentials or starting HTTP.
            var store = RuntimeHelpers.GetUninitializedObject(type);
            var generation = type.GetField("_refreshGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var request = type.GetField("_refreshCts", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var loading = type.GetField("_loading", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var complete = type.GetMethod("CompleteRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;
            using var oldRequest = new CancellationTokenSource();
            using var newRequest = new CancellationTokenSource();
            generation.SetValue(store, 2L);
            request.SetValue(store, newRequest);
            loading.SetValue(store, true);

            // Old callback runs after disable/re-enable and a replacement request.
            complete.Invoke(store, new object[] { 1L, oldRequest });
            Assert.True((bool)loading.GetValue(store)!);
            Assert.Same(newRequest, request.GetValue(store));
            newRequest.Cancel(); // Replacement request must remain usable.

            complete.Invoke(store, new object[] { 2L, newRequest });
            Assert.False((bool)loading.GetValue(store)!);
            Assert.Null(request.GetValue(store));
        }
    }

    [Fact]
    public void ReportBoundariesUseOffsetAtEachHistoricalDate()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        foreach (var kind in new[] { DateTimeKind.Unspecified, DateTimeKind.Local })
        {
            var winter = ReportPeriods.AtLocalBoundary(new DateTime(2026, 1, 1, 0, 0, 0, kind), zone);
            var summer = ReportPeriods.AtLocalBoundary(new DateTime(2026, 7, 1, 0, 0, 0, kind), zone);
            Assert.Equal(TimeSpan.FromHours(-5), winter.Offset);
            Assert.Equal(TimeSpan.FromHours(-4), summer.Offset);
            var start = ReportPeriods.AtLocalBoundary(new DateTime(2026, 3, 8, 0, 0, 0, kind), zone);
            var end = ReportPeriods.AtLocalBoundary(new DateTime(2026, 3, 9, 0, 0, 0, kind), zone);
            Assert.Equal(TimeSpan.FromHours(23), end - start);
            start = ReportPeriods.AtLocalBoundary(new DateTime(2026, 11, 1, 0, 0, 0, kind), zone);
            end = ReportPeriods.AtLocalBoundary(new DateTime(2026, 11, 2, 0, 0, 0, kind), zone);
            Assert.Equal(TimeSpan.FromHours(25), end - start);
        }
    }
}
