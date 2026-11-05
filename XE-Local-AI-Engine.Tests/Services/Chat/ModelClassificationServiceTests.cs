namespace XE_Local_AI_Engine.Tests.Services.Chat;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class ModelClassificationServiceTests
{
    private readonly IGgufModelStore _ggufModelStore = Substitute.For<IGgufModelStore>();
    private readonly InMemoryModelClassificationStore _store = new();

    public ModelClassificationServiceTests()
    {
        _ggufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<LocalModelDescriptor>());
    }

    [Test]
    public async Task SetOverrideAsync_PersistsOverrideAndReturnsEffectiveKind()
    {
        WithInstalled(new LocalModelDescriptor
        {
            ModelName = "org/Qwen-GGUF:Q4_K_M",
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1,
            ModifiedAt = null,
            MaxContextTokens = null,
            Capabilities = ["completion", "tools"]
        });

        var result = await CreateService().SetOverrideAsync("org/Qwen-GGUF:Q4_K_M", ModelKind.Embedding);

        AssertEx.Equal(ModelKind.Embedding, result.Kind);
        AssertEx.Equal(ModelKind.Chat, result.DetectedKind, "The offline kind is still surfaced for a reset affordance.");
        AssertEx.True(result.IsOverridden, "Setting an override marks the model overridden.");
        AssertEx.Contains(result.Capabilities, "tools");
        var stored = AssertEx.NotNull(await _store.GetByNameAsync("org/Qwen-GGUF:Q4_K_M"), "Override should persist.");
        AssertEx.Equal(ModelKind.Embedding, stored.OverrideKind);
    }

    [Test]
    public async Task SetOverrideAsync_WhenModelIsADecisionModel_TheStructuralKindWins()
    {
        WithInstalled(new LocalModelDescriptor
        {
            ModelName = "org/Laya-GGUF:Q8_0",
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1,
            ModifiedAt = null,
            MaxContextTokens = null,
            DecisionType = "classify"
        });

        var result = await CreateService().SetOverrideAsync("org/Laya-GGUF:Q8_0", ModelKind.Chat);

        AssertEx.Equal(ModelKind.Decision, result.Kind, "A decision head cannot chat, whatever the operator sets.");
        AssertEx.False(result.IsOverridden);
    }

    [Test]
    public async Task ResetOverrideAsync_ClearsOverrideAndRevertsToTheOfflineKind()
    {
        _ = await _store.SetOverrideAsync("org/Qwen-GGUF:Q4_K_M", ModelKind.Embedding);

        var result = await CreateService().ResetOverrideAsync("org/Qwen-GGUF:Q4_K_M");

        AssertEx.Equal(ModelKind.Chat, result.Kind, "Clearing the override falls back to the offline kind.");
        AssertEx.Equal(ModelKind.Chat, result.DetectedKind);
        AssertEx.False(result.IsOverridden, "After reset the model is no longer overridden.");
        var stored = AssertEx.NotNull(await _store.GetByNameAsync("org/Qwen-GGUF:Q4_K_M"));
        AssertEx.Null(stored.OverrideKind);
    }

    [Test]
    public async Task SetOverrideAsync_WhenTheModelIsNotInstalled_ReportsNoCapabilities()
    {
        var result = await CreateService().SetOverrideAsync("org/Gone-GGUF:Q4_K_M", ModelKind.Embedding);

        AssertEx.Equal(ModelKind.Embedding, result.Kind);
        AssertEx.Empty(result.Capabilities);
    }

    private ModelClassificationService CreateService() =>
        new(_store, _ggufModelStore);

    private void WithInstalled(LocalModelDescriptor descriptor) =>
        _ggufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            descriptor
        });

    /// <summary>
    ///     A faithful in-memory <see cref="IModelClassificationStore" /> used so the service tests exercise real
    ///     persistence semantics (NOCASE-style name keying) without a database.
    /// </summary>
    private sealed class InMemoryModelClassificationStore : IModelClassificationStore
    {
        private readonly Dictionary<string, ModelClassificationRecord> _rows = new(StringComparer.OrdinalIgnoreCase);

        public Task<ModelClassificationRecord?> GetByNameAsync(string modelName, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_rows.TryGetValue(modelName, out var record) ? record : null);
        }

        public Task<IReadOnlyList<ModelClassificationRecord>> ListAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ModelClassificationRecord>>(_rows.Values.OrderBy(row => row.ModelName, StringComparer.Ordinal).ToArray());
        }

        public Task<ModelClassificationRecord> SetOverrideAsync(string modelName, ModelKind? overrideKind, CancellationToken cancellationToken = default)
        {
            var existing = _rows.TryGetValue(modelName, out var current) ? current : null;
            var record = existing is null
                ? new ModelClassificationRecord
                {
                    ModelName = modelName,
                    Digest = null,
                    DetectedKind = ModelKind.Unknown,
                    DetectedCapabilitiesJson = null,
                    OverrideKind = overrideKind,
                    DetectedAtUtc = null,
                    UpdatedAtUtc = 1
                }
                : existing with
                {
                    OverrideKind = overrideKind,
                    UpdatedAtUtc = 2
                };
            _rows[modelName] = record;
            return Task.FromResult(record);
        }
    }
}
