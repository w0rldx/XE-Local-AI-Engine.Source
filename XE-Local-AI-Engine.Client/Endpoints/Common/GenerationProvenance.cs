namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using XE_Local_AI_Engine.Client.Services.Drafting;

/// <summary>The one place the wire provenance block is bounded, stamped and (de)serialized.</summary>
/// <remarks>
///     Every create/update endpoint that accepts an echoed <see cref="GenerationMetadata" /> runs
///     <see cref="Validate" /> first: the block is operator input like any other, so it is capped at the boundary
///     rather than trusted (invariant 7).
/// </remarks>
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
    ///     Stamps the two server-computed fields onto the echoed block and renders the persisted JSON object for the
    ///     encrypted <c>GenerationMetadataJson</c> column.
    /// </summary>
    /// <returns>
    ///     The persisted JSON, or <c>null</c> when no block was echoed — which the stores read as "leave the stored
    ///     provenance alone".
    /// </returns>
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
        new()
        {
            Model = metadata.Model,
            Mode = metadata.Mode,
            UserBrief = metadata.UserBrief,
            Rationale = metadata.Rationale,
            Assumptions = metadata.Assumptions,
            Confidence = metadata.Confidence,
            GeneratedAtUtc = metadata.GeneratedAtUtc,
            DraftContentHash = metadata.DraftContentHash
        };
}
