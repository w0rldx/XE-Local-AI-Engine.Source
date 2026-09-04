namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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
    // The stable half of ReasoningBudgetSkipLog's message: the factory logs other lines per call, so assertions on
    // the skip notice select it by this fragment rather than by the whole entry count.
    private const string BudgetSkipMessageFragment = "cannot enforce a per-request thinking budget";

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
        AssertEx.Equal(expected: 2, context.SeedMessages.Count);
        AssertEx.Equal(ChatRole.System, context.SeedMessages[0].Role);
        AssertEx.Equal("Be helpful.", context.SeedMessages[0].Text);
        AssertEx.Equal("hello", context.SeedMessages[1].Text);
        AssertEx.Equal(expected: false, context.Items["toolsEnabled"]);
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
        AssertEx.Equal(expected: true, thinkValue);
    }

    [Test]
    public async Task CreateAsync_WithAResponseJsonSchema_ConstrainsTheTurnToIt()
    {
        // Constrained decoding for a caller whose reply is parsed strictly (the benchmark judge). The MEAI OpenAI
        // adapter maps ChatOptions.ResponseFormat onto response_format.json_schema.schema, which is the only path
        // llama-server reads before compiling the schema into a grammar.
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{"score":{"type":"integer"}}}""");
        var definition = new InvocationAgentDefinition("llama3.2:3b",
            "Be helpful.",
            [],
            [],
            ResponseJsonSchema: schema.RootElement.Clone());

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var responseFormat = AssertEx.NotNull(chatOptions.ResponseFormat as ChatResponseFormatJson);
        var wireSchema = responseFormat.Schema ?? throw new AssertionException("Expected the response format to carry a schema.");
        AssertEx.True(wireSchema.GetProperty("properties").TryGetProperty("score", out _),
            "The caller's schema must survive onto the response format unchanged.");
    }

    [Test]
    public async Task CreateAsync_WithNoResponseJsonSchema_LeavesTheTurnUnconstrained()
    {
        // The no-override guarantee: every path but the judge leaves this null, and null must send no response_format
        // at all rather than a permissive one.
        var definition = new InvocationAgentDefinition("llama3.2:3b", "Be helpful.", [], []);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        AssertEx.Null(((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!.ResponseFormat);
    }

    [Test]
    public async Task CreateAsync_WhenEffectiveContextKnown_WritesItAsNumCtx()
    {
        // The runtime's effective context window is carried as num_ctx so the inner provider-round budgeter
        // sizes against the same window the outer conversation budgeter uses. No per-send override is set here.
        var definition = new InvocationAgentDefinition("llama3.2:3b",
            "Be helpful.",
            [],
            [],
            EffectiveContextTokens: 16384);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<int>("num_ctx", out var numCtx));
        AssertEx.Equal(expected: 16384, numCtx);
    }

    [Test]
    public async Task CreateAsync_WhenPerSendNumCtxIsSmaller_PreservesIt()
    {
        // A per-send num_ctx override must win over the runtime effective-context fallback.
        var definition = new InvocationAgentDefinition("llama3.2:3b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                NumCtx = 4096
            },
            EffectiveContextTokens: 16384);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<int>("num_ctx", out var numCtx));
        AssertEx.Equal(expected: 4096, numCtx);
    }

    [Test]
    public async Task CreateAsync_WhenPerSendNumCtxExceedsProcess_ClampsIt()
    {
        var definition = new InvocationAgentDefinition("llama3.2:3b",
            "Be helpful.",
            [],
            [],
            Sampling: new InvocationSamplingOptions
            {
                NumCtx = 65536,
                MaxOutputTokens = 32768
            },
            EffectiveContextTokens: 8192);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<int>("num_ctx", out var numCtx));
        AssertEx.Equal(expected: 8192, numCtx);
        AssertEx.Equal(expected: 8192, chatOptions.MaxOutputTokens);
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingFalse_SendsThinkFalseToSuppressTemplateReasoning()
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            ReasoningEffort: null,
            SupportsThinking: false);

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
        AssertEx.Equal(expected: false, thinkValue);
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingFalseAndReasoningOn_OmitsThinkOption()
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            "on",
            SupportsThinking: false);

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
            SupportsThinking: false);

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

    /// <summary>
    ///     Regression pin. A NATIVE-reasoning model (the OpenAI harmony family, e.g. gpt-oss-20b) reports
    ///     <c>SupportsThinking=false</c> by design — its template bakes reasoning onto a <c>&lt;|channel|&gt;analysis</c>
    ///     channel with no graded switch — and reasons only because this branch OMITS the think field. Detecting native
    ///     reasoning as a second capability must NOT change that: adding the harmony markers to the GRADED list instead
    ///     would flip the model into the <c>if (definition.SupportsThinking)</c> arm, which writes <c>think</c> and, on
    ///     effort <c>none</c>, the <c>enable_thinking=false</c> marker — a kwarg the harmony template does not have
    ///     (measured: 0 occurrences). Both keys are asserted ABSENT here, so that mistake fails loudly.
    /// </summary>
    [Test]
    [Arguments("on")]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task CreateAsync_WhenNativeReasoningModel_OmitsThinkAndNeverDisablesTemplateThinking(string effort)
    {
        // A native-reasoning model reaches the factory exactly as gpt-oss does today: SupportsThinking FALSE.
        var definition = new InvocationAgentDefinition("unsloth/gpt-oss-20b-GGUF:Q5_K_M",
            "Be helpful.",
            [],
            [],
            effort,
            SupportsThinking: false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        var chatOptions = runOptions.ChatOptions
                          ?? throw new AssertionException("Expected ChatOptions to be populated.");
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);

        AssertEx.False(additionalProperties.ContainsKey("think"),
            "a native-reasoning model must keep the omit-think path so its template reasoning runs");
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaDisableThinkingMarkerKey),
            "a native-reasoning model must never be sent enable_thinking=false — the harmony template has no such kwarg");
    }

    [Test]
    public async Task CreateAsync_WhenSupportsThinkingFalseAndReasoningNone_SendsThinkFalse()
    {
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            "none",
            SupportsThinking: false);

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
        AssertEx.Equal(expected: false, thinkValue);
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

    // Defence in depth for the `auto` reasoning effort. `auto` is resolved into a concrete tier by the node's
    // invocation runner BEFORE the definition is built, so this factory must never see it. If one ever leaks past the
    // runner, it must behave exactly as any UNRECOGNIZED value already does — think:true (reason), no budget marker,
    // no Codex effort, no external marker — so nothing new can reach a provider wire.
    [Test]
    public async Task CreateAsync_WhenEffortIsAuto_ProducesTheUnrecognizedValueBehaviour()
    {
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            "auto");

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);

        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(expected: true, thinkValue);
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaDisableThinkingMarkerKey));
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaReasoningBudgetMarkerKey));
        AssertEx.False(additionalProperties.ContainsKey(ReasoningOptionsResolver.CodexReasoningEffortKey));
        AssertEx.False(additionalProperties.ContainsKey(ReasoningOptionsResolver.ExternalReasoningEffortMarkerKey));
    }

    [Test]
    public async Task CreateAsync_ThinkingCapableReasoningNone_SetsLlamaDisableThinkingMarker()
    {
        // Reasoning OFF on a thinking-capable model. think:false suppresses reasoning on the Ollama wire, but
        // the llama.cpp OpenAI adapter drops think, so the factory also flags the turn for the llama.cpp chat client to
        // inject chat_template_kwargs.enable_thinking=false — otherwise a Qwen3-class template keeps emitting reasoning.
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            "none");

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<bool>("think", out var thinkValue));
        AssertEx.Equal(expected: false, thinkValue);
        AssertEx.True(additionalProperties.TryGetValue<bool>(InvocationAgentFactory.LlamaDisableThinkingMarkerKey, out var marker));
        AssertEx.Equal(expected: true, marker);
    }

    [Test]
    [Arguments("high")]
    [Arguments("low")]
    [Arguments(null)]
    public async Task CreateAsync_ThinkingCapableReasoningOn_OmitsLlamaDisableThinkingMarker(string? effort)
    {
        // Reasoning ON (a graded level, or the default when unspecified) must NOT disable thinking — the marker is absent
        // so the request stays byte-identical and the model reasons.
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            effort);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaDisableThinkingMarkerKey));
    }

    [Test]
    [Arguments("minimal", 1024)]
    [Arguments("low", 2048)]
    [Arguments("medium", 8192)]
    [Arguments("high", 24576)]
    [Arguments("xhigh", 24576)]
    public async Task CreateAsync_ThinkingCapableWithGradedEffort_CarriesTheMappedReasoningBudget(string effort, int expectedBudget)
    {
        // The effort→budget table. Without a budget llama-server lets a Qwen3-class model reason until the context
        // window is exhausted and the turn returns no final answer; the marker is what the llama.cpp chat client patches
        // onto the body as reasoning_budget_tokens. "xhigh" is capped at the same budget as "high" so a Codex-only level
        // pinned onto a local model cannot buy MORE thinking than the highest local level.
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            effort);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<int>(InvocationAgentFactory.LlamaReasoningBudgetMarkerKey, out var budget));
        AssertEx.Equal(expectedBudget, budget);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("none")]
    [Arguments("on")]
    public async Task CreateAsync_ThinkingCapableWithoutGradedEffort_OmitsTheReasoningBudget(string? effort)
    {
        // No explicit graded effort → no budget at all, preserving the unrestricted pre-budget behavior (and, for
        // "none", reasoning is being turned off outright, where a budget would be meaningless).
        var definition = new InvocationAgentDefinition("qwen3:8b",
            "Be helpful.",
            [],
            [],
            effort);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaReasoningBudgetMarkerKey));
    }

    [Test]
    [Arguments("low")]
    [Arguments("medium")]
    [Arguments("high")]
    public async Task CreateAsync_NonThinkingModelWithGradedEffort_OmitsTheReasoningBudget(string effort)
    {
        // A model without the graded thinking capability (including the native-reasoning harmony family) has no
        // think-end tag for llama-server to close the reasoning phase on, so the budget would be a no-op — it is never
        // set, keeping that path byte-identical.
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            effort,
            SupportsThinking: false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaReasoningBudgetMarkerKey));
    }

    [Test]
    public async Task CreateAsync_NonThinkingModelReasoningNone_OmitsLlamaDisableThinkingMarker()
    {
        // A non-thinking model has no default reasoning to disable — think:false alone is enough and no llama.cpp
        // enable_thinking switch is meaningful, so the marker is never set (the else branch takes no action).
        var definition = new InvocationAgentDefinition("gemma:12b",
            "Be helpful.",
            [],
            [],
            "none",
            SupportsThinking: false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var chatOptions = ((ChatClientAgentRunOptions)context.RunOptions!).ChatOptions!;
        var additionalProperties = AssertEx.NotNull(chatOptions.AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaDisableThinkingMarkerKey));
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
        AssertEx.Equal(expected: true, thinkValue);
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
        AssertEx.Equal(expected: 1, additionalProperties.Count);
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
        AssertEx.Equal(expected: true, context.Items["toolsEnabled"]);
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
        AssertEx.Equal(expected: false, context.Items["toolsEnabled"]);
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
        AssertEx.Equal(expected: true, context.Items["toolsEnabled"]);
    }

    [Test]
    public async Task CreateAsync_WithApprovalRequiredClientLocalTool_ResolvesApprovalWrappedHandler()
    {
        // End-to-end: a ClientLocal tool offered via the envelope path (an offer placeholder) is
        // resolved against the REAL ClientLocalToolRegistry, which wraps a RequiresApproval=true handler in an
        // ApprovalRequiredAIFunction. Prove the wrapped handler flows through the offer→resolve path without being
        // dropped, so the agent builds with tools enabled.
        var registry = new ClientLocalToolRegistry([new ApprovalRequiredFakeHandler("run_in_agent_home", "Runs an agent task.", parameterSchema: """{"type":"object"}""")],
            Options.Create(new AgentToolPipelineOptions()));
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
        AssertEx.Equal(expected: true, context.Items["toolsEnabled"]);
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

        AssertEx.Equal(expected: false, context.Items["toolsEnabled"]);
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
        AssertEx.Equal(expected: true, context.Items["toolsEnabled"]);
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
                new LocalChatToolDescriptor("mcp__files__write_file", "Writes a file.", ParameterSchema: """{"type":"object"}""", RequiresApproval: true))
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
        AssertEx.Equal(expected: true, context.Items["toolsEnabled"]);
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
        AssertEx.Equal(expected: 0.3f, chatOptions.Temperature);
        AssertEx.Equal(expected: 0.85f, chatOptions.TopP);
        AssertEx.Equal(expected: 40, chatOptions.TopK);
        AssertEx.Equal(expected: 256, chatOptions.MaxOutputTokens);
        AssertEx.Equal(expected: 0.2f, chatOptions.PresencePenalty);
        AssertEx.Equal(expected: 0.1f, chatOptions.FrequencyPenalty);
        AssertEx.Equal(expected: 7L, chatOptions.Seed);
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
        AssertEx.Equal(expected: 0.05f, minP);
        AssertEx.True(additionalProperties.TryGetValue<float>("repeat_penalty", out var repeatPenalty));
        AssertEx.Equal(expected: 1.2f, repeatPenalty);
        AssertEx.True(additionalProperties.TryGetValue<int>("repeat_last_n", out var repeatLastN));
        AssertEx.Equal(expected: 128, repeatLastN);
        AssertEx.True(additionalProperties.TryGetValue<int>("num_ctx", out var numCtx));
        AssertEx.Equal(expected: 8192, numCtx);
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
        AssertEx.Equal(expected: 1, additionalProperties.Count);
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
        AssertEx.Equal(expected: 4096, chatOptions.MaxOutputTokens);
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
        AssertEx.Equal(expected: 2f, chatOptions.Temperature);
        AssertEx.Equal(expected: 2f, chatOptions.PresencePenalty);
        AssertEx.Equal(expected: -2f, chatOptions.FrequencyPenalty);
        AssertEx.Equal(expected: -1L, chatOptions.Seed);
    }

    // MAAI001: AgentSkillsProvider/AgentInlineSkill were [Experimental] in Microsoft.Agents.AI 1.8.0. The scoped
    // suppression remains at the pinned 1.15.0 until explicit graduation evidence is available; the factory adopts them
    // deliberately for progressive disclosure of agent skills, so
    // this test references them under the same scoped suppression the production code uses.
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
        var noSkillsAgent = ResolveChatClientAgent(noSkillsContext.Agent);
        AssertEx.True(noSkillsAgent.AIContextProviders is null or { Count: 0 },
            "A no-skills agent must attach no context providers.");

        // With skills: the agent is built via the options constructor with an AgentSkillsProvider attached. The agent
        // carries NO instructions on either path — the system instructions are delivered once by the seed system
        // message (see the InstructionsDeliveredOnce wire tests), so the skills path must match the no-skills path and
        // leave the agent's Instructions null. Only the agent's name/identity flows through the options ctor.
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
        var withSkillsAgent = ResolveChatClientAgent(withSkillsContext.Agent);
        var providers = AssertEx.NotNull(withSkillsAgent.AIContextProviders, "A skills agent must attach a context provider.");
        AssertEx.Equal(expected: 1, providers.Count);
        AssertEx.True(providers[0] is AgentSkillsProvider, "The attached provider must be an AgentSkillsProvider.");
        AssertEx.True(string.IsNullOrEmpty(withSkillsAgent.Instructions),
            "A skills agent must carry no instructions (delivered once via the seed system message).");
        AssertEx.Equal("XeInvocation-qwen3.5:0.8b", withSkillsAgent.Name);
    }

    [Test]
    public async Task BuildInlineSkill_AdvertisesEveryResource_AndCarriesTheFrontmatter()
    {
        // Behaviour, not registration order: MAF renders the <available_resources> block from the resources present when
        // the skill's content is built, so a resource registered too late would be readable but never advertised — the
        // model would have no way to learn it exists. Assert the generated content actually names them.
        var skill = new InvocationSkill("kubernetes-debug",
            "Debug k8s issues",
            "## Body",
            License: "MIT",
            Compatibility: "any Agent Skills host",
            AllowedTools: "read_file search_knowledge_base",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["author"] = "acme"
            },
            Resources:
            [
                new InvocationSkillResource("references/runbook.md", "Escalation runbook", "text/markdown", "step one"),
                new InvocationSkillResource("references/faq.md", "Frequently asked questions", "text/markdown", "answer one")
            ]);

        var inlineSkill = InvocationAgentFactory.BuildInlineSkill(skill);
        var content = await inlineSkill.GetContentAsync(CancellationToken.None);

        AssertEx.Contains(content, "references/runbook.md", message: "the skill content must advertise its first resource.");
        AssertEx.Contains(content, "references/faq.md", message: "the skill content must advertise its second resource.");
        AssertEx.Contains(content, "## Body", message: "the skill body must still be the skill's instructions.");

        // The frontmatter extras are carried, not dropped, by the full constructor. allowed-tools is metadata only —
        // nothing here grants a tool on its account.
        AssertEx.Equal("MIT", inlineSkill.Frontmatter.License);
        AssertEx.Equal("any Agent Skills host", inlineSkill.Frontmatter.Compatibility);
        AssertEx.Equal("read_file search_knowledge_base", inlineSkill.Frontmatter.AllowedTools);
        AssertEx.NotNull(inlineSkill.Frontmatter.Metadata, "frontmatter metadata must survive the conversion to AdditionalPropertiesDictionary.");
    }

    [Test]
    public async Task BuildInlineSkill_WithNoFrontmatterOrResources_MatchesThePreFrontmatterSkill()
    {
        // The 3-argument constructor this replaced IS the full constructor taking its defaults, so a skill carrying
        // neither frontmatter nor resources must build exactly what it built before either existed.
        var skill = new InvocationSkill("log-triage", "Triage logs", "## Logs");

        var built = InvocationAgentFactory.BuildInlineSkill(skill);
        var baseline = new AgentInlineSkill("log-triage", "Triage logs", "## Logs");

        AssertEx.Equal(await baseline.GetContentAsync(CancellationToken.None), await built.GetContentAsync(CancellationToken.None));
        AssertEx.Null(built.Frontmatter.License);
        AssertEx.Null(built.Frontmatter.AllowedTools);
    }
#pragma warning restore MAAI001

    [Test]
    public async Task RunStreamingAsync_NoSkills_InstructionsDeliveredOnce_AndAgentNameNeverLeaksToTheWire()
    {
        const string instructions = "You are the worker. Follow the playbook exactly.";
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            instructions,
            [],
            [new ChatMessage(ChatRole.User, "Summarise the deployment status.")]);

        using var chatClient = new CapturingChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);
        var chatClientAgent = ResolveChatClientAgent(context.Agent);

        await DriveAsync(context.Agent, context);

        AssertOutboundInstructionContract(chatClient, instructions, expectedAgentName: "XeInvocation-qwen3.5:0.8b", chatClientAgent);
    }

    // MAAI001: the skills definition drives the AgentSkillsProvider options ctor inside the factory; the wire assertion
    // below is provider-boundary only, so this test does not reference the experimental types directly.
    [Test]
    public async Task RunStreamingAsync_WithSkills_InstructionsDeliveredOnce_AndAgentNameNeverLeaksToTheWire()
    {
        const string instructions = "You are the worker. Follow the playbook exactly.";
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            instructions,
            [],
            [new ChatMessage(ChatRole.User, "Summarise the deployment status.")],
            Skills:
            [
                new InvocationSkill("kubernetes-debug", "Debug k8s issues", "## Body"),
                new InvocationSkill("log-triage", "Triage logs", "## Logs")
            ]);

        using var chatClient = new CapturingChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);
        var chatClientAgent = ResolveChatClientAgent(context.Agent);

        await DriveAsync(context.Agent, context);

        AssertOutboundInstructionContract(chatClient, instructions, expectedAgentName: "XeInvocation-qwen3.5:0.8b", chatClientAgent);
    }

    // ---- Tool-relevance escape hatch (list_tools) ----
    //
    // This factory is the ONLY site in the product that appends list_tools, which is what makes the send-time
    // relevance hop inert by construction for orchestration participants and spawned sub-agents: their arrays carry
    // no escape hatch, and the hop refuses to filter an array that has none.

    [Test]
    public async Task CreateAsync_WithNoToolRelevanceScope_DoesNotOfferListTools()
    {
        using var chatClient = new CapturingChatClient();

        var offered = await ResolveOfferedToolsAsync(chatClient, toolCount: 20);

        AssertEx.Empty(offered.OfType<ListToolsFunction>(), "The shipped default offers exactly the tools it offered before this slice existed.");
    }

    [Test]
    public async Task CreateAsync_UnderAnInactiveScope_DoesNotOfferListTools()
    {
        using var chatClient = new CapturingChatClient();

        using (ToolRelevanceScope.BeginScope(active: false, new HashSet<string>(StringComparer.Ordinal)))
        {
            var offered = await ResolveOfferedToolsAsync(chatClient, toolCount: 20);

            AssertEx.Empty(offered.OfType<ListToolsFunction>(), "The node kill-switch and the per-agent opt-out both arrive as an inactive scope.");
        }
    }

    [Test]
    public async Task CreateAsync_UnderAnActiveScopeAtTheThreshold_DoesNotOfferListTools()
    {
        using var chatClient = new CapturingChatClient();

        using (ToolRelevanceScope.BeginScope(active: true, new HashSet<string>(StringComparer.Ordinal)))
        {
            var offered = await ResolveOfferedToolsAsync(chatClient, toolCount: 12);

            AssertEx.Empty(offered.OfType<ListToolsFunction>(), "At or below the threshold nothing is ever hidden, so no escape hatch is needed.");
        }
    }

    [Test]
    public async Task CreateAsync_UnderAnActiveScopeAboveTheThreshold_OffersListTools()
    {
        using var chatClient = new CapturingChatClient();

        using (ToolRelevanceScope.BeginScope(active: true, new HashSet<string>(StringComparer.Ordinal)))
        {
            var offered = await ResolveOfferedToolsAsync(chatClient, toolCount: 13);

            AssertEx.ContainsSingle(offered, static tool => tool is ListToolsFunction);
        }
    }

    [Test]
    public async Task CreateAsync_ForASkillsAgent_CountsTheThreeMafSkillToolsTowardsTheThreshold()
    {
        // Ten resolved tools plus the three MAF skill tools is thirteen — the array the hop will measure. Counting only
        // the resolved ten would leave a skills agent above the threshold with no escape hatch.
        using var withSkills = new CapturingChatClient();
        using var withoutSkills = new CapturingChatClient();

        using (ToolRelevanceScope.BeginScope(active: true, new HashSet<string>(StringComparer.Ordinal)))
        {
            var skillsAgent = await ResolveOfferedToolsAsync(withSkills, toolCount: 10, withSkills: true);
            AssertEx.ContainsSingle(skillsAgent, static tool => tool is ListToolsFunction);

            var plainAgent = await ResolveOfferedToolsAsync(withoutSkills, toolCount: 10);
            AssertEx.Empty(plainAgent.OfType<ListToolsFunction>(), "The same ten tools without skills stay below the threshold.");
        }
    }

    [Test]
    public async Task CreateAsync_WhenTheNodePolicyTightensReadLocal_ListToolsStillRequiresNoApproval()
    {
        // A tightening node policy reaches the resolver as requiresApproval on every offer placeholder, and the
        // resolver wraps each resolved executable. list_tools is appended AFTER that pass, so it is deliberately
        // subject to neither the tighten-only compose nor AllowedToolNames — an in-process listing of names the agent
        // is already authorised for. This exemption is stated in the design, not discovered here.
        using var chatClient = new CapturingChatClient();

        using (ToolRelevanceScope.BeginScope(active: true, new HashSet<string>(StringComparer.Ordinal)))
        {
            var offered = await ResolveOfferedToolsAsync(chatClient, toolCount: 13, requiresApproval: true);

            AssertEx.Equal(expected: 13, offered.OfType<ApprovalRequiredAIFunction>().Count(), "Every OFFERED tool was tightened by the resolver's compose.");
            // Had the append run before that pass, this entry would be an ApprovalRequiredAIFunction wrapper and no
            // bare ListToolsFunction would survive in the array.
            AssertEx.ContainsSingle(offered, static tool => tool is ListToolsFunction);
            AssertEx.Equal(expected: 14, offered.Count, "Thirteen tightened tools plus the unwrapped escape hatch.");
        }
    }

    // Builds an agent over `toolCount` resolvable tools, drives one turn, and returns the tool array the model was
    // actually handed — the same observation point the production pipeline's relevance hop reads.
    private static async Task<IReadOnlyList<AITool>> ResolveOfferedToolsAsync(CapturingChatClient chatClient,
        int toolCount,
        bool withSkills = false,
        bool requiresApproval = false)
    {
        var names = Enumerable.Range(0, toolCount).Select(static index => $"tool_{index}").ToList();
        var registry = new FakeToolRegistry([.. names.Select(static name => AIFunctionFactory.Create((string input) => input, name))]);
        var definition = new InvocationAgentDefinition("qwen3.5:0.8b",
            "Be helpful.",
            [.. names.Select(name => InvocationToolBridge.CreateOfferPlaceholder(name, requiresApproval))],
            [new ChatMessage(ChatRole.User, "Summarise the deployment status.")],
            Skills: withSkills ? [new InvocationSkill("kubernetes-debug", "Debug k8s issues", "## Body")] : null);

        var sut = CreateSut(chatClient, registry);
        await using var context = await sut.CreateAsync(definition);
        await DriveAsync(context.Agent, context);

        return [.. AssertEx.NotNull(chatClient.CapturedOptions).Tools ?? []];
    }

    // Mirrors the production run loop (InvocationRunner): replay the seed messages through the agent with the per-turn
    // run options, threadless, so the capturing client observes exactly what the model would receive.
    private static async Task DriveAsync(AIAgent agent, InvocationAgentContext context)
    {
        await foreach (var _ in agent.RunStreamingAsync(context.SeedMessages, session: null, context.RunOptions, CancellationToken.None))
        {
            // Drain the stream so the inner chat client is actually invoked.
        }
    }

    private static ChatClientAgent ResolveChatClientAgent(AIAgent agent)
    {
        return agent.GetService<ChatClientAgent>()
               ?? throw new AssertionException("Expected the agent pipeline to expose its inner ChatClientAgent.");
    }

    // Verifies the instructions-delivery contract on the ACTUAL outbound GetStreamingResponseAsync call: the system
    // instructions appear exactly once (as a single System message, never also on ChatOptions.Instructions), and the
    // synthetic agent name is identity-only — it never appears as message content or as instructions, and the real
    // instructions never become the agent name.
    private static void AssertOutboundInstructionContract(CapturingChatClient chatClient,
        string instructions,
        string expectedAgentName,
        ChatClientAgent agent)
    {
        AssertEx.True(chatClient.CallCount > 0, "the inner chat client must have been invoked at least once");
        var messages = AssertEx.NotNull(chatClient.CapturedMessages, "the chat client must have captured the outbound messages");

        var systemInstructionMessages = messages.Count(message =>
            message.Role == ChatRole.System && string.Equals(message.Text, instructions, StringComparison.Ordinal));
        AssertEx.Equal(expected: 1, systemInstructionMessages);

        // The AgentSkillsProvider legitimately contributes its own skill-discovery preamble via
        // ChatOptions.Instructions on the skills path; the contract is that the DEFINITION instructions are never
        // duplicated there (the seed System message is their single delivery channel).
        var outboundInstructions = chatClient.CapturedOptions?.Instructions ?? string.Empty;
        AssertEx.False(outboundInstructions.Contains(instructions, StringComparison.Ordinal),
            "the definition instructions must not also ride ChatOptions.Instructions (that would double-send them)");

        AssertEx.False(messages.Any(message => (message.Text ?? string.Empty).Contains(expectedAgentName, StringComparison.Ordinal)),
            "the synthetic agent name must never appear as message content");

        AssertEx.Equal(expectedAgentName, agent.Name);
        AssertEx.NotEqual(instructions, agent.Name);
    }

    /// <summary>
    ///     The budget is only real for a model llama.cpp can ENFORCE it on. llama-server writes
    ///     <c>reasoning_budget_tokens</c> onto the sampler only when its chat-template classification produced a
    ///     non-empty think-end-tag set; with an empty set it accepts the field and then ignores it, and the reasoning
    ///     free-runs exactly as if nothing had been sent. Emitting the marker there would leave every layer above
    ///     believing in a cap that never fires, so it is omitted instead — while the graded <c>think</c> option, which
    ///     governs whether the model reasons at all, is untouched.
    /// </summary>
    [Test]
    public async Task CreateAsync_ThinkingCapableButBudgetNotEnforceable_OmitsTheReasoningBudget()
    {
        var definition = new InvocationAgentDefinition("factory-unenforceable-budget-model-a",
            "Be helpful.",
            [],
            [],
            "high",
            ReasoningBudgetEnforceable: false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey(InvocationAgentFactory.LlamaReasoningBudgetMarkerKey),
            "an unenforceable budget must be omitted, not sent and silently ignored");
        AssertEx.True(additionalProperties.TryGetValue<string>("think", out var think),
            "enforceability governs the cap, never whether the model reasons");
        AssertEx.Equal("high", think);
    }

    /// <summary>
    ///     Every turn on such a model would otherwise repeat the notice on the hot send path, so the skip is reported
    ///     once per MODEL. The model id here is unique to this test because the de-duplication memory is process-wide
    ///     (see <c>ReasoningBudgetSkipLog</c>), and the message must name the model so an operator can act on it.
    /// </summary>
    [Test]
    public async Task CreateAsync_UnenforceableBudget_ReportsTheSkipOncePerModel()
    {
        const string modelId = "factory-unenforceable-budget-model-b";
        var logger = new RecordingLogger<InvocationAgentFactory>();
        var definition = new InvocationAgentDefinition(modelId,
            "Be helpful.",
            [],
            [],
            "medium",
            ReasoningBudgetEnforceable: false);

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, logger: logger);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var context = await sut.CreateAsync(definition);
        }

        // The factory logs other things per call, so count only the skip notice itself.
        var skipNotices = logger.Entries
                                .Where(entry => entry.Level == LogLevel.Information
                                                && entry.Message.Contains(BudgetSkipMessageFragment, StringComparison.Ordinal))
                                .ToArray();
        AssertEx.Equal(expected: 1, skipNotices.Length);
        AssertEx.Contains(skipNotices[0].Message, modelId);
    }

    /// <summary>
    ///     The default is the enforceable one, so every pre-existing caller — and every cloud provider, whose wire never
    ///     sees the marker at all — keeps the request it had before the flag existed, with nothing logged.
    /// </summary>
    [Test]
    public async Task CreateAsync_EnforceableBudgetIsTheDefault_CarriesTheBudgetAndLogsNothing()
    {
        var logger = new RecordingLogger<InvocationAgentFactory>();
        var definition = new InvocationAgentDefinition("factory-enforceable-budget-model",
            "Be helpful.",
            [],
            [],
            "low");

        using var chatClient = new FakeChatClient();
        var sut = CreateSut(chatClient, logger: logger);

        await using var context = await sut.CreateAsync(definition);

        var additionalProperties = AssertEx.NotNull(ResolveChatOptions(context).AdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<int>(InvocationAgentFactory.LlamaReasoningBudgetMarkerKey, out var budget));
        AssertEx.Equal(expected: 2048, budget);
        AssertEx.False(logger.Entries.Any(entry => entry.Message.Contains(BudgetSkipMessageFragment, StringComparison.Ordinal)),
            "an enforceable budget is the status quo and must report nothing");
    }

    private static ChatOptions ResolveChatOptions(InvocationAgentContext context)
    {
        var runOptions = context.RunOptions as ChatClientAgentRunOptions
                         ?? throw new AssertionException("Expected ChatClientAgentRunOptions.");
        return runOptions.ChatOptions
               ?? throw new AssertionException("Expected ChatOptions to be populated.");
    }

    private static InvocationAgentFactory CreateSut(IChatClient chatClient,
        IAgentToolRegistry? toolRegistry = null,
        IClientLocalToolRegistry? clientLocalToolRegistry = null,
        IMcpToolRegistry? mcpToolRegistry = null,
        ICustomToolCatalog? customToolCatalog = null,
        ILogger<InvocationAgentFactory>? logger = null)
    {
        return new InvocationAgentFactory(chatClient,
            Options.Create(new InvocationAgentOptions()),
            logger ?? NullLogger<InvocationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            FakeServiceProvider.Instance,
            toolRegistry ?? new FakeToolRegistry(),
            clientLocalToolRegistry ?? new FakeClientLocalToolRegistry(),
            mcpToolRegistry ?? new FakeMcpToolRegistry(),
            customToolCatalog ?? new EmptyCustomToolCatalog());
    }

    // An always-empty custom-tool catalog: the single-agent factory tests never offer a custom__ tool, so the catalog is
    // never consulted for one. Kept minimal so it stays a no-op in the resolver's pre-resolution pass.
    private sealed class EmptyCustomToolCatalog : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);
        }

        public Task<AITool?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<AITool?>(null);
        }
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
                         .Select(static function => new LocalChatToolDescriptor(function.Name, function.Description, function.JsonSchema.GetRawText(), RequiresApproval: false))
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
                         .Select(static function => new LocalChatToolDescriptor(function.Name, function.Description, function.JsonSchema.GetRawText(), RequiresApproval: true))
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

    /// <summary>
    ///     Records the exact outbound <see cref="ChatMessage" /> list and <see cref="ChatOptions" /> the agent hands to
    ///     the model on the streaming (and non-streaming) path, so a test can assert the provider-wire instruction
    ///     contract rather than the agent's in-memory shape.
    /// </summary>
    private sealed class CapturingChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? CapturedMessages { get; private set; }

        public ChatOptions? CapturedOptions { get; private set; }

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Capture(messages, options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Capture(messages, options);
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

        private void Capture(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            CapturedMessages = messages.ToList();
            CapturedOptions = options;
            CallCount++;
        }
    }
}
