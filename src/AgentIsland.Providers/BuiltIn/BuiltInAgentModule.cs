using AgentIsland.Core.Agents;

namespace AgentIsland.Providers.BuiltIn;

internal sealed record BuiltInAgentModule(AgentDescriptor Descriptor) : IAgentModule;
