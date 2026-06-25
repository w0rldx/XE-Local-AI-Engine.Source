namespace XE_Local_AI_Engine.Client.Services.Eval;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Application-layer orchestration over <see cref="IGoldenConversationStore" /> for the golden set (manual
///     authoring) plus harvested-candidate staging. Validates the supplied fields (non-blank
///     Title, existing owning agent, non-empty InputTurns, at least one of {Assertion, Rubric}) and delegates
///     persistence to the store. The manual create path pins <see cref="GoldenConversationSource.Manual" />; the
///     harvested create path pins <see cref="GoldenConversationSource.Harvested" /> and stages the case inert
///     (<c>Enabled == false</c>) until the operator approves it. Delete is ownership-guarded so one agent's route cannot
///     touch another agent's golden case.
/// </summary>
public interface IGoldenConversationService
{
    /// <summary>
    ///     Validates and persists a new <see cref="GoldenConversationSource.Manual" /> golden case, returning the stored
    ///     record. A manual create never produces harvested provenance: the source is forced to Manual regardless of the
    ///     input.
    /// </summary>
    Task<GoldenConversationRecord> CreateAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates (the same rules as <see cref="CreateAsync" />, plus non-null provenance ids) and persists a new
    ///     <see cref="GoldenConversationSource.Harvested" /> golden candidate staged inert (<c>Enabled == false</c>)
    ///     regardless of the input's Enabled flag, returning the stored record. The operator promotes it into the active
    ///     set via <see cref="ApproveHarvestedAsync" />.
    /// </summary>
    Task<GoldenConversationRecord> CreateHarvestedAsync(GoldenConversationCreateInput input, CancellationToken cancellationToken = default);

    /// <summary>Returns every golden case for <paramref name="agentDefinitionId" />, ordered by CreatedAtUtc.</summary>
    Task<IReadOnlyList<GoldenConversationRecord>> ListByAgentAsync(Guid agentDefinitionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Promotes a staged harvested candidate into the active golden set: enables the case with <paramref name="id" />
    ///     only when it belongs to <paramref name="agentDefinitionId" />, is <see cref="GoldenConversationSource.Harvested" />
    ///     and currently disabled. Returns the updated record, or <c>null</c> when no such case exists (the endpoint maps
    ///     <c>null</c> to 404) — the same ownership guard as the manual-authoring and analysis-review paths.
    /// </summary>
    Task<GoldenConversationRecord?> ApproveHarvestedAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes the golden case with <paramref name="id" /> only when it belongs to <paramref name="agentDefinitionId" />
    ///     (the agent named on the route). Returns <c>true</c> when a row was deleted, <c>false</c> when no case has that
    ///     id or it belongs to a different agent — the same ownership guard as the manual-authoring and analysis-review paths.
    /// </summary>
    Task<bool> DeleteAsync(Guid agentDefinitionId, Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
///     Mutable fields of a golden case supplied on create. <see cref="Enabled" /> defaults to <c>true</c> so a manual
///     case participates in the next eval run unless the operator parks it (the harvested create path forces it inert).
///     <see cref="Source" />, <see cref="SourceMessageId" /> and <see cref="SourceConversationId" /> carry harvest
///     provenance; they default to a Manual case with no provenance so the manual create path keeps compiling unchanged.
/// </summary>
public sealed record GoldenConversationCreateInput(
    Guid AgentDefinitionId,
    string Title,
    string InputTurns,
    string? Assertion,
    string? Rubric,
    bool Enabled = true,
    GoldenConversationSource Source = GoldenConversationSource.Manual,
    Guid? SourceMessageId = null,
    Guid? SourceConversationId = null);
