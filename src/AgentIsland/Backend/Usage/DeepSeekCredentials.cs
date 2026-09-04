using System.IO;
using System.Text.Json;
using AgentIsland.Windows;

namespace AgentIsland.Backend.Usage;

/// Resolves the official DeepSeek API key used by DeepSeek Harness. The
/// Harness credentials file is YAML, but the value we need lives in one small
/// `refs` entry; keeping this reader dependency-free avoids pulling a YAML
/// package into the desktop app.
public static class DeepSeekCredentials
{
    public static string CredentialsFile => Path.Combine(
        IslandPaths.Home, ".dsh", ".credentials.yaml");

    /// Environment overrides the Harness file, matching the `apiKeyEnv`
    /// convention used by DSH provider definitions. No value is written to
    /// Agent Island preferences or logs.
    public static string? ReadApiKey()
    {
        var environment = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        if (!string.IsNullOrWhiteSpace(environment)) return environment.Trim();

        try
        {
            if (!File.Exists(CredentialsFile)) return null;
            return ParseApiKey(File.ReadAllText(CredentialsFile));
        }
        catch
        {
            return null;
        }
    }

    internal static string? ParseApiKey(string yaml)
    {
        var inRefs = false;
        var refsIndent = -1;
        foreach (var rawLine in yaml.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var content = StripComment(line);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var indent = content.TakeWhile(char.IsWhiteSpace).Count();
            var trimmed = content.Trim();
            if (!inRefs)
            {
                if (trimmed.Equals("refs:", StringComparison.OrdinalIgnoreCase))
                {
                    inRefs = true;
                    refsIndent = indent;
                }
                continue;
            }

            // A new same-level YAML key ends the refs mapping. Nested values
            // below it are the only lines eligible to contain this secret.
            if (indent <= refsIndent && !trimmed.StartsWith("-", StringComparison.Ordinal))
            {
                inRefs = trimmed.Equals("refs:", StringComparison.OrdinalIgnoreCase);
                if (!inRefs) break;
                continue;
            }

            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed[..colon].Trim();
            if (!key.Equals("DEEPSEEK_API_KEY", StringComparison.OrdinalIgnoreCase)) continue;
            return ResolveScalar(trimmed[(colon + 1)..].Trim());
        }
        return null;
    }

    private static string? ResolveScalar(string raw)
    {
        if (raw.Length == 0 || raw is "~" or "null" or "NULL") return null;

        if (raw.StartsWith("${", StringComparison.Ordinal)
            && raw.EndsWith('}'))
        {
            return Environment.GetEnvironmentVariable(raw[2..^1]);
        }
        if (raw.Length > 0 && raw[0] == '$')
        {
            return Environment.GetEnvironmentVariable(raw[1..]);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(raw); }
            catch { return raw[1..^1]; }
        }
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'", StringComparison.Ordinal);
        }
        return raw.Trim();
    }

    /// Removes YAML comments only when they are outside a quoted scalar; an
    /// API key containing a `#` must not be truncated.
    private static string StripComment(string line)
    {
        var single = false;
        var doubleQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\'' && !doubleQuote) single = !single;
            else if (c == '"' && !single && (i == 0 || line[i - 1] != '\\')) doubleQuote = !doubleQuote;
            else if (c == '#' && !single && !doubleQuote
                && (i == 0 || char.IsWhiteSpace(line[i - 1])))
            {
                return line[..i];
            }
        }
        return line;
    }
}
