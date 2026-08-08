namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     The parsed arguments of a <c>spawn_subagent</c> tool call. Exactly one of <see cref="SubAgentKey" /> or
///     <see cref="ModelId" /> identifies the sub-agent's model binding; <see cref="Task" /> seeds the inner agent's
///     run; <see cref="Instructions" /> optionally overrides the inner agent's system prompt (when binding by
///     <see cref="ModelId" /> directly rather than reusing a persisted definition's prompt).
/// </summary>
public sealed record SubAgentSpawnRequest
{
    /// <summary>The persisted agent definition to spawn (its <c>Id</c> as a GUID string, or its <c>Name</c>); mutually exclusive with <see cref="ModelId" />.</summary>
    public string? SubAgentKey { get; init; }

    /// <summary>A model id to bind an ad-hoc sub-agent to directly; mutually exclusive with <see cref="SubAgentKey" />.</summary>
    public string? ModelId { get; init; }

    /// <summary>The task seeded into the sub-agent's run (the user-turn the inner agent answers). Required.</summary>
    public string Task { get; init; } = string.Empty;

    /// <summary>Optional system-prompt override for an ad-hoc (<see cref="ModelId" />-bound) sub-agent; ignored when a definition is resolved.</summary>
    public string? Instructions { get; init; }
}
