namespace XE_Local_AI_Engine.Client.Services.Agents;

using System.Text.Json;

/// <summary>
///     Canonical v1 shape of an orchestrator definition's <c>OrchestrationTopologyJson</c> column.
/// </summary>
/// <remarks>
///     It references node-local <c>AgentDefinition</c> ids: the orchestrator is the triage by default and
///     <see cref="ParticipantAgentDefinitionIds" /> are the specialists it can hand off to. The PINNED contract
///     shared by the resolver's compile, the management service's validation and the React topology editor, so
///     adding a field is forward-compatible only with a <see cref="Version" /> bump and a parser that stays tolerant
///     of unknown versions.
/// </remarks>
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
///     Tolerant parse and serialize helpers for <see cref="OrchestrationTopology" />.
/// </summary>
/// <remarks>
///     One cached <see cref="JsonSerializerOptions" /> (web defaults, camelCase) is reused so the JSON shape stays
///     stable across the resolver, the management service and any test fixtures (CA1869). Parsing NEVER throws: a
///     null, blank or invalid payload answers <c>null</c>, degrading the caller to the single-agent path.
/// </remarks>
public static class OrchestrationTopologyJson
{
    /// <summary>The only schema version this build understands.</summary>
    public const int CurrentVersion = 1;

    /// <summary>
    ///     Hard caps on topology size, enforced at parse time so a hand-edited or hostile column cannot fan out into
    ///     an unbounded per-turn DB lookup or handoff graph.
    /// </summary>
    /// <remarks>
    ///     An oversized topology fails closed by parsing to <c>null</c>: the resolver degrades to single-agent and
    ///     the management service rejects it as malformed.
    /// </remarks>
    public const int MaxParticipants = 64;

    public const int MaxHandoffs = 512;

    /// <summary>
    ///     Server-side ceiling on <see cref="OrchestrationTopology.MaxTurnsPerAgent" />, the per-agent autonomous-turn
    ///     loop guard, matching the authoring UI's cap.
    /// </summary>
    /// <remarks>
    ///     A stored value above it would fan an agent into an arbitrarily long autonomous loop per turn, so it fails
    ///     closed like an oversized participant or handoff list. A non-positive value is not over the cap: it means
    ///     "use the resolver default".
    /// </remarks>
    public const int MaxTurnsPerAgentCeiling = 64;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        // The authoring round-trip contract: camelCase, tolerant of the trailing commas and comments a hand-edited
        // column might carry, unknown members ignored. MaxDepth is pinned low — the schema is shallow, so deep is malformed.
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

        // Fail closed on an oversized topology: unbounded participants mean a DB lookup per id per turn, unbounded
        // handoffs an arbitrarily large graph, an unbounded turn cap an endless loop. All three are malformed.
        if (topology.ParticipantAgentDefinitionIds.Count > MaxParticipants
            || topology.Handoffs.Count > MaxHandoffs
            || topology.MaxTurnsPerAgent > MaxTurnsPerAgentCeiling)
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
