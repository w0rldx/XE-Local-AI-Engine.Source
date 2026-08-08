namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows.Implementation;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Default <see cref="IPreviewWorkflowService" />: CRUD over <see cref="ICanvasWorkflowStore" /> with graph
///     validation on every write and JSON (de)serialization between the Client graph model and the encrypted
///     <c>GraphJson</c> blob.
/// </summary>
internal sealed class PreviewWorkflowService(ICanvasWorkflowStore store) : IPreviewWorkflowService
{
    private readonly ICanvasWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<PreviewWorkflowSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        var records = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. records.Select(static r => new PreviewWorkflowSummary(r.Id, r.Name, r.Version, r.CreatedAtUtc, r.UpdatedAtUtc))];
    }

    public async Task<PreviewWorkflowDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var record = await _store.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var graph = PreviewWorkflowGraphMapper.Deserialize(record.GraphJson ?? string.Empty);
        return new PreviewWorkflowDetail(record.Id, record.Name, graph, record.Version, record.CreatedAtUtc, record.UpdatedAtUtc);
    }

    public async Task<PreviewWorkflowMutationResult> CreateAsync(string name, PreviewWorkflowGraph graph, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(graph);

        var validation = PreviewWorkflowGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            return PreviewWorkflowMutationResult.Invalid(validation);
        }

        var graphJson = PreviewWorkflowGraphMapper.Serialize(graph);
        var record = await _store.AddAsync(new CanvasWorkflowInput(name, graphJson), cancellationToken).ConfigureAwait(false);

        return PreviewWorkflowMutationResult.Created(ToDetail(record));
    }

    public async Task<PreviewWorkflowMutationResult> UpdateAsync(Guid id,
        int expectedVersion,
        string name,
        PreviewWorkflowGraph graph,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(graph);

        var validation = PreviewWorkflowGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            return PreviewWorkflowMutationResult.Invalid(validation);
        }

        var graphJson = PreviewWorkflowGraphMapper.Serialize(graph);
        var result = await _store.UpdateAsync(id, expectedVersion, new CanvasWorkflowInput(name, graphJson), cancellationToken)
                                 .ConfigureAwait(false);

        return result.Outcome switch
        {
            CanvasWorkflowUpdateOutcome.Updated => PreviewWorkflowMutationResult.Updated(ToDetail(result.Record!)),
            CanvasWorkflowUpdateOutcome.NotFound => PreviewWorkflowMutationResult.NotFound(),
            CanvasWorkflowUpdateOutcome.Conflict => PreviewWorkflowMutationResult.Conflict(),
            _ => throw new InvalidOperationException($"Unhandled canvas workflow update outcome '{result.Outcome}'.")
        };
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return _store.DeleteAsync(id, cancellationToken);
    }

    private static PreviewWorkflowDetail ToDetail(CanvasWorkflowRecord record)
    {
        var graph = PreviewWorkflowGraphMapper.Deserialize(record.GraphJson ?? string.Empty);
        return new PreviewWorkflowDetail(record.Id, record.Name, graph, record.Version, record.CreatedAtUtc, record.UpdatedAtUtc);
    }
}
