namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The pre-send context estimate: the fixed parts of the next request (prompt, offered tools, preamble) summed with
///     the outer budgeter's charges, the window and its reserves, and a null answer for a model the node does not know.
/// </summary>
/// <remarks>Send-path parity lives in <c>NodeChatStreamServiceTests</c>, beside the fakes the send path is built from.</remarks>
[Category(TestCategories.Unit)]
public sealed class ChatContextEstimateServiceTests
{
    private const string LocalModel = "local-model";
    private const string CloudModel = "cloud-model";
    private const string DeclaredCloudModel = "declared-cloud-model";
    private const string UndeclaredCloudModel = "undeclared-cloud-model";
    private const int DeclaredCloudWindow = 400_000;
    private const string DeclaredExternalModel = "declared-external-model";
    private const int DeclaredExternalWindow = 131_072;
    private const string UnknownModel = "unknown-model";
    private const string NoToolsModel = "local-without-tools";
    private const int LocalWindow = 32768;
    private const int NodeDefaultWindow = 8192;
    private const string ColdGgufModel = "cold-gguf";
    private const string WarmGgufModel = "warm-gguf";
    private const int GgufTrainCeiling = 262144;
    private const int WarmGgufLaunchedWindow = 16384;
    private const int Preamble = 123;

    [Test]
    public async Task Estimate_SumsPromptToolsAndPreamble_AndDerivesReserveUsableAndMargin()
    {
        var harness = new Harness(CreateTool("get_time"), CreateTool("read_file"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel
        }));

        var expected = AssertEx.NotNull(estimate.Estimated);
        AssertEx.Equal(NodeChatContextWindowKind.PreSendEstimate, estimate.Kind);
        AssertEx.Equal(LocalModel, estimate.ModelId);
        AssertEx.Equal("get_time,read_file", string.Join(",", estimate.Tools.Select(static tool => tool.Name)));
        AssertEx.True(expected.SystemPromptTokens > 0, "the embedded default prompt costs tokens");
        AssertEx.Equal(estimate.Tools.Sum(static tool => tool.Tokens), expected.ToolSchemaTokens);
        AssertEx.Equal(Preamble, expected.ToolTemplatePreambleTokens);
        AssertEx.Equal(expected.SystemPromptTokens + expected.ToolSchemaTokens + expected.ToolTemplatePreambleTokens, expected.TotalTokens);
        AssertEx.Equal(0, expected.InstructionsTokens + expected.KnowledgeTokens + expected.AttachmentTokens + expected.CompactionTokens + expected.ConversationTokens);

        var reserve = new ConversationContextBudgetOptions().ReservedOutputTokenFloor;
        AssertEx.Equal(LocalWindow, estimate.WindowTokens);
        AssertEx.Equal(reserve, estimate.ReservedOutputTokens);
        AssertEx.Equal(TokenEstimatorCalibrationStore.ApplyEstimateMargins(LocalWindow, TokenEstimatorCalibrationStore.NeutralObservedCorrection) - reserve, estimate.UsableWindowTokens);
        AssertEx.Equal(LocalWindow - reserve - estimate.UsableWindowTokens, estimate.SafetyMarginTokens);
        AssertEx.True(estimate.SafetyMarginTokens > 0, "the safety factor withholds part of the window");

        AssertEx.Null(estimate.ProviderInputTokens);
        AssertEx.Null(estimate.Trimmed);
        AssertEx.Equal(0, estimate.ToolsWithheldCount);
    }

    [Test]
    public async Task Estimate_CapsTheToolList_ButChargesEveryOfferedTool()
    {
        var offered = Enumerable.Range(0, NodeChatContextWindowDto.MaxToolEntries + 6).Select(static index => CreateTool($"tool_{index:D3}")).ToArray();
        var harness = new Harness(offered);

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel
        }));

        AssertEx.Equal(NodeChatContextWindowDto.MaxToolEntries, estimate.Tools.Count);
        var expected = AssertEx.NotNull(estimate.Estimated);
        var divisor = harness.Estimator.ResolveDivisor(LocalModel);
        var everyTool = InvocationRunner.BuildToolBudgetDefinitions(offered).Sum(definition => ConversationContextBudgeter.EstimateToolDefinitionTokens(harness.Estimator, definition, divisor));
        AssertEx.Equal(everyTool, expected.ToolSchemaTokens);
        AssertEx.True(expected.ToolSchemaTokens > estimate.Tools.Sum(static tool => tool.Tokens), "the tools past the cap are still charged");
    }

    [Test]
    public async Task Estimate_ForABoundAgent_MeasuresItsPromptAndNarrowedOffer()
    {
        var agentId = Guid.NewGuid();
        var narrow = CreateTool("get_time");
        var harness = new Harness(narrow, CreateTool("read_file"));
        harness.AgentResolver.ResolveAsync(agentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(new ResolvedAgentRuntime("Agent persona.", [narrow], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 2, agentId, "Narrow"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel,
            AgentId = agentId
        }));

        AssertEx.Equal("get_time", string.Join(",", estimate.Tools.Select(static tool => tool.Name)));
        AssertEx.Equal(ConversationContextBudgeter.EstimateSystemPromptTokens(harness.Estimator, "Agent persona.", harness.Estimator.ResolveDivisor(LocalModel)),
            AssertEx.NotNull(estimate.Estimated).SystemPromptTokens);
    }

    [Test]
    public async Task Estimate_OnAModelWithoutTools_ChargesNoToolsAndNoPreamble()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = NoToolsModel
        }));

        var expected = AssertEx.NotNull(estimate.Estimated);
        AssertEx.Empty(estimate.Tools);
        AssertEx.Equal(0, expected.ToolSchemaTokens);
        AssertEx.Equal(0, expected.ToolTemplatePreambleTokens);
        AssertEx.Equal(expected.SystemPromptTokens, expected.TotalTokens);
    }

    [Test]
    public async Task Estimate_WithLocalToolsOff_ChargesNoToolsAndNoPreamble()
    {
        // The send honors the composer's local-tools toggle; with it off the round offers nothing, so neither may the estimate.
        var harness = new Harness(CreateTool("get_time"), CreateTool("read_file"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel,
            UseLocalTools = false
        }));

        var expected = AssertEx.NotNull(estimate.Estimated);
        AssertEx.Empty(estimate.Tools);
        AssertEx.Equal(0, expected.ToolSchemaTokens);
        AssertEx.Equal(0, expected.ToolTemplatePreambleTokens);
        AssertEx.True(expected.SystemPromptTokens > 0, "the prompt is still charged");
        AssertEx.Equal(expected.SystemPromptTokens, expected.TotalTokens);
    }

    [Test]
    public async Task Estimate_WithLocalToolsOff_StillChargesAnAgentsOwnToolList()
    {
        // An agent naming its own tools is offered them whatever the toggle says, so the estimate charges them too.
        var agentId = Guid.NewGuid();
        var own = CreateTool("run_python");
        var harness = new Harness(own, CreateTool("read_file"));
        harness.AgentResolver.ResolveAsync(agentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(new ResolvedAgentRuntime("Agent persona.", [own], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Mathematician",
                   DeclaresToolList: true));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel,
            AgentId = agentId,
            UseLocalTools = false
        }));

        AssertEx.Equal("run_python", string.Join(",", estimate.Tools.Select(static tool => tool.Name)));
        AssertEx.Equal(Preamble, AssertEx.NotNull(estimate.Estimated).ToolTemplatePreambleTokens);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Estimate_WithLocalToolsOff_ChargesNoToolsForAnAgentWithoutItsOwnList(bool defaultAssistant)
    {
        // The Default Assistant resolves to the node-wide offer and an empty-list agent to ask_user alone; neither declares
        // a list, so the toggle still gates both.
        var agentId = Guid.NewGuid();
        AllowedToolDto[] resolvedTools = defaultAssistant ? [CreateTool("get_time"), CreateTool("read_file")] : [CreateTool("ask_user")];
        var harness = new Harness(CreateTool("get_time"), CreateTool("read_file"));
        harness.AgentResolver.ResolveAsync(agentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(new ResolvedAgentRuntime("Agent persona.", resolvedTools, ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Agent"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel,
            AgentId = agentId,
            UseLocalTools = false
        }));

        AssertEx.Empty(estimate.Tools);
        AssertEx.Equal(0, AssertEx.NotNull(estimate.Estimated).ToolTemplatePreambleTokens);
    }

    [Test]
    public async Task Estimate_WithNodeToolsOff_ChargesNoToolsEvenForAnAgentsOwnList()
    {
        // The node-wide EnableTools switch outranks an agent's own list.
        var agentId = Guid.NewGuid();
        var own = CreateTool("run_python");
        var harness = new Harness(enableTools: false, own);
        harness.AgentResolver.ResolveAsync(agentId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
               .Returns(new ResolvedAgentRuntime("Agent persona.", [own], ModelProfile: null, ReasoningEffort: null, AgentDefinitionVersion: 1, agentId, "Mathematician",
                   DeclaresToolList: true));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel,
            AgentId = agentId,
            UseLocalTools = false
        }));

        AssertEx.Empty(estimate.Tools);
        AssertEx.Equal(0, AssertEx.NotNull(estimate.Estimated).ToolSchemaTokens);
    }

    [Test]
    public async Task Estimate_ForACloudModelWithoutLocalDetails_UsesTheNodeDefaultWindow()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = CloudModel
        }));

        AssertEx.Equal(NodeDefaultWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_ForACloudModelWithADeclaredWindow_UsesItOverTheNodeDefault()
    {
        // Larger than the node default on purpose: a declared cloud window replaces the default, it is not clamped to it.
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = DeclaredCloudModel
        }));

        AssertEx.Equal(DeclaredCloudWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_ForARecognisedCloudModelWithAnUnknownWindow_UsesTheNodeDefault()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = UndeclaredCloudModel
        }));

        AssertEx.Equal(NodeDefaultWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_ForAColdGguf_CapsTheTrainCeilingAtTheNodeDefault()
    {
        // A pre-launch turn budgets against the node default, not the train ceiling, so the estimate must not promise more.
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = ColdGgufModel
        }));

        AssertEx.Equal(NodeDefaultWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_ForAWarmGguf_UsesTheLaunchedWindow()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = WarmGgufModel
        }));

        AssertEx.Equal(WarmGgufLaunchedWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxBelowTheLaunchedWindow_TakesTheRequest()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = WarmGgufModel,
            NumCtx = 4096
        }));

        AssertEx.Equal(4096, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxAboveTheLaunchedWindow_IsCappedByIt()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = WarmGgufModel,
            NumCtx = 65536
        }));

        AssertEx.Equal(WarmGgufLaunchedWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxAboveADeclaredCloudWindow_IsCappedByIt()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = DeclaredCloudModel,
            NumCtx = DeclaredCloudWindow + 1
        }));

        AssertEx.Equal(DeclaredCloudWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxBelowADeclaredCloudWindow_TakesTheRequest()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = DeclaredCloudModel,
            NumCtx = 4096
        }));

        AssertEx.Equal(4096, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxAboveADeclaredExternalWindow_IsCappedByIt()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = DeclaredExternalModel,
            NumCtx = DeclaredExternalWindow + 1
        }));

        AssertEx.Equal(DeclaredExternalWindow, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxBelowADeclaredExternalWindow_TakesTheRequest()
    {
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = DeclaredExternalModel,
            NumCtx = 4096
        }));

        AssertEx.Equal(4096, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithANumCtxOnAColdModel_TakesTheRequestAsIs()
    {
        // No launched window is known, so the send path budgets the requested window unchanged, above the node default too.
        var harness = new Harness(CreateTool("get_time"));

        var estimate = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = ColdGgufModel,
            NumCtx = 20000
        }));

        AssertEx.Equal(20000, estimate.WindowTokens);
    }

    [Test]
    public async Task Estimate_WithMaxOutputAboveTheFloor_RaisesTheReserveAndShrinksUsable()
    {
        var harness = new Harness(CreateTool("get_time"));
        var floor = new ConversationContextBudgetOptions().ReservedOutputTokenFloor;

        var baseline = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel
        }));
        var widened = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = LocalModel,
            MaxOutputTokens = floor + 3000
        }));

        AssertEx.Equal(floor, baseline.ReservedOutputTokens);
        AssertEx.Equal(floor + 3000, widened.ReservedOutputTokens);
        AssertEx.Equal(baseline.UsableWindowTokens - 3000, widened.UsableWindowTokens);
        AssertEx.Equal(baseline.WindowTokens, widened.WindowTokens);
    }

    [Test]
    [Arguments(0)]
    [Arguments(-5)]
    public async Task Estimate_WithNonPositiveOverrides_IgnoresThem(int value)
    {
        var harness = new Harness(CreateTool("get_time"));

        var baseline = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = WarmGgufModel
        }));
        var overridden = AssertEx.NotNull(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = WarmGgufModel,
            NumCtx = value,
            MaxOutputTokens = value
        }));

        AssertEx.Equal(WarmGgufLaunchedWindow, overridden.WindowTokens);
        AssertEx.Equal(baseline.ReservedOutputTokens, overridden.ReservedOutputTokens);
        AssertEx.Equal(baseline.UsableWindowTokens, overridden.UsableWindowTokens);
    }

    [Test]
    public async Task Estimate_ForAModelTheNodeDoesNotKnow_IsNull()
    {
        var harness = new Harness(CreateTool("get_time"));

        AssertEx.Null(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = UnknownModel
        }));
    }

    [Test]
    public async Task Estimate_WithNoModelAndNoInstalledDefault_IsNull()
    {
        var harness = new Harness(CreateTool("get_time"));

        AssertEx.Null(await harness.Service.EstimateAsync(new ChatContextEstimateRequest
        {
            ModelName = " "
        }));
    }

    private static AllowedToolDto CreateTool(string name)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = $"Does {name}.",
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"string\"}}}",
            RequiresApproval = false,
            Category = ToolCategory.ReadLocal
        };
    }

    /// <summary>The service over the real turn resolver, offer gate and heuristic estimator, with model facts faked by name.</summary>
    private sealed class Harness
    {
        public Harness(params AllowedToolDto[] offered)
            : this(enableTools: true, offered)
        {
        }

        public Harness(bool enableTools, params AllowedToolDto[] offered)
        {
            AgentResolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                         .Returns((ResolvedAgentRuntime?)null);
            var agentStore = Substitute.For<IAgentDefinitionStore>();
            agentStore.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AgentDefinitionRecord?)null);
            var orchestration = Substitute.For<IOrchestrationResolver>();
            var turnResolver = new ChatTurnResolver(AgentResolver, agentStore, orchestration, new NamedModelCapabilities(), NullLogger<ChatTurnResolver>.Instance);

            var offerProvider = Substitute.For<ILocalToolOfferProvider>();
            offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<ExternalProviderCloudGrants?>(), Arg.Any<CancellationToken>()).Returns(offered);
            var defaultAgent = Substitute.For<IDefaultAgentProvider>();
            defaultAgent.GetDefaultAgentIdAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<Guid?>(null));
            var nodeSettings = Substitute.For<INodeSettingsStore>();
            nodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
            var localDefault = Substitute.For<ILocalDefaultChatModelResolver>();
            localDefault.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>(null));

            var details = Substitute.For<ILocalModelDetailsResolver>();
            details.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new LocalModelDetailsResolution.NoLocalDetails());
            details.ResolveAsync(Arg.Is<string>(static name => string.Equals(name, LocalModel, StringComparison.Ordinal) || string.Equals(name, NoToolsModel, StringComparison.Ordinal)),
                       Arg.Any<CancellationToken>())
                   .Returns(new LocalModelDetailsResolution.Ollama(new OllamaModelDetails
                   {
                       MaxContextTokens = LocalWindow,
                       Capabilities = ["completion", "tools"]
                   }));

            details.ResolveAsync(DeclaredCloudModel, Arg.Any<CancellationToken>()).Returns(new LocalModelDetailsResolution.Cloud(DeclaredCloudWindow));
            details.ResolveAsync(DeclaredExternalModel, Arg.Any<CancellationToken>()).Returns(new LocalModelDetailsResolution.External(new ExternalProviderModelRegistration
            {
                Connection = new ExternalProviderConnectionDescriptor
                {
                    Id = "external-box",
                    DisplayName = "External box",
                    BaseUrl = new Uri("http://127.0.0.1:18099/v1/"),
                    Locality = ExternalProviderLocality.Local,
                    CloudGrants = ExternalProviderCloudGrants.None
                },
                Model = new ExternalProviderModelDescriptor
                {
                    WireId = DeclaredExternalModel,
                    ContextLength = DeclaredExternalWindow
                }
            }));
            details.ResolveAsync(UndeclaredCloudModel, Arg.Any<CancellationToken>()).Returns(new LocalModelDetailsResolution.Cloud(ContextLength: null));
            details.ResolveAsync(ColdGgufModel, Arg.Any<CancellationToken>()).Returns(new LocalModelDetailsResolution.Gguf(CreateGgufDescriptor(ColdGgufModel), EffectiveContextTokens: null));
            details.ResolveAsync(WarmGgufModel, Arg.Any<CancellationToken>()).Returns(new LocalModelDetailsResolution.Gguf(CreateGgufDescriptor(WarmGgufModel), WarmGgufLaunchedWindow));

            var calibration = new TokenEstimatorCalibrationStore();
            calibration.SetToolTemplatePreamble(LocalModel, Preamble);
            Estimator = new HeuristicTokenEstimator(calibration);

            Service = new ChatContextEstimateService(turnResolver,
                defaultAgent,
                nodeSettings,
                localDefault,
                StubNodeRuntimeSettings.Create().WithEnableTools(enableTools).WithDefaultContextTokens(NodeDefaultWindow).Build(),
                offerProvider,
                new PermissiveToolApprovalPolicy(),
                Options.Create(new LocalChatAgentOptions()),
                details,
                Estimator,
                Options.Create(new ConversationContextBudgetOptions()));
        }

        private static LocalModelDescriptor CreateGgufDescriptor(string name)
        {
            return new LocalModelDescriptor
            {
                ModelName = name,
                ProviderName = "llama.cpp",
                IsAvailable = true,
                SizeBytes = null,
                ModifiedAt = null,
                MaxContextTokens = GgufTrainCeiling
            };
        }

        public IAgentDefinitionResolver AgentResolver { get; } = Substitute.For<IAgentDefinitionResolver>();

        public HeuristicTokenEstimator Estimator { get; }

        public ChatContextEstimateService Service { get; }
    }

    /// <summary>Advertises tools unless the name says "without-tools"; cloud only when the name says "cloud".</summary>
    private sealed class NamedModelCapabilities : IModelCapabilityResolver
    {
        public Task<ModelCapabilitySnapshot> ResolveAsync(string? model, CancellationToken cancellationToken) =>
            Task.FromResult(new ModelCapabilitySnapshot(SupportsThinking: false,
                model is not null && !model.Contains("without-tools", StringComparison.Ordinal),
                model?.Contains("cloud", StringComparison.Ordinal) == true));
    }
}
