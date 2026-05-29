namespace XE_Local_AI_Engine.AI.Agent.Tools;

/// <summary>
///     A worker-owned executable for a <c>ToolLocation.ClientLocal</c> tool driven by a server
///     <c>ToolDefinition</c> (Option B). The handler is JSON-in / JSON-out and owns its own model-visible
///     <see cref="ParameterSchema" />, <see cref="Description" />, and <see cref="RequiresApproval" /> so the
///     registry can build a fully-described, optionally approval-gated tool for it. Implementations live in the
///     worker application layer; the registry that consumes them lives here in the agent layer.
/// </summary>
internal interface IClientLocalToolHandler
{
    string ToolName { get; }

    string Description { get; }

    /// <summary>Model-visible JSON schema; matches the server <c>ToolDefinition.ParameterSchema</c> for this tool.</summary>
    string ParameterSchema { get; }

    bool RequiresApproval { get; }

    Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default);
}
