namespace XE_Local_AI_Engine.Client.Models;

/// <summary>
///     The compiled, MAF-agnostic orchestration spec carried on the loopback <see cref="RuntimePackage" /> when a
///     conversation is bound to a <c>Kind=Orchestrator</c> definition whose effective model is tool-capable (loop P5).
///     It is OPTIONAL: <c>null</c> on the single-agent loopback path and on the encrypted/server path, where the
///     config hash stays byte-identical to today. The orchestration resolver produces it from a topology + the
///     per-participant P3 tool projection; the invocation factory compiles it 1:1 into the workflow participants, and
///     <c>RuntimePackageConfigHash</c> folds it deterministically so a topology/participant edit invalidates resume.
/// </summary>
public sealed record OrchestrationSpec
{
    /// <summary>The triage/coordinator participant's stable key (must match a member of <see cref="Participants" />).</summary>
    public required string TriageParticipantKey { get; init; }

    /// <summary>Every participant (including triage). Order is NOT significant — the hash sorts by <see cref="OrchestrationSpecParticipant.Key" />.</summary>
    public required IReadOnlyList<OrchestrationSpecParticipant> Participants { get; init; }

    /// <summary>Explicit handoff edges; empty means the mesh default (every participant can hand off to every other).</summary>
    public required IReadOnlyList<OrchestrationSpecEdge> Edges { get; init; }

    /// <summary>Per-agent autonomous-turn cap (depth/loop guard).</summary>
    public required int MaxTurnsPerAgent { get; init; }

    /// <summary>When true, subsequent user turns route back to the specialist that handled the previous turn.</summary>
    public required bool ReturnToPrevious { get; init; }
}

/// <summary>
///     One participant of a compiled orchestration. Maps 1:1 onto a workflow agent: its prompt, model, reasoning, and
///     the per-participant capability-gated, approval-flagged tool projection (same P3 contract as the single-agent
///     path). <see cref="Key" /> is the stable correlation id (the participant's agent-definition id as a string).
/// </summary>
public sealed record OrchestrationSpecParticipant
{
    public required string Key { get; init; }

    public required string Name { get; init; }

    /// <summary>The handoff-routing signal MAF surfaces to the model (the definition's Description, may be null/empty).</summary>
    public string? Description { get; init; }

    public required string Instructions { get; init; }

    public string? ModelId { get; init; }

    public string? ReasoningEffort { get; init; }

    /// <summary>The participant's projected tool offer (capability-gated ∩ AllowedToolNames, approval-overridden).</summary>
    public required IReadOnlyList<AllowedToolDto> Tools { get; init; }
}

/// <summary>A directed handoff edge between two participant keys, with an optional routing reason.</summary>
public sealed record OrchestrationSpecEdge
{
    public required string FromKey { get; init; }

    public required string ToKey { get; init; }

    public string? Reason { get; init; }
}
