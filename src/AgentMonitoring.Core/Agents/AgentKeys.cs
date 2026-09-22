namespace AgentIsland.Core.Agents;

/// Well-known AgentKey values for built-in agents. New agents still construct
/// AgentKey from their stable string; this list is only the current catalog.
public static class AgentKeys
{
    public static readonly AgentKey Claude = new("claude");
    public static readonly AgentKey Codex = new("codex");
    public static readonly AgentKey Antigravity = new("antigravity");
    public static readonly AgentKey Grok = new("grok");
    public static readonly AgentKey Cursor = new("cursor");
    public static readonly AgentKey DeepSeek = new("deepseek");
}
