namespace XE_Local_AI_Engine.AI.Agent.Sessions;

using System.Diagnostics;
using XE_Local_AI_Engine.AI.Contracts.Telemetry;

internal static class AgentActivitySource
{
    internal static readonly ActivitySource Instance = new(TelemetrySourceNames.Agent, "1.0.0");
}
