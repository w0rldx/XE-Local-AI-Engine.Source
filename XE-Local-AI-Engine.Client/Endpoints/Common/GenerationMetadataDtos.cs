namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>
///     AI-drafting provenance as it travels on the wire: the draft endpoints return this block alongside the drafted
///     fields, and the client echoes it back <em>opaquely</em> on the create/update request that saves the draft.
///     <para>
///         <b>This is informational, not an attestation.</b> Every field here is client-supplied on the save path, and
///         this is a single-operator local node — so an operator can trivially forge it against themselves. It records
///         what a draft claimed, and nothing downstream grants trust or capability based on it (locked decision 9: no
///         signed receipts). The two fields a reader can rely on are the server-stamped <c>acceptedAtUtc</c> and
///         <c>wasEdited</c> on <see cref="GenerationMetadataResponse" />.
///     </para>
/// </summary>
public sealed class GenerationMetadata
{
    /// <summary>The node-local model that produced the draft, as the operator selected it.</summary>
    public string? Model { get; init; }

    /// <summary>Whether the draft was written from scratch or revised from existing content.</summary>
    public DraftMode Mode { get; init; }

    /// <summary>The operator's description of what they wanted — the brief the draft was generated from.</summary>
    public string? UserBrief { get; init; }

    /// <summary>The model's own explanation of its drafting choices.</summary>
    public string? Rationale { get; init; }

    /// <summary>Short assumptions the model had to make; empty when it made none.</summary>
    public IReadOnlyList<string>? Assumptions { get; init; }

    /// <summary>The model's self-reported confidence in [0,1]. Model-asserted, so treated as a display value only.</summary>
    public double Confidence { get; init; }

    /// <summary>When the draft was generated, stamped server-side at draft time (Unix ms).</summary>
    public long GeneratedAtUtc { get; init; }

    /// <summary>
    ///     Server-computed hash over the drafted name/description/content at draft time. The save path recomputes it
    ///     over what was actually submitted to derive <see cref="GenerationMetadataResponse.WasEdited" />.
    /// </summary>
    public string? DraftContentHash { get; init; }
}

/// <summary>
///     Stored provenance as the single-item reads return it: everything the client echoed, plus the two fields the
///     server computes at save time. Carried on the full skill and single-agent projections only — list projections
///     leave it out/null so the library lists stay lean.
/// </summary>
public sealed class GenerationMetadataResponse
{
    public string? Model { get; init; }

    public DraftMode Mode { get; init; }

    public string? UserBrief { get; init; }

    public string? Rationale { get; init; }

    public IReadOnlyList<string>? Assumptions { get; init; }

    public double Confidence { get; init; }

    public long GeneratedAtUtc { get; init; }

    public string? DraftContentHash { get; init; }

    /// <summary>When the operator saved the draft, stamped server-side at save time (Unix ms).</summary>
    public long AcceptedAtUtc { get; init; }

    /// <summary>
    ///     Whether the saved content differs from what the model drafted, computed server-side by recomputing
    ///     <see cref="GenerationMetadata.DraftContentHash" /> over the submitted fields. Line-ending-insensitive, so a
    ///     browser textarea handing LF content back as CRLF does not read as an edit.
    /// </summary>
    public bool WasEdited { get; init; }
}

/// <summary>
///     The one place the wire provenance block is bounded, stamped and (de)serialized. Every create/update endpoint
///     that accepts an echoed <see cref="GenerationMetadata" /> runs <see cref="Validate" /> first — the block is
///     operator input like any other, so it is capped at the boundary rather than trusted (invariant 7).
/// </summary>
internal static class GenerationProvenance
{
    /// <summary>
    ///     Returns an operator-facing message when the echoed block breaches a cap, or <c>null</c> when it is absent or
    ///     within bounds.
    /// </summary>
    public static string? Validate(GenerationMetadata? metadata)
    {
        return Services.Drafting.GenerationProvenance.Validate(metadata?.ToInput());
    }

    /// <summary>
    ///     Stamps the two server-computed fields onto the echoed block and renders the plan §5.1 JSON object for the
    ///     encrypted <c>GenerationMetadataJson</c> column. Returns <c>null</c> when no block was echoed, which the
    ///     stores read as "leave the stored provenance alone".
    /// </summary>
    public static string? ToPersistedJson(GenerationMetadata? metadata,
        string? savedName,
        string? savedDescription,
        string? savedContent,
        DateTimeOffset acceptedAt)
    {
        return Services.Drafting.GenerationProvenance.ToPersistedJson(metadata?.ToInput(),
            savedName,
            savedDescription,
            savedContent,
            acceptedAt);
    }

    /// <summary>
    ///     Projects the stored column onto the wire. A row whose JSON cannot be read degrades to <c>null</c> rather
    ///     than failing the read: provenance is informational, and an unreadable block must not take the skill or agent
    ///     it decorates offline.
    /// </summary>
    public static GenerationMetadataResponse? FromPersistedJson(string? json)
    {
        var persisted = Services.Drafting.GenerationProvenance.FromPersistedJson(json);
        return persisted is null
            ? null
            : new GenerationMetadataResponse
            {
                Model = persisted.Model,
                Mode = persisted.Mode,
                UserBrief = persisted.UserBrief,
                Rationale = persisted.Rationale,
                Assumptions = persisted.Assumptions,
                Confidence = persisted.Confidence,
                GeneratedAtUtc = persisted.GeneratedAtUtc,
                DraftContentHash = persisted.DraftContentHash,
                AcceptedAtUtc = persisted.AcceptedAtUtc,
                WasEdited = persisted.WasEdited
            };
    }

    private static GenerationMetadataInput ToInput(this GenerationMetadata metadata) =>
        new(metadata.Model,
            metadata.Mode,
            metadata.UserBrief,
            metadata.Rationale,
            metadata.Assumptions,
            metadata.Confidence,
            metadata.GeneratedAtUtc,
            metadata.DraftContentHash);
}
