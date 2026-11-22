namespace XE_Local_AI_Engine.Tests.Knowledge;

using System.Net;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Knowledge.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chunk embedder resolves the configured embedding name against the models installed on the resolved
///     provider before creating the generator, so the same nomic weights work whether they are installed under the
///     configured name or a llama.cpp <c>&lt;repo&gt;:&lt;quant&gt;</c> GGUF name. The name actually handed to
///     <see cref="ILocalModelProvider.CreateEmbeddingGenerator" /> is asserted, and the graceful content-free failure is
///     preserved when the generator itself fails.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class KnowledgeChunkEmbedderResolutionTests
{
    private const int Dimensions = 768;
    private const string ConfiguredName = "nomic-embed-text";

    [Test]
    public async Task EmbedAsync_WhenExactConfiguredNameInstalled_UsesConfiguredName()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName), Descriptor("qwen2.5:Q4_K_M"));
        var embedder = CreateEmbedder(provider);

        var result = await embedder.EmbedAsync(["chunk one"], CancellationToken.None);

        AssertEx.Equal(ConfiguredName, provider.LastSelectedModelName);
        AssertEx.Equal(ConfiguredName, result.ResolvedModel);
        AssertEx.Equal(1, result.Vectors.Count);
        // Dimension is derived from the produced vector, not a static config constant.
        AssertEx.Equal(Dimensions, result.Dimension);
    }

    [Test]
    public async Task EmbedAsync_WhenModelWidthIsNotTheLegacyDefault_DerivesItAndSucceeds()
    {
        // Regression: a 1024-wide model (bge-m3/mxbai) must not fail every chunk against a static 768 constant.
        const int width = 1024;
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            VectorDimensions = [width]
        };
        var embedder = CreateEmbedder(provider);

        var result = await embedder.EmbedAsync(["chunk one", "chunk two"], CancellationToken.None);

        AssertEx.Equal(2, result.Vectors.Count);
        AssertEx.Equal(width, result.Dimension);
        // Each blob is width float32 values → width * 4 bytes.
        AssertEx.Equal(width * sizeof(float), result.Vectors[0].Length);
    }

    [Test]
    public async Task EmbedAsync_WhenModelReturnsInconsistentWidths_ThrowsContentFreeFailure()
    {
        // A dimension-stable model never does this; an inconsistent width within one run is a genuinely broken model.
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            VectorDimensions = [1024, 512]
        };
        var embedder = CreateEmbedder(provider);

        var exception = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() => embedder.EmbedAsync(["chunk one", "chunk two"], CancellationToken.None));

        // Reason names the mismatch (integers only, content-free) so an operator can act.
        AssertEx.True(exception.Reason.Contains("1024", StringComparison.Ordinal), "Reason should name the expected width.");
        AssertEx.True(exception.Reason.Contains("512", StringComparison.Ordinal), "Reason should name the observed width.");
    }

    [Test]
    public async Task EmbedAsync_WhenEmbeddingGgufInstalled_UsesGgufName()
    {
        const string ggufName = "nomic-ai/nomic-embed-text-v1.5-GGUF:Q4_K_M";
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M"), Descriptor(ggufName));
        var embedder = CreateEmbedder(provider);

        var result = await embedder.EmbedAsync(["chunk one"], CancellationToken.None);

        AssertEx.Equal(ggufName, provider.LastSelectedModelName);
        // The resolved (GGUF) name — not the configured name — is what the ingestion lane stamps as the vector scope key.
        AssertEx.Equal(ggufName, result.ResolvedModel);
        AssertEx.Equal(512, result.Dimension);
        AssertEx.Equal(512 * sizeof(float), result.Vectors[0].Length);
        AssertEx.True(result.VectorIdentity.Contains("layernorm-population-eps1e-5-truncate-l2:v1:512", StringComparison.Ordinal));
    }

    [Test]
    public async Task EmbedAsync_WhenTheInventoryHoldsNoEmbedder_FailsAsUnavailableWithoutCreatingAGenerator()
    {
        // The inventory answered without an embedding model: llama-server cannot load the fallback name, and asking would
        // only meet a stale eject mark of a removed model and park the run again.
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M"));
        var embedder = CreateEmbedder(provider);

        var failure = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() => embedder.EmbedAsync(["chunk one"], CancellationToken.None));

        AssertEx.Equal(KnowledgeChunkEmbedder.EmbeddingUnavailableReason, failure.Reason);
        AssertEx.Null(provider.LastSelectedModelName);
        AssertEx.Equal(0, provider.GenerateCalls);
    }

    [Test]
    public async Task EmbedAsync_WhenTheInventoryReadFails_StillAsksTheGeneratorWithTheConfiguredName()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            ThrowOnListModels = true,
            ThrowOnGenerate = true
        };
        var embedder = CreateEmbedder(provider);

        var failure = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() => embedder.EmbedAsync(["chunk one"], CancellationToken.None));

        AssertEx.Equal(KnowledgeChunkEmbedder.EmbeddingUnavailableReason, failure.Reason);
        AssertEx.Equal(ConfiguredName, provider.LastSelectedModelName, "An unreadable inventory proves nothing missing, so the generator is still asked.");
        AssertEx.Equal(1, provider.GenerateCalls);
    }

    [Test]
    public async Task EmbedAsync_WhenServerAnswersWithNon2xx_ReportsRejection_NotMissingModel()
    {
        // REGRESSION: when llama-server rejects every full-size chunk with a 500, the user must not be told
        // "No embedding model is installed ... install an embedding GGUF" while the model is installed, loaded,
        // and serving on the GPU. A REACHABLE server that answered is a different failure from an absent model, and
        // conflating them sends the user to fix something that is not broken.
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            GenerateFailureStatus = HttpStatusCode.InternalServerError
        };
        var embedder = CreateEmbedder(provider);

        var exception = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() =>
            embedder.EmbedAsync(["chunk one"], CancellationToken.None));

        AssertEx.True(exception.Reason.Contains("rejected the request", StringComparison.Ordinal),
            $"Reason should say the server rejected the request, got: {exception.Reason}");
        AssertEx.False(exception.Reason.Contains("No embedding model is installed", StringComparison.Ordinal),
            "A server that answered must NOT be reported as a missing embedding model.");

        // The transport detail must survive on the exception chain so the ingestion service can log it — the reason
        // itself stays content-free and fixed, because it is persisted on the document row.
        AssertEx.NotNull(exception.InnerException);
    }

    [Test]
    public async Task EmbedAsync_OnAnotherProviderWithNoEmbedderInstalled_AsksTheGeneratorOnceAndReportsMissingModel()
    {
        // No installed model matched, so "install an embedding model" IS the correct remediation, and there is no
        // process worth retrying: the generator is called exactly once. Only the llama.cpp provider fails before asking.
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M"))
        {
            ThrowOnGenerate = true
        };
        var embedder = CreateEmbedder(provider, providerName: "another-runtime");

        var exception = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() =>
            embedder.EmbedAsync(["chunk one"], CancellationToken.None));

        AssertEx.True(exception.Reason.Contains("No embedding model is installed", StringComparison.Ordinal),
            $"A missing model should report the install remediation, got: {exception.Reason}");
        AssertEx.Equal(1, provider.GenerateCalls);
    }

    [Test]
    public async Task EmbedAsync_WhenInstalledModelProcessStaysUnreachable_ReportsDeadProcess_NotMissingModel()
    {
        // REGRESSION: a killed embedding llama-server mid-document reported "No embedding model is installed"
        // although it was installed and respawned. A confidently-resolved model gets one retry, then a reason naming the process.
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            ThrowOnGenerate = true
        };
        var embedder = CreateEmbedder(provider);

        var exception = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() =>
            embedder.EmbedAsync(["chunk one"], CancellationToken.None));

        AssertEx.True(exception.Reason.Contains("process stopped or became unreachable", StringComparison.Ordinal),
            $"An installed model's dead process should be named, got: {exception.Reason}");
        AssertEx.False(exception.Reason.Contains("No embedding model is installed", StringComparison.Ordinal),
            "An installed model must NOT be reported as missing.");
        AssertEx.NotNull(exception.InnerException);
        AssertEx.Equal(2, provider.GenerateCalls);
    }

    [Test]
    public async Task EmbedAsync_WhenInstalledModelProcessDiesOnceMidDocument_RetriesTheBatchAndSucceeds()
    {
        // The llama.cpp generator drops its dead endpoint on a transport failure and re-ensures (respawns) the process on
        // its next call, so the failed batch is re-issued once and the document indexes with every chunk embedded.
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            TransientFailures = 1
        };
        var embedder = CreateEmbedder(provider, maxBatchSize: 1);

        var result = await embedder.EmbedAsync(["chunk one", "chunk two"], CancellationToken.None);

        AssertEx.Equal(2, result.Vectors.Count);
        AssertEx.Equal(3, provider.GenerateCalls);
    }

    [Test]
    public async Task EmbedAsync_EmbedsWithBackgroundIntent()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName));

        _ = await CreateEmbedder(provider).EmbedAsync(["chunk one"], CancellationToken.None);

        AssertEx.Equal(ModelResidencyIntent.Background, provider.LastSelectedIntent);
    }

    [Test]
    public async Task EmbedAsync_WhenTheOperatorVetoesAfterAnEarlierBatchSpentTheRetry_PassesTheVetoThrough()
    {
        // Call 1 fails and spends the retry, call 2 embeds chunk one, call 3 is the operator's veto for chunk two.
        var provider = new CapturingProvider(Descriptor(ConfiguredName))
        {
            TransientFailures = 1,
            VetoOnCall = 3
        };
        var embedder = CreateEmbedder(provider, maxBatchSize: 1);

        _ = await AssertEx.ThrowsAsync<OperatorEjectedException>(() => embedder.EmbedAsync(["chunk one", "chunk two"], CancellationToken.None));

        AssertEx.Equal(3, provider.GenerateCalls);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task IsEmbeddingModelBlockedAsync_AsksTheSupervisorForTheResolvedEmbedder(bool blocked)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.IsBackgroundLaunchBlocked(ConfiguredName, ModelRole.Embedding).Returns(blocked);
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor(ConfiguredName)), supervisor: supervisor);

        AssertEx.Equal(blocked, await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None));
    }

    [Test]
    public async Task IsEmbeddingModelBlockedAsync_WhenTheInventoryHoldsNoEmbedder_IsFalseWithoutAskingTheSupervisor()
    {
        // The operator ejected the unpinned embedder, then removed it: the inventory answered and nothing matches, so there
        // is nothing to wait for and the re-admitted run fails with the missing-model reason instead of stranding.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor("qwen2.5:Q4_K_M")), supervisor: supervisor);

        AssertEx.False(await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None), "A removed unpinned embedder must not keep rows waiting.");
        _ = supervisor.DidNotReceiveWithAnyArgs().IsBackgroundLaunchBlocked(default!, default);
    }

    [Test]
    public async Task IsEmbeddingModelBlockedAsync_WhenThePinnedModelWasRemoved_IsFalseSoTheRunFailsWithTheMissingModelReason()
    {
        // The inventory answered and the pinned model is not in it: nothing to wait for, even if an eject mark remains.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor(ConfiguredName)), supervisor: supervisor, pinnedModel: "removed-embed.gguf");

        AssertEx.False(await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None), "A removed pinned model must not keep rows waiting.");
    }

    [Test]
    public async Task EmbedAsync_WhenThePinnedModelWasRemoved_FailsWithThePinnedModelMissingReasonWithoutCreatingAGenerator()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName));
        var embedder = CreateEmbedder(provider, pinnedModel: "removed-embed.gguf");

        var failure = await AssertEx.ThrowsAsync<KnowledgeIngestionException>(() => embedder.EmbedAsync(["chunk one"], CancellationToken.None));

        AssertEx.Equal(KnowledgeChunkEmbedder.EmbeddingPinnedModelMissingReason, failure.Reason, "The remedy is reinstalling or re-choosing the pin, not installing any embedder.");
        AssertEx.Null(provider.LastSelectedModelName, "A removed pinned model must never reach the supervisor, where a stale eject mark would veto it.");
    }

    [Test]
    public async Task IsEmbeddingModelBlockedAsync_WhenTheInventoryReadFails_IsTrue()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor(ConfiguredName))
            {
                ThrowOnListModels = true
            },
            supervisor: supervisor,
            pinnedModel: ConfiguredName);

        AssertEx.True(await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None), "A transient inventory failure keeps waiting rows waiting.");
    }

    [Test]
    public async Task IsEmbeddingModelBlockedAsync_OnAnotherProvider_IsFalseWithoutAskingTheSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor(ConfiguredName)), supervisor: supervisor, providerName: "another-runtime");

        AssertEx.False(await embedder.IsEmbeddingModelBlockedAsync(CancellationToken.None));
        _ = supervisor.DidNotReceiveWithAnyArgs().IsBackgroundLaunchBlocked(default!, default);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task IsEmbeddingModelEjectedAsync_AsksTheSupervisorForTheConfidentlyResolvedEmbedder(bool blocked)
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.HasBackgroundLaunchBlock(ModelRole.Embedding).Returns(true);
        supervisor.IsBackgroundLaunchBlocked(ConfiguredName, ModelRole.Embedding).Returns(blocked);
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor(ConfiguredName)), supervisor: supervisor);

        AssertEx.Equal(blocked, await embedder.IsEmbeddingModelEjectedAsync(CancellationToken.None));
    }

    [Test]
    public async Task IsEmbeddingModelEjectedAsync_WhenNoEmbeddingMarkExists_IsFalseWithoutResolvingTheModel()
    {
        // A large import asks once per chunkless document; with no embedding mark at all, no inventory read is paid.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.HasBackgroundLaunchBlock(ModelRole.Embedding).Returns(false);
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var provider = new CapturingProvider(Descriptor(ConfiguredName));
        var embedder = CreateEmbedder(provider, supervisor: supervisor);

        AssertEx.False(await embedder.IsEmbeddingModelEjectedAsync(CancellationToken.None));
        AssertEx.Equal(0, provider.ListModelsCalls, "No embedding mark means nothing to resolve.");
        _ = supervisor.DidNotReceiveWithAnyArgs().IsBackgroundLaunchBlocked(default!, default);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task IsEmbeddingModelEjectedAsync_WhenTheResolutionIsNotConfident_IsFalseWithoutAskingTheSupervisor(bool inventoryFails)
    {
        // Unlike the wake's blocked check, an unknown embedder never parks a fresh upload: its run meets the real cause.
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.HasBackgroundLaunchBlock(ModelRole.Embedding).Returns(true);
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M"))
        {
            ThrowOnListModels = inventoryFails
        };
        var embedder = CreateEmbedder(provider, supervisor: supervisor);

        AssertEx.False(await embedder.IsEmbeddingModelEjectedAsync(CancellationToken.None));
        AssertEx.Equal(1, provider.ListModelsCalls, "A standing embedding mark makes the check resolve the model.");
        _ = supervisor.DidNotReceiveWithAnyArgs().IsBackgroundLaunchBlocked(default!, default);
    }

    [Test]
    public async Task IsEmbeddingModelEjectedAsync_OnAnotherProvider_IsFalseWithoutAskingTheSupervisor()
    {
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.HasBackgroundLaunchBlock(ModelRole.Embedding).Returns(true);
        supervisor.IsBackgroundLaunchBlocked(Arg.Any<string>(), Arg.Any<ModelRole>()).Returns(true);
        var embedder = CreateEmbedder(new CapturingProvider(Descriptor(ConfiguredName)), supervisor: supervisor, providerName: "another-runtime");

        AssertEx.False(await embedder.IsEmbeddingModelEjectedAsync(CancellationToken.None));
        _ = supervisor.DidNotReceiveWithAnyArgs().IsBackgroundLaunchBlocked(default!, default);
    }

    [Test]
    public async Task IsEmbeddingModelEjectedAsync_WhenTheProviderCannotBeResolved_IsFalse()
    {
        // An unmapped provider is not an eject; the run's embed step reports it with its own reason.
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("No provider is registered."));
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingModelName = ConfiguredName
        });
        var supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.HasBackgroundLaunchBlock(ModelRole.Embedding).Returns(true);
        var embedder = new KnowledgeChunkEmbedder(providerResolver,
            new EmbeddingModelResolver(options, new FakeNodeSettingsStore(new StoredNodeSettings())),
            new KnowledgeEmbeddingPrefixer(),
            options,
            supervisor);

        AssertEx.False(await embedder.IsEmbeddingModelEjectedAsync(CancellationToken.None));
        _ = providerResolver.Received(1).ResolveProvider(Arg.Any<string>());
    }

    [Test]
    public async Task ResolveEmbeddingContextWindowAsync_WhenResolvedModelAdvertisesAWindow_ReturnsIt()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName, maxContextTokens: 2048));
        var embedder = CreateEmbedder(provider);

        var window = await embedder.ResolveEmbeddingContextWindowAsync(CancellationToken.None);

        AssertEx.Equal(2048, window);
    }

    [Test]
    public async Task ResolveEmbeddingContextWindowAsync_WhenResolvedModelAdvertisesNoWindow_ReturnsNull()
    {
        var provider = new CapturingProvider(Descriptor(ConfiguredName));
        var embedder = CreateEmbedder(provider);

        var window = await embedder.ResolveEmbeddingContextWindowAsync(CancellationToken.None);

        AssertEx.Null(window);
    }

    [Test]
    public async Task ResolveEmbeddingContextWindowAsync_WhenResolutionIsNotConfident_ReturnsNull()
    {
        // Nothing installed matches the configured name and no embedding-named model is present, so the resolution is a
        // bare fallback (not confident) — its window is unknown and must not be guessed from an unrelated model.
        var provider = new CapturingProvider(Descriptor("qwen2.5:Q4_K_M", maxContextTokens: 32768));
        var embedder = CreateEmbedder(provider);

        var window = await embedder.ResolveEmbeddingContextWindowAsync(CancellationToken.None);

        AssertEx.Null(window);
    }

    private static KnowledgeChunkEmbedder CreateEmbedder(ILocalModelProvider provider,
        int maxBatchSize = 64,
        ILlamaServerProcessSupervisor? supervisor = null,
        string providerName = "llamacpp",
        string? pinnedModel = null)
    {
        var options = Options.Create(new KnowledgeBaseOptions
        {
            EmbeddingModelName = ConfiguredName,
            EmbeddingProviderName = providerName,
            MaxEmbeddingBatchSize = maxBatchSize
        });

        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        providerResolver.ResolveProvider(Arg.Any<string>()).Returns(provider);

        return new KnowledgeChunkEmbedder(providerResolver,
            new EmbeddingModelResolver(options, new FakeNodeSettingsStore(new StoredNodeSettings
            {
                KnowledgeEmbeddingModelName = pinnedModel
            })),
            new KnowledgeEmbeddingPrefixer(),
            options,
            supervisor ?? Substitute.For<ILlamaServerProcessSupervisor>());
    }

    private static LocalModelDescriptor Descriptor(string modelName, int? maxContextTokens = null)
    {
        return new LocalModelDescriptor
        {
            ModelName = modelName,
            ProviderName = "llamacpp",
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = maxContextTokens,
            Capabilities = []
        };
    }

    // A node-local provider fake that records the model name its embedding generator was created with and returns
    // fixed-dimension zero vectors (or throws a transport error), so the resolution wiring can be asserted without a model runtime.
    private sealed class CapturingProvider : ILocalModelProvider
    {
        private readonly LocalModelDescriptor[] _models;

        public CapturingProvider(params LocalModelDescriptor[] models)
        {
            _models = models;
        }

        public string? LastSelectedModelName { get; private set; }

        public ModelResidencyIntent? LastSelectedIntent { get; private set; }

        /// <summary>The <c>GenerateAsync</c> call number that throws the operator's eject veto instead.</summary>
        public int? VetoOnCall { get; init; }

        public bool ThrowOnGenerate { get; init; }

        /// <summary>When set, the inventory read fails as an unreachable provider.</summary>
        public bool ThrowOnListModels { get; init; }

        /// <summary>How many leading <c>GenerateAsync</c> calls fail as an unreachable process before generation succeeds.</summary>
        public int TransientFailures { get; init; }

        /// <summary>Total <c>GenerateAsync</c> calls across every generator this provider created.</summary>
        public int GenerateCalls { get; private set; }

        /// <summary>
        ///     When set, <c>GenerateAsync</c> throws a status-carrying <see cref="HttpRequestException" /> — the shape the
        ///     llama.cpp provider translates a llama-server non-2xx into. Distinct from <see cref="ThrowOnGenerate" />,
        ///     which models an unreachable provider (no status).
        /// </summary>
        public HttpStatusCode? GenerateFailureStatus { get; init; }

        // Width of each produced vector, by position; the last entry repeats when more vectors than entries are generated.
        // Defaults to the shipped nomic width so the resolution tests are unaffected; overridden to exercise arbitrary and
        // inconsistent widths.
        public IReadOnlyList<int> VectorDimensions { get; init; } = [Dimensions];

        public string ProviderName => "llamacpp";

        public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGenerator(LocalModelSelection selection)
        {
            LastSelectedModelName = selection.ModelName;
            LastSelectedIntent = selection.ResidencyIntent;
            return new FixedEmbeddingGenerator(this, VectorDimensions, GenerateFailureStatus);
        }

        /// <summary>Total <c>ListModelsAsync</c> calls, i.e. inventory reads.</summary>
        public int ListModelsCalls { get; private set; }

        public Task<IReadOnlyList<LocalModelDescriptor>> ListModelsAsync(CancellationToken ct)
        {
            ListModelsCalls++;
            return ThrowOnListModels
                ? Task.FromException<IReadOnlyList<LocalModelDescriptor>>(new HttpRequestException("fake inventory transport failure"))
                : Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(_models);
        }

        public IChatClient CreateChatClient(LocalModelSelection selection) =>
            throw new NotSupportedException();

        public Task<ModelProviderHealth> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task PullModelAsync(string modelName, IProgress<PullProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task WarmModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UnloadModelAsync(string modelName, CancellationToken ct) =>
            throw new NotSupportedException();

        private sealed class FixedEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
        {
            private readonly CapturingProvider _owner;
            private readonly IReadOnlyList<int> _dimensions;
            private readonly HttpStatusCode? _failureStatus;

            public FixedEmbeddingGenerator(CapturingProvider owner,
                IReadOnlyList<int> dimensions,
                HttpStatusCode? failureStatus = null)
            {
                _owner = owner;
                _dimensions = dimensions;
                _failureStatus = failureStatus;
            }

            public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
                EmbeddingGenerationOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                _owner.GenerateCalls++;
                if (_owner.GenerateCalls == _owner.VetoOnCall)
                {
                    throw new OperatorEjectedException("The embedding model was ejected by the operator.");
                }

                if (_failureStatus is { } status)
                {
                    throw new HttpRequestException("The llama-server embedding endpoint returned HTTP 500: input (678 tokens) is too large to process.",
                        inner: null,
                        status);
                }

                if (_owner.ThrowOnGenerate || _owner.GenerateCalls <= _owner.TransientFailures)
                {
                    throw new HttpRequestException("fake embedding transport failure");
                }

                var embeddings = values.Select((_, index) =>
                    new Embedding<float>(new float[_dimensions[Math.Min(index, _dimensions.Count - 1)]]));
                return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(embeddings));
            }

            public object? GetService(Type serviceType, object? serviceKey = null) =>
                null;

            public void Dispose()
            {
            }
        }
    }
}
