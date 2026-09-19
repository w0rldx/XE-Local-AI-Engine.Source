namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

/// <summary>
///     One tool a Tool node may run. <see cref="ParameterSchema" /> is the RAW JSON-schema text rather than a typed
///     shape: every tool declares a different object, and the editor parses this string to draw the argument form —
///     the same way <c>AllowedToolDto.ParameterSchema</c> carries it on the agent surface.
/// </summary>
public sealed class GraphWorkflowToolResponse
{
    public required string Name { get; init; }

    public required string Description { get; init; }

    public required string ParameterSchema { get; init; }
}

/// <summary>
///     The Tool node picker's whole feed. A concrete type rather than a generic envelope, for the same reason as
///     its siblings: NSwag builds schema ids from the CLR type name.
/// </summary>
public sealed class ListGraphWorkflowToolsResponse
{
    public required IReadOnlyList<GraphWorkflowToolResponse> Tools { get; init; }
}
