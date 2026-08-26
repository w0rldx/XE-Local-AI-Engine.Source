namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The operator-facing task-item surface. Everything about identity — the index, the revision, the input hash and
///     the project's item-set hash — is decided by the store; this layer decodes the wire shape, applies the caps, and
///     refuses the kinds this build cannot yet execute.
/// </summary>
public interface IBenchmarkTaskItemService
{
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The project's items, materializing item 0 for a project created before task items existed. Reads only for
    ///     every project created since — those get their items in the same transaction as themselves.
    /// </summary>
    Task<IReadOnlyList<BenchmarkTaskItemRecord>> GetOrCreateItemsAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<BenchmarkTaskItemRecord> CreateAsync(Guid projectId, long expectedProjectVersion, BenchmarkTaskItemDraft draft, CancellationToken cancellationToken = default);

    Task<BenchmarkTaskItemRecord> UpdateAsync(Guid projectId,
        Guid itemId,
        long expectedVersion,
        BenchmarkTaskItemDraft draft,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid projectId, Guid itemId, long expectedVersion, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BenchmarkTaskItemRecord>> ReorderAsync(Guid projectId, IReadOnlyList<Guid> orderedItemIds, CancellationToken cancellationToken = default);
}

/// <summary>
///     One task item as an operator writes it. The index, revision and input hash are absent on purpose: a caller that
///     could name them could present an answer to an old question as an answer to the current one.
/// </summary>
/// <param name="VerifierConfig">
///     Per-criterion overrides of the judge policy's verifier config, keyed by criterion id. Carried opaquely here —
///     it can hold expected answers, which is why it is stored encrypted.
/// </param>
/// <param name="GeneratorConfig">The parameters a generator item expands into child cases. Null for a plain prompt.</param>
public sealed record BenchmarkTaskItemDraft(
    string Prompt,
    string? Kind = null,
    string? ReferenceAnswer = null,
    JsonElement? VerifierConfig = null,
    JsonElement? GeneratorConfig = null,
    bool CountsTowardScore = true);

public sealed class BenchmarkTaskItemService(IBenchmarkStore benchmarkStore) : IBenchmarkTaskItemService
{
    /// <summary>
    ///     The cap on LEAF items — the ones a freeze actually fans out over, so a generator's cases each count. Past
    ///     this a matrix stops being merely slow and becomes unschedulable: 20 items times a few quants times a few
    ///     repeats is already a night of GPU time.
    /// </summary>
    public const int MaxTaskItems = 20;

    private readonly IBenchmarkStore _benchmarkStore = benchmarkStore ?? throw new ArgumentNullException(nameof(benchmarkStore));

    public Task<IReadOnlyList<BenchmarkTaskItemRecord>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _benchmarkStore.ListTaskItemsAsync(projectId, cancellationToken);

    public Task<IReadOnlyList<BenchmarkTaskItemRecord>> GetOrCreateItemsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _benchmarkStore.GetOrCreateItemsAsync(projectId, cancellationToken);

    public async Task<BenchmarkTaskItemRecord> CreateAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkTaskItemDraft draft,
        CancellationToken cancellationToken = default)
    {
        var input = ToInput(draft);
        var existing = await _benchmarkStore.ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (existing.Count(static item => item.IsLeaf) >= MaxTaskItems)
        {
            throw new BenchmarkValidationException($"A benchmark project holds at most {MaxTaskItems} task items.");
        }

        return await _benchmarkStore.CreateTaskItemAsync(projectId, expectedProjectVersion, input, cancellationToken).ConfigureAwait(false);
    }

    public Task<BenchmarkTaskItemRecord> UpdateAsync(Guid projectId,
        Guid itemId,
        long expectedVersion,
        BenchmarkTaskItemDraft draft,
        CancellationToken cancellationToken = default) =>
        _benchmarkStore.UpdateTaskItemAsync(projectId, itemId, expectedVersion, ToInput(draft), cancellationToken);

    public Task DeleteAsync(Guid projectId, Guid itemId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _benchmarkStore.DeleteTaskItemAsync(projectId, itemId, expectedVersion, cancellationToken);

    public Task<IReadOnlyList<BenchmarkTaskItemRecord>> ReorderAsync(Guid projectId,
        IReadOnlyList<Guid> orderedItemIds,
        CancellationToken cancellationToken = default) =>
        _benchmarkStore.ReorderTaskItemsAsync(projectId, orderedItemIds, cancellationToken);

    /// <summary>The prompt of one item, decoded from the stored payload the same way the project's core task is.</summary>
    public static string DecodePrompt(ReadOnlySpan<byte> payload) =>
        BenchmarkProjectService.DecodeCoreTask(payload);

    /// <summary>An optional stored string payload, or null when the item carries none.</summary>
    public static string? DecodeOptional(ReadOnlyMemory<byte>? payload) =>
        payload is { } value && !value.IsEmpty ? BenchmarkProjectService.DecodeCoreTask(value.Span) : null;

    /// <summary>An opaque stored JSON payload, handed back to the caller unchanged.</summary>
    public static JsonElement? DecodeJson(ReadOnlyMemory<byte>? payload)
    {
        if (payload is not { } value || value.IsEmpty)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new BenchmarkValidationException($"The stored task-item payload is invalid: {exception.Message}");
        }
    }

    private static BenchmarkTaskItemInput ToInput(BenchmarkTaskItemDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Prompt))
        {
            throw new BenchmarkValidationException("A benchmark task item needs a prompt.");
        }

        // The kinds are already in the schema's CHECK and in the store's vocabulary, so the generator kinds are ready
        // to be written the moment something can execute them. Nothing can yet, and accepting an item this build would
        // never run is worse than refusing it while the operator is still looking at the form.
        var kind = string.IsNullOrWhiteSpace(draft.Kind) ? BenchmarkTaskItemKinds.Prompt : draft.Kind.Trim();
        if (!string.Equals(kind, BenchmarkTaskItemKinds.Prompt, StringComparison.Ordinal))
        {
            throw new BenchmarkValidationException($"Only '{BenchmarkTaskItemKinds.Prompt}' task items are supported.");
        }

        return new BenchmarkTaskItemInput(JsonSerializer.SerializeToUtf8Bytes(draft.Prompt),
            kind,
            string.IsNullOrWhiteSpace(draft.ReferenceAnswer)
                ? null
                : (ReadOnlyMemory<byte>?)JsonSerializer.SerializeToUtf8Bytes(draft.ReferenceAnswer.Trim()),
            Encode(draft.VerifierConfig),
            Encode(draft.GeneratorConfig),
            CountsTowardScore: draft.CountsTowardScore);
    }

    /// <remarks>
    ///     The cast is load-bearing. Without it the conditional's natural type is <c>byte[]?</c>, and a null array
    ///     converts to an EMPTY <see cref="ReadOnlyMemory{T}" /> rather than to a null nullable — so an omitted payload
    ///     would be written as a present-but-empty encrypted blob instead of NULL.
    /// </remarks>
    private static ReadOnlyMemory<byte>? Encode(JsonElement? value) =>
        value is { } element && element.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)
            ? (ReadOnlyMemory<byte>?)JsonSerializer.SerializeToUtf8Bytes(element)
            : null;
}
