namespace XE_Local_AI_Engine.AI.Agent.Sessions;

using System.Diagnostics;

internal static class AgentActivitySource
{
    internal static readonly ActivitySource Instance = new("XE.LocalAiEngine.AI.Agent", "1.0.0");
}
