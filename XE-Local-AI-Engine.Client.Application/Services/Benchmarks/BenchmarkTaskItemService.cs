namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text;
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
        var existing = await _benchmarkStore.ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false);

        // The generator's id is minted HERE rather than by the store, because every case it expands into is derived
        // from it: the id is the seed material that makes one probe's haystacks its own.
        var (input, children) = await ToInputAsync(projectId, Guid.NewGuid(), draft, cancellationToken).ConfigureAwait(false);
        EnsureLeafCap(existing, children?.Count ?? 1);
        return await _benchmarkStore.CreateTaskItemAsync(projectId, expectedProjectVersion, input, children, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkTaskItemRecord> UpdateAsync(Guid projectId,
        Guid itemId,
        long expectedVersion,
        BenchmarkTaskItemDraft draft,
        CancellationToken cancellationToken = default)
    {
        var existing = await _benchmarkStore.ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureNotGenerated(existing, itemId);
        var (input, children) = await ToInputAsync(projectId, itemId, draft, cancellationToken).ConfigureAwait(false);
        if (children is not null)
        {
            // Re-expansion REPLACES this generator's cases, so only the difference counts against the cap.
            EnsureLeafCap(existing, children.Count - existing.Count(item => item.ParentItemId == itemId));
        }

        return await _benchmarkStore.UpdateTaskItemAsync(projectId, itemId, expectedVersion, input, children, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid projectId, Guid itemId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var existing = await _benchmarkStore.ListTaskItemsAsync(projectId, cancellationToken).ConfigureAwait(false);
        EnsureNotGenerated(existing, itemId);
        await _benchmarkStore.DeleteTaskItemAsync(projectId, itemId, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

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

    /// <summary>
    ///     Refuses a write to a GENERATED case. Its parameters live on the generator that produced it, and an edit
    ///     here would survive exactly until the next re-expansion — a case that disagrees with the probe it belongs to
    ///     is a probe that measures something nobody configured.
    /// </summary>
    private static void EnsureNotGenerated(IReadOnlyList<BenchmarkTaskItemRecord> existing, Guid itemId)
    {
        if (existing.Any(item => item.Id == itemId && string.Equals(item.Kind, BenchmarkTaskItemKinds.NiahCase, StringComparison.Ordinal)))
        {
            throw new BenchmarkValidationException(
                "A generated long-context case cannot be edited or deleted on its own. Change the probe it was generated from.");
        }
    }

    private static void EnsureLeafCap(IReadOnlyList<BenchmarkTaskItemRecord> existing, int leafDelta)
    {
        if (existing.Count(static item => item.IsLeaf) + leafDelta > MaxTaskItems)
        {
            throw new BenchmarkValidationException($"A benchmark project holds at most {MaxTaskItems} task items.");
        }
    }

    /// <summary>
    ///     The item to write and — for a generator — the cases it expands into, both decided before the store opens a
    ///     transaction. Expansion at WRITE time is what gives a probe's cases durable identity: each one is an
    ///     ordinary item with its own id, revision and input hash, so the caps count them, a freeze stamps them onto
    ///     runs, and the staleness exclusions reach them without any of those knowing what NIAH is.
    /// </summary>
    private async Task<(BenchmarkTaskItemInput Input, IReadOnlyList<BenchmarkTaskItemInput>? Children)> ToInputAsync(Guid projectId,
        Guid itemId,
        BenchmarkTaskItemDraft draft,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Prompt))
        {
            throw new BenchmarkValidationException("A benchmark task item needs a prompt.");
        }

        var kind = string.IsNullOrWhiteSpace(draft.Kind) ? BenchmarkTaskItemKinds.Prompt : draft.Kind.Trim();
        if (kind is not (BenchmarkTaskItemKinds.Prompt or BenchmarkTaskItemKinds.Niah))
        {
            // A case is written by the generator that owns it, never by an operator: one written by hand would carry
            // a parent that does not describe it.
            throw new BenchmarkValidationException(
                $"A task item is either '{BenchmarkTaskItemKinds.Prompt}' or '{BenchmarkTaskItemKinds.Niah}'.");
        }

        var input = new BenchmarkTaskItemInput(JsonSerializer.SerializeToUtf8Bytes(draft.Prompt),
            kind,
            string.IsNullOrWhiteSpace(draft.ReferenceAnswer)
                ? null
                : (ReadOnlyMemory<byte>?)JsonSerializer.SerializeToUtf8Bytes(draft.ReferenceAnswer.Trim()),
            Encode(draft.VerifierConfig),
            Encode(draft.GeneratorConfig),
            Id: itemId,
            CountsTowardScore: draft.CountsTowardScore);

        return string.Equals(kind, BenchmarkTaskItemKinds.Niah, StringComparison.Ordinal)
            ? (input, await ExpandAsync(projectId, itemId, draft.GeneratorConfig, cancellationToken).ConfigureAwait(false))
            : (input, null);
    }

    private async Task<IReadOnlyList<BenchmarkTaskItemInput>> ExpandAsync(Guid projectId,
        Guid itemId,
        JsonElement? generatorConfig,
        CancellationToken cancellationToken)
    {
        if (generatorConfig is not { } element || element.ValueKind is not JsonValueKind.Object)
        {
            throw new BenchmarkValidationException("A long-context probe needs its generator configuration.");
        }

        BenchmarkNiahConfigV1? config;
        try
        {
            config = element.Deserialize<BenchmarkNiahConfigV1>(BenchmarkNiahGenerator.SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new BenchmarkValidationException($"The long-context probe configuration is invalid: {exception.Message}");
        }

        if (config is null)
        {
            throw new BenchmarkValidationException("A long-context probe needs its generator configuration.");
        }

        // The project's window is the refusal's other number, and it is read here so the operator is told while still
        // looking at the form. The freeze re-checks it anyway: a project's context can be edited after expansion.
        var project = await _benchmarkStore.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        var criterionId = BenchmarkNiahGenerator.CriterionIdOf(config);
        return
        [
            .. BenchmarkNiahGenerator.Expand(itemId, config, project.ContextTokens)
                                     .Select(generated => new BenchmarkTaskItemInput(
                                         JsonSerializer.SerializeToUtf8Bytes(generated.Prompt),
                                         BenchmarkTaskItemKinds.NiahCase,
                                         ReferenceAnswerJson: JsonSerializer.SerializeToUtf8Bytes(generated.ExpectedAnswer),
                                         VerifierConfigJson: Encoding.UTF8.GetBytes(
                                             BenchmarkNiahGenerator.VerifierConfigJson(criterionId, generated.ExpectedAnswer)),
                                         GeneratorConfigJson: JsonSerializer.SerializeToUtf8Bytes(generated.Case, BenchmarkNiahGenerator.SerializerOptions),
                                         ParentItemId: itemId,

                                         // Recall is a capability, not quality. The default keeps a 0-or-10 needle
                                         // score out of the project's rubric mean and leaves it on its own axis.
                                         CountsTowardScore: config.CountsTowardScore))
        ];
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
