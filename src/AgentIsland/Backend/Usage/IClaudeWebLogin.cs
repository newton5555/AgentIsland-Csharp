namespace AgentIsland.Backend.Usage;

public interface IClaudeWebLogin
{
    Task<ClaudeWebLogin.Outcome> Start(Action<string>? onAuthorizeUrl = null);
}
