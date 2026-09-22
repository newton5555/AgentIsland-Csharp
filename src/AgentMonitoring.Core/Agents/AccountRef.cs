namespace AgentIsland.Core.Agents;

/// Identifies an account that consumption, quota, or balance can belong to.
/// AccountId is null when the source cannot name an account; callers must not
/// substitute the currently signed-in user.
public sealed record AccountRef(AgentKey Agent, string? AccountId, string? Label = null)
{
    public bool IsUnknown => string.IsNullOrWhiteSpace(AccountId);
}
