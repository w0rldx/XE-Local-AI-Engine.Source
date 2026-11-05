namespace XE_Local_AI_Engine.Client.Services.AgentHome.Tools;

/// <summary>
///     Typed projection of the <c>run_in_agent_home</c> JSON arguments.
/// </summary>
/// <remarks>
///     The <see cref="AI.Agent.Tools.Implementation.MetadataToolFunction" /> bridge stays JSON-in / JSON-out, so the handler deserializes into
///     this record and validates it against the tool constraints before any execution. The schema advertised to the
///     model is advisory, this validation is authoritative.
/// </remarks>
internal sealed record AgentHomeRunToolRequest
{
    public string? Goal { get; init; }

    public IReadOnlyList<string>? SelectedFolderIds { get; init; }

    public string? RuntimeProfile { get; init; }

    public string? Persona { get; init; }

    public IReadOnlyList<string>? AllowedActions { get; init; }
}
