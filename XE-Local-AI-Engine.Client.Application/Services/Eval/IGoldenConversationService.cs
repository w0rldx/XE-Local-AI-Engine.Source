namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer orchestration over <see cref="IGoldenConversationStore" /> for the manually authored golden
///     set plus harvested-candidate staging.
/// </summary>
/// <remarks>
///     It validates the supplied fields — a non-blank title, an existing owning agent, non-empty input turns and at
///     least one of assertion or rubric — then delegates persistence. The manual path pins
///     <see cref="GoldenConversationSource.Manual" />; the harvested path pins
///     <see cref="GoldenConversationSource.Harvested" /> and stages the case inert until the operator approves it.
///     Delete is ownership-guarded, so one agent's route cannot touch another's golden case.
/// </remarks>
public interface IGoldenConversationService
{
    /// <summary>
    ///     Validates and persists a new <see cref="GoldenConversationSource.Manual" /> golden case, returning the stored
    ///     record. A manual create never produces harvested provenance: the source is forced to Manual regardless of the
    ///     input.
    /// </summary>
    Task<GoldenConversationRecord> CreateAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and persists a new <see cref="GoldenConversationSource.Harvested" /> candidate, staged inert
    ///     whatever the input's Enabled flag says.
    /// </summary>
    /// <remarks>
    ///     The rules are <see cref="CreateAsync" />'s plus non-null provenance ids, and the operator promotes the
    ///     result into the active set through <see cref="ApproveHarvestedAsync" />.
    /// </remarks>
    Task<GoldenConversationRecord> CreateHarvestedAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns every golden case for <paramref name="agentDefinitionId" />, ordered by CreatedAtUtc.</summary>
    Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Promotes a staged harvested candidate into the active golden set, enabling <paramref name="id" /> only
    ///     when it belongs to <paramref name="agentDefinitionId" />, is harvested and is currently disabled.
    /// </summary>
    /// <remarks>
    ///     It returns the updated record, or <c>null</c> when no such case exists, which the endpoint maps to 404 —
    ///     the same ownership guard the manual-authoring and analysis-review paths use.
    /// </remarks>
    Task<GoldenConversationRecord?> ApproveHarvestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the golden case with <paramref name="id" /> only when it belongs to the agent named on the route,
    ///     returning whether a row was deleted.
    /// </summary>
    /// <remarks>It is <c>false</c> for an unknown id and for one owned by a different agent alike.</remarks>
    Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Mutable fields of a golden case supplied on create.
/// </summary>
/// <remarks>
///     <see cref="Enabled" /> defaults to <c>true</c>, so a manual case joins the next eval run unless the operator
///     parks it, while the harvested path forces it inert. <see cref="Source" />,
///     <see cref="SourceMessageId" /> and <see cref="SourceConversationId" /> carry harvest provenance and default to
///     a Manual case with none.
/// </remarks>
public sealed class GoldenConversationCreateInput
{
    public required Guid AgentDefinitionId { get; init; }

    public required string Title { get; init; }

    public required string InputTurns { get; init; }

    public required string? Assertion { get; init; }

    public required string? Rubric { get; init; }

    public bool Enabled { get; init; } = true;

    public GoldenConversationSource Source { get; init; }

    public Guid? SourceMessageId { get; init; }

    public Guid? SourceConversationId { get; init; }
}
