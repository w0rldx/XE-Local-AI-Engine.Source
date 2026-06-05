namespace XE_Local_AI_Engine.Client.Services.Agents;

using System.Text.Json;

/// <summary>
///     Canonical v1 shape of an orchestrator definition's <c>OrchestrationTopologyJson</c> column (orchestration). It
///     references existing node-local <c>AgentDefinition</c> ids — the orchestrator definition is the triage by
///     default, and <see cref="ParticipantAgentDefinitionIds" /> are the specialist agents it can hand off to. The
///     resolver compiles this into the MAF-agnostic orchestration spec; the management UI round-trips it verbatim.
///     This record is the PINNED contract shared by the resolver (worker-side compile), the management service
///     (validation), and the React topology editor (authoring). Adding fields is a forward-compatible change only if
///     <see cref="Version" /> is bumped and the parser stays tolerant of unknown versions.
/// </summary>
public sealed record OrchestrationTopology
{
    /// <summary>Schema version. v1 is the only shape the resolver understands; any other value is treated as "no topology".</summary>
    public int Version { get; init; }

    /// <summary>
    ///     The triage/coordinator agent definition id. MUST be a member of <see cref="ParticipantAgentDefinitionIds" />.
    ///     Defaults to the orchestrator definition itself in the authoring UI, but is carried explicitly for round-trip.
    /// </summary>
    public Guid TriageAgentDefinitionId { get; init; }

    /// <summary>The agent definition ids that participate in the orchestration (includes the triage id).</summary>
    public IReadOnlyList<Guid> ParticipantAgentDefinitionIds { get; init; } = [];

    /// <summary>
    ///     Explicit handoff edges. An empty list means "mesh default" — MAF auto-wires every participant to every
    ///     other so any agent can hand off to any other.
    /// </summary>
    public IReadOnlyList<OrchestrationHandoff> Handoffs { get; init; } = [];

    /// <summary>Per-agent autonomous-turn cap (depth/loop guard). Non-positive values fall back to the resolver default.</summary>
    public int MaxTurnsPerAgent { get; init; }

    /// <summary>When true, subsequent user turns route back to the specialist that handled the previous turn instead of re-entering via triage.</summary>
    public bool ReturnToPrevious { get; init; }
}

/// <summary>A single directed handoff edge between two participant agent definitions, with an optional routing reason.</summary>
public sealed record OrchestrationHandoff
{
    public Guid FromAgentDefinitionId { get; init; }

    public Guid ToAgentDefinitionId { get; init; }

    /// <summary>Optional human/model-facing reason MAF uses to describe when to take this handoff. Null falls back to the target's description/name.</summary>
    public string? Reason { get; init; }
}

/// <summary>
///     Tolerant parse/serialize helpers for <see cref="OrchestrationTopology" />. A single cached
///     <see cref="JsonSerializerOptions" /> (web defaults, camelCase) is reused so the JSON shape stays stable across
///     the resolver, the management service, and any test fixtures (CA1869). Parsing NEVER throws: a null/blank/invalid
///     payload returns <c>null</c> so the caller degrades to the single-agent path rather than failing the turn.
/// </summary>
public static class OrchestrationTopologyJson
{
    /// <summary>The only schema version this build understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    ///     Hard caps on topology size enforced at parse time so a hand-edited (or hostile) column cannot fan out into an
    ///     unbounded per-turn DB lookup or an unbounded handoff graph. An oversized topology fails closed: it parses to
    ///     <c>null</c> (the resolver degrades to single-agent; the management service rejects it as "malformed").
    /// </summary>
    public const int MaxParticipants = 64;

    public const int MaxHandoffs = 512;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // Match the authoring/round-trip contract: camelCase property names, tolerant of trailing commas/comments the
        // UI never emits but a hand-edited column might, and we ignore unknown members so an additive v1 change on one
        // side does not break the other. MaxDepth is pinned low (defensive): the schema is shallow, so a deeply nested
        // payload is malformed and is rejected during deserialization rather than driving unbounded recursion.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 8
    };

    /// <summary>
    ///     Parses the stored topology JSON. Returns <c>null</c> when the payload is null/blank, malformed, or carries a
    ///     version this build does not understand — the caller treats that as "no topology" and degrades to single-agent.
    /// </summary>
    public static OrchestrationTopology? TryParse(string? topologyJson)
    {
        if (string.IsNullOrWhiteSpace(topologyJson))
        {
            return null;
        }

        OrchestrationTopology? topology;
        try
        {
            topology = JsonSerializer.Deserialize<OrchestrationTopology>(topologyJson, Options);
        }
        catch (JsonException)
        {
            return null;
        }

        if (topology is null || topology.Version != CurrentVersion)
        {
            return null;
        }

        // Fail closed on an oversized topology: an unbounded participant list would fan out into one DB lookup per id
        // per turn, and an unbounded handoff list into an arbitrarily large graph. Treat either as malformed (null).
        if (topology.ParticipantAgentDefinitionIds.Count > MaxParticipants || topology.Handoffs.Count > MaxHandoffs)
        {
            return null;
        }

        return topology;
    }

    /// <summary>Serializes a topology to its canonical JSON shape (used by validation/tests; the UI authors the same shape).</summary>
    public static string Serialize(OrchestrationTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        return JsonSerializer.Serialize(topology, Options);
    }
}
