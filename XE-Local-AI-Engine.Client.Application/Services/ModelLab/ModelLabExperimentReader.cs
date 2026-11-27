namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Blobs;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The saved experiments and their artifacts as the catalog and the result tabs read them, and the two catalog fields an operator edits.</summary>
/// <remarks>Holds no job state and takes no lock: what a running job owns, and a delete, stay on <see cref="ModelLabExperimentService" />.</remarks>
public sealed class ModelLabExperimentReader
{
    private readonly ManagedEncryptedBlobStore _blobs;
    private readonly IServiceScopeFactory _scopeFactory;

    public ModelLabExperimentReader(IServiceScopeFactory scopeFactory, INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _blobs = NewBlobStore(dataDirectory, keyHolder);
        _scopeFactory = scopeFactory;
    }

    /// <summary>The blob convention every experiment artifact is written and read under.</summary>
    internal static ManagedEncryptedBlobStore NewBlobStore(INodeDataDirectory dataDirectory, INodeSqliteKeyHolder keyHolder) =>
        new(dataDirectory, keyHolder, "model-lab", "artifacts", "model_lab_artifact_blob",
            Math.Max(ModelLabTraceSpec.MaxTraceByteCap, ModelLabTraceSpec.MaxAttentionByteCap), "Model Lab artifact");

    public async Task<IReadOnlyList<ModelLabExperimentRecord>> ListAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().ListAsync(ct);
    }

    public async Task<ModelLabExperimentRecord?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().GetAsync(id, ct);
    }

    public async Task<bool> ExistsAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().ExistsAsync(id, ct);
    }

    public async Task<ModelLabExperimentUpdateOutcome> SetNotesAsync(Guid id, string? notes, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetNotesAsync(id, notes, ct);
    }

    public async Task<ModelLabExperimentUpdateOutcome> SetPinnedAsync(Guid id, bool pinned, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetPinnedAsync(id, pinned, ModelLabExperimentService.MaxPinnedExperiments, ct);
    }

    public async Task<ModelLabTrace> ReadTraceAsync(Guid id, CancellationToken ct)
    {
        var record = await GetAsync(id, ct);
        // A steering row's blob is its vector, which has no route out of this node.
        if (record is not { Kind: ModelLabExperimentKind.Trace, TraceBlobId: { } blobId, TraceSha256: { } hash, TraceByteCount: { } bytes })
        {
            return new ModelLabTrace
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        return read.Status == ManagedBlobReadStatus.Found
            ? ModelLabTraceSpec.ParseTrace(read.Content)
            : new ModelLabTrace
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
    }

    /// <summary>One captured block's rows. Not found for a steering row, a trace that recorded no attention, or a block it did not capture.</summary>
    /// <remarks>
    ///     Not a <see cref="ReadTraceSectionAsync{TSummary,T}" /> call: it needs no token count, a blob without a usable summary is
    ///     unavailable rather than not found, and an uncaptured block is answered before the blob is read.
    /// </remarks>
    public async Task<ModelLabAttention> ReadAttentionAsync(Guid id, int layer, CancellationToken ct)
    {
        var record = await GetAsync(id, ct);
        if (record is not { Kind: ModelLabExperimentKind.Trace, AttentionBlobId: { } blobId, AttentionSha256: { } hash, AttentionByteCount: { } bytes })
        {
            return new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        // The blob's shape is the result's: without a readable summary its rows cannot be decoded.
        if ((record.ResultJson is { } json ? ModelLabTraceSpec.ReadResultOrNull(json)?.Attention : null) is not { Heads: > 0, WeightScale: > 0 } summary)
        {
            return new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        }

        if (!summary.Layers.Contains(layer))
        {
            return new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.NotFound
            };
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        var attention = read.Status == ManagedBlobReadStatus.Found
            ? ModelLabTraceSpec.ParseAttention(read.Content, summary.Heads)
            : new ModelLabAttention
            {
                Status = ModelLabTraceReadStatus.Unavailable
            };
        return attention.Status == ModelLabTraceReadStatus.Found
            ? attention with
            {
                Summary = summary,
                Records = [.. attention.Records.Where(row => row.Layer == layer)]
            }
            : attention;
    }

    /// <summary>The trace's logit lens. Not found for a steering row or a trace that recorded none: one saved before contract 5, or of a checkpoint too deep for it.</summary>
    public Task<ModelLabLens> ReadLensAsync(Guid id, CancellationToken ct) =>
        ReadTraceSectionAsync(id,
            static result => result.Lens,
            static (content, summary, result) => ModelLabTraceSpec.ParseLens(content, summary, result.Tokens.Count),
            static status => new ModelLabLens
            {
                Status = status
            },
            ct);

    /// <summary>The trace's router choices. Not found for a steering row or a trace that recorded none: one saved before contract 8, or of a model with no router.</summary>
    public Task<ModelLabRouting> ReadRoutingAsync(Guid id, CancellationToken ct) =>
        ReadTraceSectionAsync(id,
            static result => result.Routing,
            static (content, summary, result) => ModelLabTraceSpec.ParseRouting(content, summary, result.BlockCount, result.Tokens.Count),
            static status => new ModelLabRouting
            {
                Status = status
            },
            ct);

    /// <summary>A section of the trace blob named by <paramref name="summaryOf" />: not found when the run recorded none, unavailable when it cannot be read back.</summary>
    private async Task<T> ReadTraceSectionAsync<TSummary, T>(Guid id,
        Func<ModelLabTraceResult, TSummary?> summaryOf,
        Func<ReadOnlyMemory<byte>, TSummary, ModelLabTraceResult, T> parse,
        Func<ModelLabTraceReadStatus, T> withStatus,
        CancellationToken ct)
        where TSummary : class
    {
        var record = await GetAsync(id, ct);
        if (record is not { Kind: ModelLabExperimentKind.Trace, TraceBlobId: { } blobId, TraceSha256: { } hash, TraceByteCount: { } bytes })
        {
            return withStatus(ModelLabTraceReadStatus.NotFound);
        }

        // The lines are checked against the run's own summary and token count, so without a readable result they cannot be;
        // a stored "tokens": null deserializes, and is no count either.
        if ((record.ResultJson is { } json ? ModelLabTraceSpec.ReadResultOrNull(json) : null) is not { Tokens: not null } result)
        {
            return withStatus(ModelLabTraceReadStatus.Unavailable);
        }

        if (summaryOf(result) is not { } summary)
        {
            return withStatus(ModelLabTraceReadStatus.NotFound);
        }

        var read = await _blobs.ReadAsync(id, blobId, hash, bytes, ct);
        return read.Status == ManagedBlobReadStatus.Found ? parse(read.Content, summary, result) : withStatus(ModelLabTraceReadStatus.Unavailable);
    }
}
