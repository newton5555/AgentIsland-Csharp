using AgentIsland.Core.Agents;
using AgentIsland.Core.Cost;
using AgentMonitoring.Consumption;
using Xunit;

namespace AgentMonitoring.Tests;

public sealed class StrictConsumptionPersistenceTests
{
    [Fact]
    public void StrictStore_ReportsWriteFailureToTheCollector()
    {
        var root = Path.Combine(Path.GetTempPath(), $"strict-store-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "store.json");
        Directory.CreateDirectory(storePath);
        try
        {
            var store = new ConsumptionStore(storePath, strictPersistence: true);
            Assert.Throws<IOException>(() => store.Commit(
                AgentKeys.Codex,
                Array.Empty<ConsumptionFact>(),
                new Dictionary<string, CodexFileState>()));
        }
        finally
        {
            TryDelete(storePath + ".tmp");
            TryDeleteDir(storePath);
            TryDeleteDir(root);
        }
    }

    [Fact]
    public void StrictStore_RejectsCorruptPersistenceInsteadOfStartingEmpty()
    {
        var root = Path.Combine(Path.GetTempPath(), $"strict-corrupt-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "store.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(storePath, "not-json");
        try
        {
            Assert.Throws<InvalidDataException>(() => new ConsumptionStore(storePath, strictPersistence: true));
            Assert.Equal("not-json", File.ReadAllText(storePath));
        }
        finally
        {
            TryDelete(storePath);
            TryDeleteDir(root);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
