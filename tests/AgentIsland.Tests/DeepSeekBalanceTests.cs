using System.Text;
using AgentIsland.Backend.Usage;
using AgentIsland.Core;
using AgentIsland.Providers.Usage.DeepSeek;
using AgentIsland.UI;

namespace AgentIsland.Tests;

/// Contract tests for the official DeepSeek account-balance response and the
/// small DSH credentials reader. These tests never make a network request and
/// never print a secret.
public class DeepSeekBalanceTests
{
    [WpfFact]
    public void TestDeepSeekBalance() => RunAll();

    internal static void RunAll()
    {
        TestOfficialResponse();
        TestDebtResponse();
        TestMissingOptionalAmounts();
        TestCredentialsYaml();
        TestBalancePillRendering();
        Console.WriteLine("DeepSeekBalanceTests GREEN");
    }

    private static void TestOfficialResponse()
    {
        const string json = """
        {
          "is_available": true,
          "balance_infos": [
            {
              "currency": "CNY",
              "total_balance": "12.340000",
              "granted_balance": "10.000000",
              "topped_up_balance": "2.340000"
            }
          ]
        }
        """;

        var snapshot = DeepSeekBalanceParser.Parse(Encoding.UTF8.GetBytes(json));
        Expect(snapshot is not null, "official balance response must parse");
        Expect(snapshot!.IsAvailable, "is_available must be preserved");
        Expect(snapshot.Balances.Count == 1, "one currency bucket must be retained");
        var info = snapshot.Balances[0];
        Expect(info.Currency == "CNY", "currency must be normalized");
        Expect(info.TotalBalance == 12.34m, "total balance must preserve decimal value");
        Expect(info.GrantedBalance == 10m, "granted balance must parse");
        Expect(info.ToppedUpBalance == 2.34m, "topped-up balance must parse");
        Console.WriteLine("PASS DeepSeek official balance response");
    }

    private static void TestDebtResponse()
    {
        const string json = """
        {
          "is_available": false,
          "balance_infos": [
            {
              "currency": "CNY",
              "total_balance": "-0.34",
              "granted_balance": "0.00",
              "topped_up_balance": "-0.34"
            }
          ]
        }
        """;

        var snapshot = DeepSeekBalanceParser.Parse(Encoding.UTF8.GetBytes(json));
        Expect(snapshot is not null && !snapshot.IsAvailable,
            "an official debt response must preserve unavailable state");
        var info = snapshot!.Balances[0];
        Expect(info.TotalBalance == -0.34m, "negative total balance must be preserved");
        Expect(info.ToppedUpBalance == -0.34m,
            "negative topped-up balance must be preserved");
        Expect(DeepSeekBalanceText.Amount(info, info.TotalBalance) == "-¥0.34",
            "negative CNY balance must render as a debt, not zero");
        Console.WriteLine("PASS DeepSeek debt balance response");
    }

    private static void TestMissingOptionalAmounts()
    {
        const string json = """
        {"is_available":"false","balance_infos":[
          {"currency":"usd","total_balance":0.5}
        ]}
        """;
        var snapshot = DeepSeekBalanceParser.Parse(Encoding.UTF8.GetBytes(json));
        Expect(snapshot is not null && !snapshot.IsAvailable, "string availability must parse");
        Expect(snapshot!.Balances[0].GrantedBalance == 0m,
            "missing granted balance must default to zero");
        Expect(snapshot.Balances[0].ToppedUpBalance == 0m,
            "missing topped-up balance must default to zero");
        Expect(DeepSeekBalanceParser.Parse(Encoding.UTF8.GetBytes("{}")) is null,
            "response without availability must be rejected");
        Console.WriteLine("PASS DeepSeek balance optional-field handling");
    }

    private static void TestCredentialsYaml()
    {
        const string yaml = """
        version: 1
        refs:
          MY_GATEWAY_API_KEY: ignored
          DEEPSEEK_API_KEY: "bce-v3-example#key" # keep quoted hash
        other:
          DEEPSEEK_API_KEY: wrong-scope
        """;
        Expect(DeepSeekCredentials.ParseApiKey(yaml) == "bce-v3-example#key",
            "only refs.DEEPSEEK_API_KEY must be resolved");
        Expect(DeepSeekCredentials.ParseApiKey("refs:\n  DEEPSEEK_API_KEY: plain-key # comment") == "plain-key",
            "unquoted YAML comments must be stripped");
        Expect(DeepSeekCredentials.ParseApiKey("refs:\n  DEEPSEEK_API_KEY: null") is null,
            "null API keys must be treated as absent");
        Console.WriteLine("PASS DeepSeek Harness credentials resolution");
    }

    private static void TestBalancePillRendering()
    {
        var pill = new NotchPeekPill
        {
            Tool = TriggerTool.DeepSeek,
            Mirrored = true,
        };
        pill.UpdateBalance("-¥0.34", loading: false, unavailable: true);
        var debtText = string.Concat(
            pill.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text ?? string.Empty));
        Expect(debtText.Contains("-¥0.34", StringComparison.Ordinal),
            "DeepSeek pill must render the account balance amount");
        Expect(debtText.Contains("⚠", StringComparison.Ordinal),
            "an unavailable DeepSeek balance must retain a visible warning marker");

        pill.UpdateBalance(null, loading: true);
        var loadingText = string.Concat(
            pill.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text ?? string.Empty));
        Expect(loadingText == "…",
            "DeepSeek pill must show a loading marker before the first balance arrives");
        Console.WriteLine("PASS DeepSeek balance pill renders amount, warning, and loading states");
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
