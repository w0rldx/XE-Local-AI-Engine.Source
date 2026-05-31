namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>
///     Resolves the executable <see cref="AITool" /> for an offered <c>ClientLocal</c> tool name (ClientLocal). The
///     invocation factory consults this registry for offered names the in-process catalog
///     (<see cref="IAgentToolRegistry" />) does not satisfy, so a server-driven <c>ToolDefinition(ClientLocal)</c>
///     is substituted for its name-only placeholder before the agent runs. Names matched by neither registry are
///     dropped, leaving <c>toolsEnabled</c> accurate.
/// </summary>
internal interface IClientLocalToolRegistry
{
    bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool);
}
