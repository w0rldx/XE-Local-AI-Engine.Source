namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

public interface IToolMockService
{
    /// <summary>Every tool mock the node holds.</summary>
    Task<IReadOnlyList<ToolMockRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The tool mock, or <c>null</c> when no mock carries that id.</summary>
    Task<ToolMockRecord?> GetAsync(Guid mockId, CancellationToken cancellationToken = default);

    /// <summary>Deletes the tool mock.</summary>
    Task DeleteAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default);

    Task<ToolMockRecord> CreateAsync(ToolMockDraft draft, CancellationToken cancellationToken = default);

    Task<ToolMockRecord> UpdateAsync(Guid mockId, long expectedVersion, ToolMockDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Statically verifies the stored body against the tool's live parameter schema and records the verdict.</summary>
    Task<ToolMockVerifyResult> VerifyAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ToolMockService : IToolMockService
{
    private readonly ILocalToolOfferProvider _offerProvider;
    private readonly ITrainingDatasetStore _store;
    private readonly IToolMockStaticVerifier _verifier;

    public ToolMockService(ITrainingDatasetStore store,
        IToolMockStaticVerifier verifier,
        ILocalToolOfferProvider offerProvider)
    {
        ArgumentNullException.ThrowIfNull(offerProvider);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(verifier);
        _offerProvider = offerProvider;
        _store = store;
        _verifier = verifier;
    }

    public Task<IReadOnlyList<ToolMockRecord>> ListAsync(CancellationToken cancellationToken = default) =>
        _store.ListMocksAsync(cancellationToken);

    public Task<ToolMockRecord?> GetAsync(Guid mockId, CancellationToken cancellationToken = default) =>
        _store.GetMockAsync(mockId, cancellationToken);

    public Task DeleteAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default) =>
        _store.DeleteMockAsync(mockId, expectedVersion, cancellationToken);

    public Task<ToolMockRecord> CreateAsync(ToolMockDraft draft, CancellationToken cancellationToken = default) =>
        _store.CreateMockAsync(ToInput(draft), cancellationToken);

    public Task<ToolMockRecord> UpdateAsync(Guid mockId, long expectedVersion, ToolMockDraft draft, CancellationToken cancellationToken = default) =>
        _store.UpdateMockAsync(mockId, expectedVersion, ToInput(draft), cancellationToken);

    public async Task<ToolMockVerifyResult> VerifyAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var mock = await _store.GetMockAsync(mockId, cancellationToken)
                   ?? throw new TrainingNotFoundException("The tool mock was not found.");
        var verification = _verifier.TryParse(mock.MockJson.Span, out var body, out var parseError) && body is not null
            ? _verifier.Verify(body, await FindSchemaAsync(mock.ToolName, cancellationToken))
            : new ToolMockVerificationV1(SchemaVersion: 1, Passed: false, [parseError ?? "The mock body is unreadable."]);

        var updated = await _store.SetMockVerificationAsync(mockId,
            expectedVersion,
            verification.Passed ? ToolMockVerificationState.Verified : ToolMockVerificationState.Rejected,
            JsonSerializer.SerializeToUtf8Bytes(verification, TrainingJson.Options),
            cancellationToken);
        return new ToolMockVerifyResult
        {
            Mock = updated,
            Verification = verification
        };
    }

    private async Task<string?> FindSchemaAsync(string toolName, CancellationToken cancellationToken)
    {
        // The profile pool with no active model: verification is about the mock's shape, not about which model may be
        // offered the tool, so nothing here should be capability-gated away.
        var offered = await _offerProvider.GetOfferedToolsForProfileAsync(activeModelId: null, isCloudModel: false, cancellationToken);
        return offered.FirstOrDefault(tool => string.Equals(tool.Name, toolName, StringComparison.Ordinal))?.ParameterSchema;
    }

    private static ToolMockInput ToInput(ToolMockDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.Body is null)
        {
            throw new TrainingValidationException("A tool mock requires a body.");
        }

        if (string.IsNullOrWhiteSpace(draft.ToolName))
        {
            throw new TrainingValidationException("A tool mock requires the tool name it stands in for.");
        }

        return new ToolMockInput
        {
            ToolName = draft.ToolName,
            MockJson = JsonSerializer.SerializeToUtf8Bytes(draft.Body, TrainingJson.Options),
            Enabled = draft.Enabled
        };
    }
}
