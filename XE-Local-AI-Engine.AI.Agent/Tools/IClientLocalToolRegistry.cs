namespace XE_Local_AI_Engine.AI.Agent.Tools;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

/// <summary>Resolves the executable <see cref="AITool" /> for an offered <c>ClientLocal</c> tool name.</summary>
/// <remarks>
///     The invocation factory consults this registry for offered names the in-process catalog
///     (<see cref="IAgentToolRegistry" />) does not satisfy, so a server-driven <c>ToolDefinition(ClientLocal)</c> is
///     substituted for its name-only placeholder before the agent runs. Names matched by neither registry are dropped,
///     leaving <c>toolsEnabled</c> accurate.
/// </remarks>
internal interface IClientLocalToolRegistry
{
    bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool);

    /// <summary>
    ///     When the tool's <see cref="IClientLocalToolPreflight" /> refuses right now, a variant WITHOUT the approval wrap whose every
    ///     call answers with the refusal and never runs the handler; otherwise <see langword="null" />.
    /// </summary>
    ValueTask<AITool?> ResolvePreflightRefusalAsync(string toolName, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AITool?>(null);
}
