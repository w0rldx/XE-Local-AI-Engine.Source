namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     CRUD + validation over the persisted preview-workflow library. Serializes the <see cref="PreviewWorkflowGraph" />
///     to the encrypted <c>GraphJson</c> blob on write and deserializes it on read (Lane A owns encryption-at-rest).
///     Validation (<see cref="PreviewWorkflowGraphValidator" />) runs before every create/update so an invalid graph
///     never reaches storage; the execution service re-validates before a run.
/// </summary>
public interface IPreviewWorkflowService
{
    /// <summary>Lists workflow summaries (id/name/version/timestamps; no graph), oldest first.</summary>
    Task<IReadOnlyList<PreviewWorkflowSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the full workflow (graph deserialized) for <paramref name="id" />, or null when not found.</summary>
    Task<PreviewWorkflowDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and persists a new workflow. Returns the created detail on success or the validation errors on
    ///     failure (the endpoint surfaces those as a 400).
    /// </summary>
    Task<PreviewWorkflowMutationResult> CreateAsync(string name, PreviewWorkflowGraph graph, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Validates and applies an optimistic-concurrency update. Returns <see cref="PreviewWorkflowMutationOutcome.Updated" />,
    ///     <see cref="PreviewWorkflowMutationOutcome.NotFound" />, <see cref="PreviewWorkflowMutationOutcome.Conflict" />
    ///     (stale version → 409), or <see cref="PreviewWorkflowMutationOutcome.Invalid" /> (→ 400 with errors).
    /// </summary>
    Task<PreviewWorkflowMutationResult> UpdateAsync(Guid id, int expectedVersion, string name, PreviewWorkflowGraph graph, CancellationToken cancellationToken = default);

    /// <summary>Deletes the workflow with <paramref name="id" />. Returns true when a row was removed.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Workflow list-row projection — no graph (the encrypted blob is never loaded for a list).</summary>
public sealed record PreviewWorkflowSummary(Guid Id, string Name, int Version, long CreatedAtUtc, long UpdatedAtUtc);

/// <summary>Full workflow including the deserialized graph.</summary>
public sealed record PreviewWorkflowDetail(Guid Id, string Name, PreviewWorkflowGraph Graph, int Version, long CreatedAtUtc, long UpdatedAtUtc);

/// <summary>Discriminates a create/update outcome.</summary>
public enum PreviewWorkflowMutationOutcome
{
    Created = 0,
    Updated = 1,
    NotFound = 2,
    Conflict = 3,
    Invalid = 4
}

/// <summary>
///     Result of a create/update. <see cref="Detail" /> is populated on Created/Updated; <see cref="Validation" /> on
///     Invalid.
/// </summary>
public sealed record PreviewWorkflowMutationResult(
    PreviewWorkflowMutationOutcome Outcome,
    PreviewWorkflowDetail? Detail,
    PreviewWorkflowValidationResult? Validation)
{
    public static PreviewWorkflowMutationResult Created(PreviewWorkflowDetail detail) =>
        new(PreviewWorkflowMutationOutcome.Created, detail, Validation: null);

    public static PreviewWorkflowMutationResult Updated(PreviewWorkflowDetail detail) =>
        new(PreviewWorkflowMutationOutcome.Updated, detail, Validation: null);

    public static PreviewWorkflowMutationResult NotFound() =>
        new(PreviewWorkflowMutationOutcome.NotFound, Detail: null, Validation: null);

    public static PreviewWorkflowMutationResult Conflict() =>
        new(PreviewWorkflowMutationOutcome.Conflict, Detail: null, Validation: null);

    public static PreviewWorkflowMutationResult Invalid(PreviewWorkflowValidationResult validation) =>
        new(PreviewWorkflowMutationOutcome.Invalid, Detail: null, validation);
}
