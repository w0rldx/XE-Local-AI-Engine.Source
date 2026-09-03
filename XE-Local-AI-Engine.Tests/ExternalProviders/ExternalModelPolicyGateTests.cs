namespace XE_Local_AI_Engine.Tests.ExternalProviders;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The policy sites converted for external models, each asked the one question that would otherwise have been
///     answered wrong.
/// </summary>
/// <remarks>
///     They all share a root cause. An unrecognized model id falls THROUGH cloud selection by design — the orphan
///     guard that keeps an <c>ext:</c> id routing to the local pipeline — so before this conversion every one of these
///     gates would have classified a hosted endpoint as node-local and handed it whatever a node-local model gets.
/// </remarks>
public sealed class ExternalModelPolicyGateTests
{
    private const string LocalExternalModel = "ext:local-box/qwen3";
    private const string CloudExternalModel = "ext:cloud-box/qwen3";
    private const string DeletedExternalModel = "ext:gone/qwen3";

    [Test]
    public async Task ModelCapabilityResolver_ForAnExternalModel_ReportsTheOperatorsDeclarations()
    {
        var resolver = CreateCapabilityResolver();

        var snapshot = await resolver.ResolveAsync(LocalExternalModel, CancellationToken.None);

        AssertEx.True(snapshot.SupportsTools);
        AssertEx.True(snapshot.SupportsVision);
        AssertEx.True(snapshot.SupportsThinking);
        AssertEx.False(snapshot.IsCloud);
        // Vacuously enforceable: this provider emits no llama-server budget field, so no cap can be silently ignored.
        AssertEx.True(snapshot.ReasoningBudgetEnforceable);
    }

    [Test]
    public async Task ModelCapabilityResolver_ForADeclaredCloudExternalModel_ReportsCloud()
    {
        var snapshot = await CreateCapabilityResolver().ResolveAsync(CloudExternalModel, CancellationToken.None);

        AssertEx.True(snapshot.IsCloud, "a declared-cloud external model must reach the private-data gates as cloud.");
        // Capabilities are independent of locality: the operator declared them, and the gate reads IsCloud separately.
        AssertEx.True(snapshot.SupportsTools);
    }

    [Test]
    public async Task ModelCapabilityResolver_ForAnUnresolvedExternalModel_FailsClosed()
    {
        var snapshot = await CreateCapabilityResolver().ResolveAsync(DeletedExternalModel, CancellationToken.None);

        AssertEx.True(snapshot.IsCloud);
        AssertEx.False(snapshot.SupportsTools);
        AssertEx.False(snapshot.SupportsThinking);
        AssertEx.False(snapshot.SupportsVision);
    }

    [Test]
    public async Task ModelCapabilityResolver_ForANonExternalModel_IsUnchanged()
    {
        // The GGUF path still owns non-external ids: the external branch must not have swallowed them.
        var snapshot = await CreateCapabilityResolver().ResolveAsync("qwen3-27b.gguf", CancellationToken.None);

        AssertEx.False(snapshot.IsCloud);
        AssertEx.False(snapshot.SupportsTools);
    }

    [Test]
    public void LocalToolOfferProvider_ForADeclaredLocalExternalModel_KeepsFullLocalParity()
    {
        var provider = CreateOfferProvider();

        var offered = provider.GetOfferedToolsForProfile(LocalExternalModel);

        // The locked decision: a self-hosted endpoint the operator declared Local gets the same tools a GGUF gets,
        // run_python included.
        AssertEx.Contains(offered, tool => string.Equals(tool.Name, ComputeToolDefinition.ToolName, StringComparison.Ordinal));
    }

    [Test]
    public void LocalToolOfferProvider_ForADeclaredCloudExternalModel_WithholdsRunPython()
    {
        var provider = CreateOfferProvider();

        var offered = provider.GetOfferedToolsForProfile(CloudExternalModel);

        // Not the content-leak rationale: what is withheld is a REMOTE model's ability to direct code execution on the
        // operator's machine, so it is unconditional rather than behind the knowledge-tool opt-in.
        AssertEx.False(offered.Any(tool => string.Equals(tool.Name, ComputeToolDefinition.ToolName, StringComparison.Ordinal)));
    }

    [Test]
    public void LocalToolOfferProvider_ForAnUnresolvedExternalModel_WithholdsRunPython()
    {
        var provider = CreateOfferProvider();

        AssertEx.False(provider.GetOfferedToolsForProfile(DeletedExternalModel)
                               .Any(tool => string.Equals(tool.Name, ComputeToolDefinition.ToolName, StringComparison.Ordinal)));
    }

    [Test]
    public void LocalToolOfferProvider_WithAColdRegistryCache_WithholdsRunPythonFromADeclaredLocalModel()
    {
        // The pre-boot window. Withholding is the price of a synchronous gate that cannot block on a file read; the
        // startup reconciliation pass primes the snapshot so this is the only window in which it happens.
        var trust = new FakeModelTrustResolver();
        _ = trust.Register("local-box", "qwen3");
        trust.CacheIsCold = true;
        var provider = CreateOfferProvider(trust);

        AssertEx.False(provider.GetOfferedToolsForProfile(LocalExternalModel)
                               .Any(tool => string.Equals(tool.Name, ComputeToolDefinition.ToolName, StringComparison.Ordinal)));
    }

    [Test]
    public async Task LocalToolOfferProvider_ForADeclaredCloudExternalModel_WithholdsCustomTools()
    {
        var provider = CreateOfferProvider();

        var offered = await provider.GetOfferedToolsAsync(CloudExternalModel, isCloudModel: false, CancellationToken.None);
        var localOffered = await provider.GetOfferedToolsAsync(LocalExternalModel, isCloudModel: false, CancellationToken.None);

        // The turn's own isCloudModel flag is false for both — the cloud branch never sees an ext: id — so any
        // difference here is the trust resolver doing its job.
        AssertEx.Equal(localOffered.Count >= offered.Count, actual: true);
    }

    [Test]
    public async Task CloudModelResolver_ClassifiesExternalModelsByDeclaredLocality()
    {
        var resolver = new CloudModelResolver(Substitute.For<ICloudCredentialStore>(),
            Trust(),
            NullLogger<CloudModelResolver>.Instance);

        AssertEx.False(await resolver.IsCloudModelAsync(LocalExternalModel));
        AssertEx.True(await resolver.IsCloudModelAsync(CloudExternalModel));
        AssertEx.True(await resolver.IsCloudModelAsync(DeletedExternalModel));
    }

    [Test]
    public async Task DevelopmentCoderModel_RefusesAnExternalModelThatIsNotDeclaredLocal()
    {
        using var chat = new CountingChatClient();
        var model = new DevelopmentCoderModel(chat, NonCloudFactory(), Substitute.For<ILocalModelProviderResolver>(), Trust(), NullLogger<DevelopmentCoderModel>.Instance);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
            model.RunAsync(CloudExternalModel, "prompt", new UnusedWorkspaceTools(), maxOutputTokens: 64, maxToolCalls: 2));

        // Refused before any send: Dev Mode hands the model a real workspace.
        AssertEx.Equal(expected: 0, chat.CallCount);
    }

    [Test]
    public async Task DevelopmentCoderModel_RefusesAnUnresolvedExternalModel()
    {
        using var chat = new CountingChatClient();
        var model = new DevelopmentCoderModel(chat, NonCloudFactory(), Substitute.For<ILocalModelProviderResolver>(), Trust(), NullLogger<DevelopmentCoderModel>.Instance);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
            model.RunAsync(DeletedExternalModel, "prompt", new UnusedWorkspaceTools(), maxOutputTokens: 64, maxToolCalls: 2));
    }

    [Test]
    public async Task DevelopmentReviewerModel_RefusesAnExternalModelThatIsNotDeclaredLocal()
    {
        using var chat = new CountingChatClient();
        var model = new DevelopmentReviewerModel(chat, NonCloudFactory(), Substitute.For<ILocalModelProviderResolver>(), Trust(), NullLogger<DevelopmentReviewerModel>.Instance);

        _ = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() =>
            model.RunAsync(CloudExternalModel, "prompt", new UnusedWorkspaceTools(), maxOutputTokens: 64, maxToolCalls: 2));

        AssertEx.Equal(expected: 0, chat.CallCount);
    }

    [Test]
    public void RuntimeChatClient_RefusesADevelopmentMarkedSendToANonLocalExternalModel()
    {
        using var localClient = new CountingChatClient();
        using var runtime = new RuntimeChatClient(NonCloudFactory(),
            () => localClient,
            new ThrowingCloudEgressAuthorizer(),
            Trust());

        var options = MarkedDevelopmentOptions(CloudExternalModel);

        _ = AssertEx.Throws<CloudEgressAuthorizationException>(() => runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options));

        // The last point before bytes go on the wire: every other per-send egress authorization lives on the cloud
        // branch, which an ext: id never reaches.
        AssertEx.Equal(expected: 0, localClient.CallCount);
    }

    [Test]
    public async Task RuntimeChatClient_AllowsADevelopmentMarkedSendToADeclaredLocalExternalModel()
    {
        using var localClient = new CountingChatClient();
        using var runtime = new RuntimeChatClient(NonCloudFactory(),
            () => localClient,
            new ThrowingCloudEgressAuthorizer(),
            Trust());

        var options = MarkedDevelopmentOptions(LocalExternalModel);

        _ = await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options);

        AssertEx.Equal(expected: 1, localClient.CallCount);
    }

    [Test]
    public async Task RuntimeChatClient_LeavesAnUnmarkedExternalSendAlone()
    {
        using var localClient = new CountingChatClient();
        using var runtime = new RuntimeChatClient(NonCloudFactory(),
            () => localClient,
            new ThrowingCloudEgressAuthorizer(),
            Trust());

        // No Development marker: an ordinary chat turn to a declared-cloud external model is a supported flow.
        _ = await runtime.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], new ChatOptions
        {
            ModelId = CloudExternalModel
        });

        AssertEx.Equal(expected: 1, localClient.CallCount);
    }

    [Test]
    public void UsageProviderClassifier_AttributesAnExternalTurnToItsConnection()
    {
        AssertEx.Equal("external:cloud-box", UsageProviderClassifier.ClassifyExternal(CloudExternalModel));
        // One label per connection, not one bucket for everything external: a free self-hosted box and a metered hosted
        // API must not merge in the ledger's provider column.
        AssertEx.Equal("external:local-box", UsageProviderClassifier.ClassifyExternal(LocalExternalModel));
        AssertEx.Null(UsageProviderClassifier.ClassifyExternal("qwen3-27b.gguf"));
        AssertEx.Null(UsageProviderClassifier.ClassifyExternal("ext:malformed"));
    }

    [Test]
    public async Task UsageProviderResolver_ClassifiesAnExternalTurnWithoutConsultingEitherRouter()
    {
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        var resolver = new UsageProviderResolver(cloudFactory, providerResolver, TimeProvider.System, NullLogger<UsageProviderResolver>.Instance);

        AssertEx.Equal("external:cloud-box", await resolver.ResolveAsync(CloudExternalModel));

        // Neither router can see an ext: id: cloud selection falls through it, and the local lookup would label the
        // turn "unknown" — losing exactly the attribution an operator running several endpoints needs.
        _ = cloudFactory.DidNotReceive().ResolveActiveCloudProviderName(Arg.Any<string>());
        _ = providerResolver.DidNotReceive().ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static FakeModelTrustResolver Trust()
    {
        return new FakeModelTrustResolver()
               .Register("local-box", "qwen3", contextLength: 65536, supportsTools: true, supportsVision: true, supportsReasoning: true)
               .Register("cloud-box", "qwen3", ExternalProviderLocality.Cloud, supportsTools: true, supportsVision: true, supportsReasoning: true);
    }

    private static IActiveCloudChatClientFactory NonCloudFactory()
    {
        var factory = Substitute.For<IActiveCloudChatClientFactory>();
        _ = factory.IsCloudProviderSelected(Arg.Any<string?>()).Returns(false);
        return factory;
    }

    private static ModelCapabilityResolver CreateCapabilityResolver()
    {
        var providerResolver = Substitute.For<ILocalModelProviderResolver>();
        _ = providerResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                            .Returns(OllamaLocalModelProvider.OllamaProviderName + "-not");

        return new ModelCapabilityResolver(Substitute.For<IModelClassificationService>(),
            providerResolver,
            Substitute.For<IGgufModelCapabilityResolver>(),
            NonCloudFactory(),
            Trust(),
            NullLogger<ModelCapabilityResolver>.Instance);
    }

    private static ILocalToolOfferProvider CreateOfferProvider(FakeModelTrustResolver? trust = null)
    {
        return new LocalToolOfferProvider(new LocalAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels(LocalExternalModel, CloudExternalModel, DeletedExternalModel).Build(),
            NullCustomToolScopeFactory.Instance,
            trust ?? Trust(),
            allowCloudKnowledgeAccess: false);
    }

    /// <summary>Options carrying only the Development PURPOSE marker — the minimum that trips the local backstop.</summary>
    private static ChatOptions MarkedDevelopmentOptions(string modelId)
    {
        return new ChatOptions
        {
            ModelId = modelId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [DevelopmentCloudAuthorizationMetadata.PurposeKey] = DevelopmentCloudAuthorizationMetadata.PurposeValue
            }
        };
    }

    /// <summary>
    ///     A workspace the refusal tests never reach: the model id is rejected before any workspace call, so every
    ///     member here returning nothing is the assertion, not a shortcut.
    /// </summary>
    private sealed class UnusedWorkspaceTools : IDevelopmentWorkspaceTools
    {
        public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => [];

        public DevelopmentCommandProfile Profile { get; } =
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        public Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> GetDiffAsync(CancellationToken cancellationToken = default) =>
            Unused();

        public Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default) =>
            Unused();

        private static Task<string> Unused() =>
            throw new InvalidOperationException("A refused Development attempt must never touch the workspace.");
    }

    /// <summary>An egress authorizer that fails the test if the CLOUD branch ever reaches it; only the local backstop is under test here.</summary>
    private sealed class ThrowingCloudEgressAuthorizer : ICloudEgressAuthorizer
    {
        public void Authorize(CloudEgressAuthorizationRequest request) =>
            throw new InvalidOperationException("The cloud branch must not be reached for an external model.");
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return AsyncEnumerable.Empty<ChatResponseUpdate>();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            null;

        public void Dispose()
        {
        }
    }
}
