namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

public sealed record ToolMockDraft(string ToolName, ToolMockBodyV1 Body, bool Enabled);

public sealed record ToolMockVerifyResult(ToolMockRecord Mock, ToolMockVerificationV1 Verification);

public interface IToolMockService
{
    Task<ToolMockRecord> CreateAsync(ToolMockDraft draft, CancellationToken cancellationToken = default);

    Task<ToolMockRecord> UpdateAsync(Guid mockId, long expectedVersion, ToolMockDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Statically verifies the stored body against the tool's live parameter schema and records the verdict.</summary>
    Task<ToolMockVerifyResult> VerifyAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default);
}

/// <inheritdoc />
public sealed class ToolMockService(
    ITrainingDatasetStore store,
    IToolMockStaticVerifier verifier,
    ILocalToolOfferProvider offerProvider) : IToolMockService
{
    private readonly ILocalToolOfferProvider _offerProvider = offerProvider ?? throw new ArgumentNullException(nameof(offerProvider));
    private readonly ITrainingDatasetStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IToolMockStaticVerifier _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));

    public Task<ToolMockRecord> CreateAsync(ToolMockDraft draft, CancellationToken cancellationToken = default) =>
        _store.CreateMockAsync(ToInput(draft), cancellationToken);

    public Task<ToolMockRecord> UpdateAsync(Guid mockId, long expectedVersion, ToolMockDraft draft, CancellationToken cancellationToken = default) =>
        _store.UpdateMockAsync(mockId, expectedVersion, ToInput(draft), cancellationToken);

    public async Task<ToolMockVerifyResult> VerifyAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var mock = await _store.GetMockAsync(mockId, cancellationToken).ConfigureAwait(false)
                   ?? throw new TrainingNotFoundException("The tool mock was not found.");
        var verification = _verifier.TryParse(mock.MockJson.Span, out var body, out var parseError) && body is not null
            ? _verifier.Verify(body, await FindSchemaAsync(mock.ToolName, cancellationToken).ConfigureAwait(false))
            : new ToolMockVerificationV1(SchemaVersion: 1, Passed: false, [parseError ?? "The mock body is unreadable."]);

        var updated = await _store.SetMockVerificationAsync(mockId,
                                      expectedVersion,
                                      verification.Passed ? ToolMockVerificationState.Verified : ToolMockVerificationState.Rejected,
                                      JsonSerializer.SerializeToUtf8Bytes(verification, TrainingJson.Options),
                                      cancellationToken)
                                  .ConfigureAwait(false);
        return new ToolMockVerifyResult(updated, verification);
    }

    private async Task<string?> FindSchemaAsync(string toolName, CancellationToken cancellationToken)
    {
        // The profile pool with no active model: verification is about the mock's shape, not about which model may be
        // offered the tool, so nothing here should be capability-gated away.
        var offered = await _offerProvider.GetOfferedToolsForProfileAsync(activeModelId: null, isCloudModel: false, cancellationToken).ConfigureAwait(false);
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

        return new ToolMockInput(draft.ToolName, JsonSerializer.SerializeToUtf8Bytes(draft.Body, TrainingJson.Options), draft.Enabled);
    }
}
