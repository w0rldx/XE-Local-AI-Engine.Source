namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A versioned workflow artifact. Deliberately a separate table from <c>agent_work_session_artifacts</c>, whose
///     unique <c>(session, name)</c> index means a same-named save <em>replaces</em> — versioning is precisely the
///     semantic that entity does not have. The bytes never reach a column; they live encrypted on disk.
/// </summary>
internal sealed class DevWorkflowArtifact
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }

    /// <summary>Stable across every version of one logical artifact. Uses and staleness propagation key on this.</summary>
    public Guid LineageId { get; set; }

    /// <summary>
    ///     Part of the lineage identity <c>(run, producing node key, name)</c>, denormalized from the producing node-run
    ///     so lineage resolution is one indexed read. Keying on <c>(run, name)</c> alone would be wrong and quietly so:
    ///     materialized siblings share one template and emit artifacts under the same logical name, so sibling #2's
    ///     artifact would read back as version 2 of sibling #1's and mark unrelated consumers stale.
    /// </summary>
    public string ProducingNodeKey { get; set; } = string.Empty;

    public Guid ProducedByNodeRunId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Version { get; set; }
    public DevWorkflowArtifactKind Kind { get; set; }
    public string MediaType { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ManagedReference { get; set; } = string.Empty;
    public bool IsValid { get; set; }
    public bool IsStale { get; set; }
    public long? StaleSinceSequence { get; set; }

    /// <summary>The exact newer version that caused the mark, so "stale" reads as "stale because specification v2 landed" without parsing a reason string.</summary>
    public Guid? StaleBecauseArtifactId { get; set; }

    public string? StaleReason { get; set; }
    public long Sequence { get; set; }
    public long CreatedAtUtc { get; set; }
}
