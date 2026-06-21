namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InvocationAgentFactoryTests
{
    [Test]
    public async Task CreateAsync_ReturnsContextWithSeedMessages()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [new ChatMessage(ChatRole.User, "hello")]);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(2, context.SeedMessages.Count);
        AssertEx.Equal(ChatRole.System, context.SeedMessages[0].Role);
        AssertEx.Equal("Be helpful.", context.SeedMessages[0].Text);
        AssertEx.Equal("hello", context.SeedMessages[1].Text);
        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_AppliesResolvedModelToChatOptions()
    {
        var definition = new InvocationAgentDefinition("llama3.2:3b",
            "Be helpful.",
            [],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        AssertEx.Equal("llama3.2:3b", chatOptions.ModelId);
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(true, thinkValue);
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingFalse_SendsThinkFalseToSuppressTemplateReasoning()
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            null,
            false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        // A non-thinking model gets think:FALSE (not an omitted key). think:false is accepted by Ollama (only
        // think:true/<level> 400s) and suppresses the reasoning some GGUF templates emit by default; omitting the
        // field would let that reasoning through and surface a reasoning block even with reasoning off.
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(false, thinkValue);
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingFalseAndReasoningOn_OmitsThinkOption()
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            "on",
            false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        // Binary reasoning ON for a non-thinking model: the think key is OMITTED entirely so the model's default
        // (template) reasoning runs. Sending think:true / a level would 400; only omission lets it through.
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey("think"));
    }

    [Test]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task CreateAsync_WhenSupportsThinkingFalseAndGradedEffort_OmitsThinkOption(string effort)
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            effort,
            false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        // A graded effort carried onto a NON-thinking model (an agent definition pins it, or the composer keeps a
        // stale "medium" across a model switch) still means "reason": the think key is OMITTED so the model's built-in
        // template reasoning runs, NOT think:false which would suppress it. Sending think:<level> would 400.
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey("think"));
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingFalseAndReasoningNone_SendsThinkFalse()
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            "none",
            false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        // Explicit OFF ("none") on a non-thinking model: think:FALSE is sent to suppress the template reasoning.
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(false, thinkValue);
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingTrue_IncludesThinkOption()
    {
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            "high");

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");

        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<string>("think", out var thinkValue));
        AssertEx.Equal("high", thinkValue);

        // The Codex side channel carries the raw effort for a graded level too (read only on the Codex boundary).
        AssertEx.True(additionalProperties.TryGetValue<string>("codex_reasoning_effort", out var codexEffort));
        AssertEx.Equal("high", codexEffort);
    }

    [Test]
    [Arguments("minimal")]
    [Arguments("xhigh")]
    public async Task CreateAsync_WhenSupportsThinkingTrueAndCodexLevel_SendsThinkTrue_AndRawCodexSideChannel(string effort)
    {
        // minimal/xhigh are Codex-only OpenAI Responses levels. Ollama 400s on them as a think level, so the factory
        // collapses think to TRUE (safe on the Ollama path) while preserving the un-collapsed level on the Codex side
        // channel so the Codex boundary can map it with full fidelity.
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            effort);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(true, thinkValue);
        AssertEx.True(additionalProperties.TryGetValue<string>("codex_reasoning_effort", out var codexEffort));
        AssertEx.Equal(effort, codexEffort);
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingTrueAndNoEffort_OmitsCodexSideChannel()
    {
        // Blank/unspecified effort: only `think` is set (no side channel). Guards the no-override byte-identical path.
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.True(additionalProperties.ContainsKey("think"));
        AssertEx.False(additionalProperties.ContainsKey("codex_reasoning_effort"));
        AssertEx.Equal(1, additionalProperties.Count);
    }

    [Test]
    public async Task CreateAsync_WithOfferedNameInRegistry_EnablesToolsAndResolvesExecutable()
    {
        var registry = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "Calculate"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("Calculate")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, registry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedNameNotInRegistry_SkipsToolAndDisablesTools()
    {
        var registry = new FakeToolRegistry(AIFunctionFactory.Create((string input) => input, "Calculate"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.Create("echo", (input, _) => Task.FromResult(input))],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, registry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedClientLocalName_ResolvesFromClientLocalRegistry()
    {
        var clientLocalRegistry = new FakeClientLocalToolRegistry(AIFunctionFactory.Create((string input) => input, "run_in_agent_home"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, clientLocalToolRegistry: clientLocalRegistry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithApprovalRequiredClientLocalTool_ResolvesApprovalWrappedHandler()
    {
        // End-to-end: a ClientLocal tool offered via the envelope path (an offer placeholder) is
        // resolved against the REAL ClientLocalToolRegistry, which wraps a RequiresApproval=true handler in an
        // ApprovalRequiredAIFunction. Prove the wrapped handler flows through the offer→resolve path without being
        // dropped, so the agent builds with tools enabled.
        var registry = new ClientLocalToolRegistry([new ApprovalRequiredFakeHandler("run_in_agent_home", "Runs an agent task.", """{"type":"object"}""")]);
        var resolved = registry.TryResolve("run_in_agent_home", out var wrapped);
        AssertEx.True(resolved);
        AssertEx.True(wrapped is ApprovalRequiredAIFunction, "the high-risk handler must resolve approval-wrapped");

        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, clientLocalToolRegistry: registry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedNameInNeitherRegistry_DisablesTools()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("run_in_agent_home")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.Equal(false, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithOfferedMcpName_ResolvesFromMcpRegistry()
    {
        // Option C: an offered MCP-qualified name that matches neither the built-in nor the ClientLocal registry
        // resolves against the MCP tool registry's cached executable.
        var mcpRegistry = new FakeMcpToolRegistry(AIFunctionFactory.Create((string input) => input, "mcp__weather__get_forecast"));
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("mcp__weather__get_forecast")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, mcpToolRegistry: mcpRegistry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithApprovalWrappedMcpTool_ResolvesWrappedExecutable()
    {
        // An MCP tool is registered approval-wrapped (the catalog default). The factory must resolve the wrapped
        // executable through Option C so the approval gate survives the offer -> resolve path.
        var wrapped = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string input) => input, "mcp__files__write_file"));
        var snapshot = new[]
        {
            new McpRegisteredTool("mcp__files__write_file",
                wrapped,
                new LocalChatToolDescriptor("mcp__files__write_file", "Writes a file.", """{"type":"object"}""", true))
        };
        var mcpRegistry = new FakeMcpToolRegistry();
        mcpRegistry.ReplaceSnapshot(snapshot);

        var resolved = mcpRegistry.TryResolve("mcp__files__write_file", out var executable);
        AssertEx.True(resolved);
        AssertEx.True(executable is ApprovalRequiredAIFunction, "the MCP tool must resolve approval-wrapped");

        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [InvocationToolBridge.CreateOfferPlaceholder("mcp__files__write_file")],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, mcpToolRegistry: mcpRegistry);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.NotNull(context.Agent);
        AssertEx.Equal(true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_OrdersConversationContext_WhenBuildingSeedMessages()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [
                new ChatMessage(ChatRole.User, "first"),
                new ChatMessage(ChatRole.Assistant, "second")
            ]);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.Equal("first", context.SeedMessages[1].Text);
        AssertEx.Equal("second", context.SeedMessages[2].Text);
    }

    [Test]
    public async Task CreateAsync_WhenSamplingProvided_AppliesNativeChatOptions()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                Temperature = 0.3f,
                TopP = 0.85f,
                TopK = 40,
                MaxOutputTokens = 256,
                PresencePenalty = 0.2f,
                FrequencyPenalty = 0.1f,
                Seed = 7,
                Stop = ["END"]
            });

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ResolveChatOptions(context);
        AssertEx.Equal(0.3f, chatOptions.Temperature);
        AssertEx.Equal(0.85f, chatOptions.TopP);
        AssertEx.Equal(40, chatOptions.TopK);
        AssertEx.Equal(256, chatOptions.MaxOutputTokens);
        AssertEx.Equal(0.2f, chatOptions.PresencePenalty);
        AssertEx.Equal(0.1f, chatOptions.FrequencyPenalty);
        AssertEx.Equal(7L, chatOptions.Seed);
        AssertEx.Equal("END", AssertEx.NotNull(chatOptions.StopSequences)[0]);
    }

    [Test]
    public async Task CreateAsync_WhenSamplingProvided_AddsOllamaAdditionalProperties()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                MinP = 0.05f,
                RepeatPenalty = 1.2f,
                RepeatLastN = 128,
                NumCtx = 8192
            });

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<float>("min_p", out var minP));
        AssertEx.Equal(0.05f, minP);
        AssertEx.True(additionalProperties.TryGetValue<float>("repeat_penalty", out var repeatPenalty));
        AssertEx.Equal(1.2f, repeatPenalty);
        AssertEx.True(additionalProperties.TryGetValue<int>("repeat_last_n", out var repeatLastN));
        AssertEx.Equal(128, repeatLastN);
        AssertEx.True(additionalProperties.TryGetValue<int>("num_ctx", out var numCtx));
        AssertEx.Equal(8192, numCtx);
    }

    [Test]
    public async Task CreateAsync_WhenSamplingAbsent_LeavesChatOptionsByteIdentical()
    {
        // No-override guarantee: with no sampling, the factory sets only `think` (the pre-sampling behavior) and leaves
        // every native sampling property null and adds no Ollama option keys.
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ResolveChatOptions(context);
        AssertEx.Null(chatOptions.Temperature);
        AssertEx.Null(chatOptions.TopP);
        AssertEx.Null(chatOptions.TopK);
        AssertEx.Null(chatOptions.MaxOutputTokens);
        AssertEx.Null(chatOptions.Seed);
        AssertEx.Null(chatOptions.StopSequences);

        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey("min_p"));
        AssertEx.False(additionalProperties.ContainsKey("num_ctx"));
        AssertEx.True(additionalProperties.ContainsKey("think"));
        // Only the `think` key is present (no sampling keys leaked).
        AssertEx.Equal(1, additionalProperties.Count);
    }

    [Test]
    public async Task CreateAsync_WhenMaxOutputTokensExceedsNumCtx_ClampsToContextWindow()
    {
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                MaxOutputTokens = 16384,
                NumCtx = 4096
            });

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ResolveChatOptions(context);
        AssertEx.Equal(4096, chatOptions.MaxOutputTokens);
    }

    [Test]
    public async Task CreateAsync_WhenSamplingValuesOutOfRange_SkipsInvalidFields()
    {
        // Defensive guards: NaN/negative/out-of-range values are treated as "no override" and dropped. Covers both the
        // lower bounds (negative/zero) and the upper bounds (temperature > 2, penalty |x| > 2, seed < -1).
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                Temperature = 2.5f,
                TopP = 1.5f,
                MinP = float.NaN,
                TopK = 0,
                PresencePenalty = 3f,
                FrequencyPenalty = -3f,
                Seed = -2
            });

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ResolveChatOptions(context);
        AssertEx.Null(chatOptions.Temperature);
        AssertEx.Null(chatOptions.TopP);
        AssertEx.Null(chatOptions.TopK);
        AssertEx.Null(chatOptions.PresencePenalty);
        AssertEx.Null(chatOptions.FrequencyPenalty);
        AssertEx.Null(chatOptions.Seed);
        AssertEx.False(AssertEx.NotNull(chatOptions.AdditionalProperties).ContainsKey("min_p"));
    }

    [Test]
    public async Task CreateAsync_WhenSamplingValuesAtBoundaryEdges_AppliesThem()
    {
        // The boundary edges are inclusive: temperature 2, penalties ±2, and seed -1 (Ollama's random-seed sentinel)
        // are all valid and must be applied, not dropped.
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                Temperature = 2f,
                PresencePenalty = 2f,
                FrequencyPenalty = -2f,
                Seed = -1
            });

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ResolveChatOptions(context);
        AssertEx.Equal(2f, chatOptions.Temperature);
        AssertEx.Equal(2f, chatOptions.PresencePenalty);
        AssertEx.Equal(-2f, chatOptions.FrequencyPenalty);
        AssertEx.Equal(-1L, chatOptions.Seed);
    }

    // MAAI001: AgentSkillsProvider/AgentInlineSkill are [Experimental] in Microsoft.Agents.AI 1.8.0; the factory adopts
    // them deliberately for progressive disclosure of agent skills, so this test references them under the same
    // scoped suppression the production code uses.
#pragma warning disable MAAI001
    [Test]
    public async Task Factory_WithSkills_AttachesSkillsProvider_EmptyKeepsPositionalCtor()
    {
        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        // No skills: the agent is built via the positional constructor, so no context providers are attached
        // (AIContextProviders is null per MAF when none are configured) — byte-identical to the pre-skills build.
        var noSkills = new InvocationAgentDefinition("qwen3.5:0.8b", "Be helpful.", [], []);
        await using var noSkillsContext = await sut.CreateAsync(noSkills);
        var noSkillsAgent = noSkillsContext.Agent as ChatClientAgent
                            ?? throw new AssertionException("Expected a ChatClientAgent.");
        AssertEx.True(noSkillsAgent.AIContextProviders is null or { Count: 0 },
            "A no-skills agent must attach no context providers.");

        // With skills: the agent is built via the options constructor with an AgentSkillsProvider attached, and the
        // instructions/name still flow through (proving the options path wires them, since MAF 1.8.0 has no Instructions
        // property on ChatClientAgentOptions — they ride ChatOptions.Instructions).
        var withSkills = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [],
            [],
            Skills:
            [
                new InvocationSkill("kubernetes-debug", "Debug k8s issues", "## Body"),
                new InvocationSkill("log-triage", "Triage logs", "## Logs")
            ]);
        await using var withSkillsContext = await sut.CreateAsync(withSkills);
        var withSkillsAgent = withSkillsContext.Agent as ChatClientAgent
                              ?? throw new AssertionException("Expected a ChatClientAgent.");
        var providers = AssertEx.NotNull(withSkillsAgent.AIContextProviders, "A skills agent must attach a context provider.");
        AssertEx.Equal(1, providers.Count);
        AssertEx.True(providers[0] is AgentSkillsProvider, "The attached provider must be an AgentSkillsProvider.");
        AssertEx.Equal("Be helpful.", withSkillsAgent.Instructions);
        AssertEx.Equal("XeInvocation-qwen3.5:0.8b", withSkillsAgent.Name);
    }
#pragma warning restore MAAI001

    private static ChatOptions ResolveChatOptions(InvocationAgentContext context)
    {
        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        return runOptions.ChatOptions
               ?? throw new AssertionException("Expected ChatOptions to be populated.");
    }

    private static InvocationAgentFactory CreateSut(FakeChatClient chatClient,
        IAgentToolRegistry? toolRegistry = null,
        IClientLocalToolRegistry? clientLocalToolRegistry = null,
        IMcpToolRegistry? mcpToolRegistry = null)
    {
        return new InvocationAgentFactory(chatClient,
            Options.Create(new InvocationAgentOptions()),
            NullLogger<InvocationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            FakeServiceProvider.Instance,
            toolRegistry ?? new FakeToolRegistry(),
            clientLocalToolRegistry ?? new FakeClientLocalToolRegistry(),
            mcpToolRegistry ?? new FakeMcpToolRegistry());
    }

    private sealed class FakeToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<AITool> _tools;

        public FakeToolRegistry(params AITool[] tools)
        {
            _tools = tools;
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return _tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return
            [
                .. _tools.OfType<AIFunction>()
                         .Select(static function => new LocalChatToolDescriptor(function.Name, function.Description, function.JsonSchema.GetRawText(), false))
            ];
        }
    }

    private sealed class FakeClientLocalToolRegistry : IClientLocalToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeClientLocalToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(toolName, out tool);
        }
    }

    private sealed class FakeMcpToolRegistry : IMcpToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeMcpToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(name, out tool);
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return
            [
                .. _tools.Values.OfType<AIFunction>()
                         .Select(static function => new LocalChatToolDescriptor(function.Name, function.Description, function.JsonSchema.GetRawText(), true))
            ];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            _tools.Clear();
            foreach (var tool in tools)
            {
                _tools[tool.Name] = tool.Executable;
            }
        }
    }

    private sealed class ApprovalRequiredFakeHandler(string toolName, string description, string parameterSchema)
        : IClientLocalToolHandler
    {
        public string ToolName => toolName;

        public string Description => description;

        public string ParameterSchema => parameterSchema;

        public bool RequiresApproval => true;

        public Task<string> ExecuteAsync(string jsonArguments, CancellationToken cancellationToken = default)
        {
            return Task.FromResult("ok");
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public static FakeServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
