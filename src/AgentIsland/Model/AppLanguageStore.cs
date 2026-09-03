using AgentIsland.Core;
using AgentIsland.Localization;

namespace AgentIsland.Model;

/// Persisted language override. Applied at startup; static labels are built
/// once, so switching prompts for an app restart (the stores and data keep
/// running — only the label language lags until then).
public static class AppLanguageStore
{
    private const string Key = "AgentIsland.appLanguage";

    public static L10n.Language Load()
    {
        // Verification-rig override: the CI snapshot sweep renders the
        // Chinese and English UIs on a machine whose prefs say neither.
        switch (Environment.GetEnvironmentVariable("AGENTISLAND_LANG"))
        {
            case "zh": return L10n.Language.SimplifiedChinese;
            case "en": return L10n.Language.English;
        }
        var raw = Preferences.Get<string?>(Key) ?? "";
        return raw switch
        {
            "en" => L10n.Language.English,
            "zh-Hans" => L10n.Language.SimplifiedChinese,
            _ => L10n.Language.Auto,
        };
    }

    public static void Save(L10n.Language language)
    {
        Preferences.Set(Key, language switch
        {
            L10n.Language.English => "en",
            L10n.Language.SimplifiedChinese => "zh-Hans",
            _ => "",
        });
    }

    public static void ApplyAtStartup() => L10n.Current = Load();
}
