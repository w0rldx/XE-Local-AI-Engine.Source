namespace XE_Local_AI_Engine.Tests.Capacity;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     <see cref="SubAgentSpawnService" /> tests: the spawn dispatcher. An admitted local spawn builds a
///     tool-less sub-agent and returns its result while disposing the ledger reservation; a same-model verdict
///     serializes (no second load) with a bounded wait that rejects on timeout; an over-cap fan-out or cloud-spawn is
///     rejected; a reject verdict returns its sanitized reason as the result (no exception, no path/secret); and the
///     outer cancellation token flows into the inner run. The capacity verdict is mocked so each dispatch branch is
///     exercised in isolation; no Ollama/Docker/network.
/// </summary>
public sealed class SubAgentSpawnServiceTests
{
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";

    [Test]
    public async Task Spawn_WhenAllow_BuildsBoundSubAgent_AndReturnsResult()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("do the thing"), CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal(1, harness.ChatClient.CallCount);
        // The child run MUST carry the bound model id so RuntimeChatClient routes the shared client to the right
        // provider; without it the inner run falls back to the node default provider (a real bug a mocked-out ModelId
        // would hide). The model id rides ChatOptions.ModelId, set on the child's ChatClientAgentOptions.ChatOptions.
        AssertEx.Equal(Model, harness.ChatClient.LastModelId);
        // The local Allow reservation must be released when the child exits.
        AssertEx.True(harness.ReservationDisposed, "ledger reservation should be disposed on child exit");
    }

    [Test]
    public async Task Spawn_WhenAllow_ChildInheritsProfileTools_ButNotSpawnSubAgent()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        // The profile lists spawn_subagent ALONGSIDE real tools — it must be filtered from the child set regardless.
        var id = harness.RegisterProfile("coder", "read_file", "list_files", "spawn_subagent");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "read it"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        // The child inherited its profile's curated tools…
        AssertEx.Contains(harness.ChatClient.LastToolNames, "read_file");
        AssertEx.Contains(harness.ChatClient.LastToolNames, "list_files");
        // …but spawn_subagent is UNCONDITIONALLY filtered out (structural depth cap), even though the profile listed it.
        AssertEx.False(harness.ChatClient.LastToolNames.Any(name => name == "spawn_subagent"),
            "the child tool set must never contain spawn_subagent");
    }

    [Test]
    public async Task Spawn_WhenChildDefinitionPinnedToCloudModel_ResolvesToolOfferOnThatCloudModel()
    {
        // A spawned sub-agent bound to a definition pinned to a CLOUD model must resolve its tool offer
        // through the shared AgentDefinitionResolver keyed on the CHILD's effective (pinned) cloud model — the model the
        // effective-model knowledge-tool locality gate keys on (the gate's withholding is proven end-to-end in
        // AgentDefinitionResolverTests). This proves the spawn path threads the child's pinned model into that gate,
        // not the parent turn's active model, so a cloud-pinned spawned profile cannot retain the knowledge tools.
        using var harness = new Harness();
        harness.AllowCloud();
        const string cloudModel = "azure-foundry-deploy";
        var id = harness.RegisterProfilePinnedTo("cloud-child", cloudModel, "read_file");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "do it"
        }, CancellationToken.None);

        await harness.Resolver.Received().ResolveAsync(id, cloudModel, Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Spawn_WhenProfileBound_ChildConsumesResolvedSystemPrompt_NotRawInstructions()
    {
        // A profile-bound child must run on the RESOLVED system prompt (scaffold + persona + injected playbook
        // memory), NOT the raw definition.Instructions. Raw and resolved DIVERGE here so reading the wrong one is visible.
        using var harness = new Harness();
        harness.AllowLocal();
        var id = harness.RegisterResolvedProfile("coder",
            rawInstructions: "RAW persona only — no scaffold, no memory.",
            resolvedPrompt: "SCAFFOLD + persona + injected playbook memory.",
            reasoningEffort: null,
            skills: [],
            allowedToolNames: "read_file");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "read it"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal("SCAFFOLD + persona + injected playbook memory.", harness.ChatClient.LastInstructions);
        AssertEx.False(harness.ChatClient.LastInstructions!.Contains("RAW persona only", StringComparison.Ordinal),
            "the child must never run on the raw definition.Instructions — that is the sanitizer bypass.");
    }

    [Test]
    public async Task Spawn_WhenProfileBound_ThreadsResolvedReasoningEffort_OnThinkingModel()
    {
        // The resolved reasoning effort must be baked into the child's construction-time ChatOptions (an agent-as-tool
        // never receives per-run RunOptions), gated on the child model's thinking capability — mirroring the
        // orchestration-participant path. A thinking-capable model honors the graded level on the Ollama think key.
        using var harness = new Harness();
        harness.AllowLocal();
        harness.WithThinkingCapability(true);
        var id = harness.RegisterResolvedProfile("coder",
            rawInstructions: "raw",
            resolvedPrompt: "resolved",
            reasoningEffort: "medium",
            skills: [],
            allowedToolNames: "read_file");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "reason about it"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        var additionalProperties = AssertEx.NotNull(harness.ChatClient.LastAdditionalProperties,
            "a profile-bound child must carry reasoning AdditionalProperties");
        AssertEx.True(additionalProperties.TryGetValue<string>("think", out var thinkValue));
        AssertEx.Equal("medium", thinkValue);
    }

    [Test]
    public async Task Spawn_WhenProfileBound_NonThinkingModel_OmitsThinkField()
    {
        // Same resolved effort, but the child model does NOT advertise the thinking capability: the think field must be
        // OMITTED (Ollama 400s on think:true/level for such a model) so its built-in template reasoning runs — the exact
        // capability gate ParticipantReasoningOptions applies on the direct/orchestration paths.
        using var harness = new Harness();
        harness.AllowLocal();
        harness.WithThinkingCapability(false);
        var id = harness.RegisterResolvedProfile("coder",
            rawInstructions: "raw",
            resolvedPrompt: "resolved",
            reasoningEffort: "medium",
            skills: [],
            allowedToolNames: "read_file");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "reason about it"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        var additionalProperties = AssertEx.NotNull(harness.ChatClient.LastAdditionalProperties);
        AssertEx.False(additionalProperties.ContainsKey("think"),
            "a non-thinking child model must omit the think field entirely.");
    }

    [Test]
    public async Task Spawn_WhenProfileBound_AttachesResolvedSkills()
    {
        // The resolved skills must ride into the child as a MAF AgentSkillsProvider (progressive disclosure): the
        // provider contributes its skill-discovery preamble via ChatOptions.Instructions at run time, naming each
        // available skill — so the skill name reaches the wire alongside the resolved prompt.
        using var harness = new Harness();
        harness.AllowLocal();
        var id = harness.RegisterResolvedProfile("coder",
            rawInstructions: "raw",
            resolvedPrompt: "resolved system prompt",
            reasoningEffort: null,
            skills: [new ResolvedSkill(Guid.NewGuid(), "kubernetes-debug", "Debug k8s issues", "## Body", 1)],
            allowedToolNames: "read_file");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "use the skill"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        var instructions = AssertEx.NotNull(harness.ChatClient.LastInstructions);
        AssertEx.Contains(instructions, "resolved system prompt");
        AssertEx.Contains(instructions, "kubernetes-debug");
    }

    [Test]
    public async Task Spawn_WhenProfileBound_ConsumesSameRuntimeFieldsAsDirectPath()
    {
        // Parity, shaped as one assertion: a resolved runtime with a divergent prompt, a graded reasoning effort, and a
        // skill must ALL reach the child — the same ResolvedAgentRuntime fields the direct chat path threads into its
        // runtime package (resolved.ResolvedSystemPrompt / resolved.ReasoningEffort / resolved.Skills). Previously
        // the spawn path consumed only resolved.AllowedTools, so a saved sub-agent diverged from a direct send.
        using var harness = new Harness();
        harness.AllowLocal();
        harness.WithThinkingCapability(true);
        var id = harness.RegisterResolvedProfile("coder",
            rawInstructions: "RAW instructions the spawn path must NOT read.",
            resolvedPrompt: "RESOLVED prompt the direct path threads.",
            reasoningEffort: "high",
            skills: [new ResolvedSkill(Guid.NewGuid(), "log-triage", "Triage logs", "## Logs", 1)],
            allowedToolNames: "read_file");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "do it"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        var instructions = AssertEx.NotNull(harness.ChatClient.LastInstructions);
        // Prompt: the resolved prompt, never the raw instructions.
        AssertEx.Contains(instructions, "RESOLVED prompt the direct path threads.");
        AssertEx.False(instructions.Contains("RAW instructions", StringComparison.Ordinal),
            "the spawn path must consume the resolved prompt, not raw definition.Instructions.");
        // Reasoning effort: threaded and honored on the think key.
        var additionalProperties = AssertEx.NotNull(harness.ChatClient.LastAdditionalProperties);
        AssertEx.True(additionalProperties.TryGetValue<string>("think", out var thinkValue));
        AssertEx.Equal("high", thinkValue);
        // Skills: attached (the discovery preamble names the skill on the wire).
        AssertEx.Contains(instructions, "log-triage");
        // Tools: the curated profile tool still rides through.
        AssertEx.Contains(harness.ChatClient.LastToolNames, "read_file");
    }

    [Test]
    public async Task Spawn_WhenProfileBound_DropsApprovalRequiredTools_KeepsNonGated()
    {
        // A spawned child runs as an agent-as-tool (AsAIFunction, no per-run options, no HITL round-trip), so
        // an approval-gated tool would surface a ToolApprovalRequestContent the child can never answer — failing every
        // call silently. CurateChildTools must DROP the approval-required tool (never unwrap it to auto-execute) while
        // the non-gated tool survives, and warn naming the dropped tool.
        using var harness = new Harness();
        harness.AllowLocal();
        // read_file resolves to an approval-required tool (its offer requires approval, so the resolver wraps it as an
        // ApprovalRequiredAIFunction); list_files stays a plain executable.
        var id = harness.RegisterProfileWithMixedApprovalTools("coder", gatedTool: "read_file", ungatedTool: "list_files");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "read it"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        // The non-gated tool survives…
        AssertEx.Contains(harness.ChatClient.LastToolNames, "list_files");
        // …but the approval-required tool is dropped — the child never carries a tool it cannot complete.
        AssertEx.False(harness.ChatClient.LastToolNames.Any(name => name == "read_file"),
            "an approval-required tool must be stripped from a sub-agent child (no HITL route).");
        // The drop is observable: a Warning names the dropped tool.
        AssertEx.Contains(harness.LogText, "approval-required");
        AssertEx.Contains(harness.LogText, "read_file");
    }

    [Test]
    public async Task Spawn_WhenModelIdOnly_ChildIsToolLess()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("ad-hoc"), CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        // A model-id-only spawn has no agent profile, so no AllowedToolNames → the child runs tool-less.
        AssertEx.Empty(harness.ChatClient.LastToolNames);
    }

    [Test]
    public async Task Spawn_WhenModelIdOnlyWithNoCustomInstructions_ComposesScaffoldAheadOfDefaultPersona()
    {
        // A model-id-only child has no persisted definition to opt out with, so it always gets the same scaffold
        // coverage as a resolved agent — composed ahead of the short default sub-agent persona text.
        const string scaffoldText = "You are a locally-run agent. Ground every claim; use tools when they help.";
        using var harness = new Harness();
        harness.AllowLocal();
        harness.WithScaffold(scaffoldText);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("ad-hoc"), CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        AssertEx.NotNull(harness.ChatClient.LastInstructions);
        AssertEx.True(harness.ChatClient.LastInstructions!.StartsWith(scaffoldText, StringComparison.Ordinal),
            "the default sub-agent instructions must be prefixed with the base scaffold.");
        AssertEx.Contains(harness.ChatClient.LastInstructions, "focused sub-agent");
    }

    [Test]
    public async Task Spawn_WhenModelIdOnlyWithCustomInstructions_UsesCustomInstructionsVerbatim_NoScaffold()
    {
        // An explicit request-supplied Instructions override bypasses the default persona AND the scaffold entirely —
        // the caller's exact text is used unchanged.
        const string scaffoldText = "You are a locally-run agent. Ground every claim; use tools when they help.";
        using var harness = new Harness();
        harness.AllowLocal();
        harness.WithScaffold(scaffoldText);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            ModelId = Model,
            Task = "ad-hoc",
            Instructions = "Only answer in French."
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal("Only answer in French.", harness.ChatClient.LastInstructions);
    }

    [Test]
    public async Task Spawn_WhenDepthGuardHit_Rejects()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        // Simulate a child-depth context (Depth >= 1): the runtime depth guard rejects with a sanitized reason and the
        // inner agent never runs. (In production a Depth-1 agent is also tool-less, so it cannot reach here at all.)
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        using var child = SpawnContext.Current!.BeginChildScope();

        var result = await service.SpawnAsync(ModelRequest("nested"), CancellationToken.None);

        AssertEx.Contains(result, "may not spawn further sub-agents");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task Spawn_PropagatesCancellationToInnerRun()
    {
        // A live cancellation while the inner agent is running must cancel the inner run (AsAIFunction forwards the ct).
        using var harness = new Harness(delayBeforeResponse: TimeSpan.FromSeconds(5));
        harness.AllowLocal();
        var service = harness.Build();

        using var cts = new CancellationTokenSource();
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);

        var spawnTask = service.SpawnAsync(ModelRequest("slow task"), cts.Token);
        // Wait deterministically until the inner run has actually started before cancelling. A timed poll races under a
        // parallel test load: if cancellation fires before the inner ChatClient run begins, the run never observes the
        // token and InnerObservedCancellation stays false. WaitUntilRunningAsync completes the moment the run body starts.
        await harness.ChatClient.WaitUntilRunningAsync();

        await cts.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => spawnTask);
        AssertEx.True(harness.ChatClient.InnerObservedCancellation, "inner run should observe the parent cancellation");
        // A cancelled spawn still releases its reservation.
        AssertEx.True(harness.ReservationDisposed, "reservation must be released even when the inner run is cancelled");
    }

    [Test]
    public async Task Spawn_WhenFanOutExceedsCap_Rejects()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        // A root with a fan-out cap of 1: hold one live spawn open, then a second concurrent spawn must be rejected.
        using var root = SpawnContext.BeginRoot(fanOutCap: 1, cloudSpawnCap: 3);
        var gate = new TaskCompletionSource();
        harness.ChatClient.HoldUntil(gate.Task);

        var first = service.SpawnAsync(ModelRequest("first"), CancellationToken.None);
        await harness.ChatClient.WaitUntilRunningAsync();

        var secondResult = await service.SpawnAsync(ModelRequest("second"), CancellationToken.None);
        AssertEx.Contains(secondResult, "concurrent sub-agents");

        gate.SetResult();
        await first;
    }

    [Test]
    public async Task Spawn_WhenCloudSpawnCapExceeded_Rejects()
    {
        // Cloud Allow carries a NULL reservation; the cloud-spawn cap is the gate. Cap = 1 → the 2nd cloud spawn rejects.
        using var harness = new Harness();
        harness.AllowCloud();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 1);

        var first = await service.SpawnAsync(ModelRequest("first"), CancellationToken.None);
        AssertEx.Equal("sub-agent-result", first);

        var second = await service.SpawnAsync(ModelRequest("second"), CancellationToken.None);
        AssertEx.Contains(second, "cloud sub-agents");
    }

    [Test]
    public async Task Spawn_WhenReject_ReturnsStructuredReason_NotException()
    {
        using var harness = new Harness();
        harness.RejectInsufficient();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("nope"), CancellationToken.None);

        // The sanitized reason is returned as the tool result; the inner agent never runs.
        AssertEx.Contains(result, "Insufficient capacity");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
        // No node-internal detail leaks: the model id / path must not appear in the caller-facing reason.
        AssertEx.False(result.Contains(Model, StringComparison.Ordinal), "reject reason must not leak the model id");
    }

    [Test]
    public async Task Spawn_WhenQueueSameModel_Serializes_NoSecondLoad()
    {
        using var harness = new Harness();
        harness.QueueSameModel();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(ModelRequest("queued"), CancellationToken.None);

        // The queued spawn runs the inner agent against the already-running process (no reservation, no second load).
        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal(1, harness.ChatClient.CallCount);
        AssertEx.False(harness.ReservationDisposed, "QueueSameModel must not take a ledger reservation");
    }

    [Test]
    public async Task Spawn_WhenMissingSpawnContext_Rejects()
    {
        // With NO ambient SpawnContext (no root seeded), a spawn defaults safe — it is rejected rather than overrunning.
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        var result = await service.SpawnAsync(ModelRequest("orphan"), CancellationToken.None);

        AssertEx.Contains(result, "concurrent sub-agents");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task Spawn_WhenInvalidArguments_Rejects()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);

        // Both bindings present (not mutually exclusive) → invalid.
        var bothResult = await service.SpawnAsync(new SubAgentSpawnRequest
            {
                ModelId = Model,
                SubAgentKey = "x",
                Task = "t"
            },
            CancellationToken.None);
        AssertEx.Contains(bothResult, "exactly one");

        // Empty task → invalid.
        var emptyTask = await service.SpawnAsync(new SubAgentSpawnRequest
            {
                ModelId = Model,
                Task = "   "
            },
            CancellationToken.None);
        AssertEx.Contains(emptyTask, "non-empty task");

        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    private static SubAgentSpawnRequest ModelRequest(string task)
    {
        return new SubAgentSpawnRequest
        {
            ModelId = Model,
            Task = task
        };
    }

    // Assembles the spawn service over a mocked capacity verdict + a real SpawnSerializer + a gateable RecordingChatClient.
    private sealed class Harness : IDisposable
    {
        private readonly GateableChatClient _chatClient;
        private readonly ICapacityService _capacity = Substitute.For<ICapacityService>();
        private readonly IAgentDefinitionResolver _resolver = Substitute.For<IAgentDefinitionResolver>();
        private readonly IAgentDefinitionStore _definitionStore = Substitute.For<IAgentDefinitionStore>();
        private readonly IModelCapabilityResolver _modelCapabilityResolver = Substitute.For<IModelCapabilityResolver>();
        private readonly FakeAgentInstructionProvider _instructionProvider = new();
        private readonly IAgentToolRegistry _toolRegistry = new FakeAgentToolRegistry();
        private readonly CapturingLogger<SubAgentSpawnService> _logger = new();
        private bool _reservationDisposed;

        public Harness(TimeSpan? delayBeforeResponse = null)
        {
            _chatClient = new GateableChatClient(delayBeforeResponse: delayBeforeResponse);
            // Default: the child model advertises the thinking capability, so a resolved reasoning effort is honored on
            // the Ollama think key. A test can flip this to prove a non-thinking model omits the field.
            _modelCapabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                                    .Returns((SupportsThinking: true, SupportsTools: true, IsCloud: false));
        }

        // Overrides the child model's advertised thinking capability (default true), gating whether a resolved reasoning
        // effort reaches the Ollama think key or is omitted (a non-thinking model 400s on think:true/level).
        public void WithThinkingCapability(bool supportsThinking)
        {
            _modelCapabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                                    .Returns((SupportsThinking: supportsThinking, SupportsTools: true, IsCloud: false));
        }

        public GateableChatClient ChatClient => _chatClient;

        public IAgentDefinitionResolver Resolver => _resolver;

        public bool ReservationDisposed => _reservationDisposed;

        // All log text captured from the SUT, so a test can assert the dropped-approval-tool Warning.
        public string LogText => _logger.AllText;

        // Registers a bound sub-agent profile pinned to a SPECIFIC model (e.g. a cloud deployment) so a locality-gate test can
        // prove the spawn resolves the child's tool offer through the shared resolver keyed on that pinned model — the
        // effective model the knowledge-tool locality gate keys on (the gate itself is proven in AgentDefinitionResolverTests).
        public Guid RegisterProfilePinnedTo(string name, string modelProfile, params string[] allowedToolNames)
        {
            var id = Guid.NewGuid();
            var definition = new AgentDefinitionRecord(id,
                name,
                Description: null,
                Instructions: "child instructions",
                ModelProfile: modelProfile,
                ReasoningEffort: null,
                Kind: AgentDefinitionKind.Single,
                AllowedToolNames: allowedToolNames,
                ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
                OrchestrationTopologyJson: null,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0);
            _definitionStore.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(definition);

            var offered = allowedToolNames
                          .Select(static toolName => new AllowedToolDto
                          {
                              Id = Guid.NewGuid(),
                              Name = toolName,
                              Location = ToolLocation.ClientLocal,
                              ParameterSchema = "{\"type\":\"object\"}",
                              RequiresApproval = false
                          })
                          .ToArray();
            _resolver.ResolveAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(new ResolvedAgentRuntime("child instructions", offered, modelProfile, null, 1, id, name, []));
            return id;
        }

        // Registers a profile (subAgentKey → definition) whose resolved AllowedTools are the supplied offer names, so the
        // service curates the child tool set through the real InvocationToolResolver against the fake catalog. Raw and
        // resolved prompts are identical here (tool-focused tests); the reasoning/skills-parity tests use
        // RegisterResolvedProfile to make them DIVERGE.
        public Guid RegisterProfile(string name, params string[] allowedToolNames)
        {
            return RegisterResolvedProfile(name,
                rawInstructions: "child instructions",
                resolvedPrompt: "child instructions",
                reasoningEffort: null,
                skills: [],
                allowedToolNames: allowedToolNames);
        }

        // Registers a profile whose raw definition.Instructions and resolved system prompt DIVERGE, and that carries a
        // reasoning effort + skills — so a test can prove the child consumes the RESOLVED runtime (prompt/reasoning/
        // skills), not the raw definition fields. The definition's raw ReasoningEffort is set to a deliberately WRONG
        // sentinel to prove the child reads the resolver's value, not the definition's.
        public Guid RegisterResolvedProfile(string name,
            string rawInstructions,
            string resolvedPrompt,
            string? reasoningEffort,
            IReadOnlyList<ResolvedSkill> skills,
            params string[] allowedToolNames)
        {
            var id = Guid.NewGuid();
            var definition = new AgentDefinitionRecord(id,
                name,
                Description: null,
                Instructions: rawInstructions,
                ModelProfile: Model,
                ReasoningEffort: "raw-definition-effort-should-not-be-read",
                Kind: AgentDefinitionKind.Single,
                AllowedToolNames: allowedToolNames,
                ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
                OrchestrationTopologyJson: null,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0);
            _definitionStore.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(definition);

            var offered = allowedToolNames
                          .Select(static toolName => new AllowedToolDto
                          {
                              Id = Guid.NewGuid(),
                              Name = toolName,
                              Location = ToolLocation.ClientLocal,
                              ParameterSchema = "{\"type\":\"object\"}",
                              RequiresApproval = false
                          })
                          .ToArray();
            _resolver.ResolveAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(new ResolvedAgentRuntime(resolvedPrompt, offered, Model, reasoningEffort, 1, id, name, skills));
            return id;
        }

        // Registers a profile whose resolved AllowedTools carry MIXED approval flags: the gated tool's offer requires
        // approval (so InvocationToolResolver wraps it in ApprovalRequiredAIFunction), the ungated one does not. The
        // child-tool curation must drop the wrapped one and keep the plain one. Both names must exist in the
        // fake catalog so they resolve to executables.
        public Guid RegisterProfileWithMixedApprovalTools(string name, string gatedTool, string ungatedTool)
        {
            var id = Guid.NewGuid();
            var definition = new AgentDefinitionRecord(id,
                name,
                Description: null,
                Instructions: "child instructions",
                ModelProfile: Model,
                ReasoningEffort: null,
                Kind: AgentDefinitionKind.Single,
                AllowedToolNames: [gatedTool, ungatedTool],
                ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
                OrchestrationTopologyJson: null,
                Version: 1,
                CreatedAtUtc: 0,
                UpdatedAtUtc: 0);
            _definitionStore.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(definition);

            var offered = new[]
            {
                new AllowedToolDto
                {
                    Id = Guid.NewGuid(),
                    Name = gatedTool,
                    Location = ToolLocation.ClientLocal,
                    ParameterSchema = "{\"type\":\"object\"}",
                    RequiresApproval = true
                },
                new AllowedToolDto
                {
                    Id = Guid.NewGuid(),
                    Name = ungatedTool,
                    Location = ToolLocation.ClientLocal,
                    ParameterSchema = "{\"type\":\"object\"}",
                    RequiresApproval = false
                }
            };
            _resolver.ResolveAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns(new ResolvedAgentRuntime("child instructions", offered, Model, null, 1, id, name, []));
            return id;
        }

        public void AllowLocal()
        {
            // Ownership of the reservation transfers to the SUT, which disposes it on child exit (the assertion the
            // tests make). The analyzer cannot see the transfer through the mocked decision, so suppress here.
#pragma warning disable CA2000
            var reservation = new TrackingDisposable(() => _reservationDisposed = true);
#pragma warning restore CA2000
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.Allow, "Capacity available.", OllamaEvictionWarning: false, reservation));
        }

        public void AllowCloud()
        {
            // Cloud Allow carries a null reservation (no local cost).
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.Allow, "Cloud provider selected; no local capacity required.", OllamaEvictionWarning: false));
        }

        public void QueueSameModel()
        {
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.QueueSameModel, "Model already running; the spawn will share that process.", OllamaEvictionWarning: false));
        }

        public void RejectInsufficient()
        {
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(_ => new CapacityDecision(CapacityVerdict.RejectInsufficient, "Insufficient capacity: not enough free memory for another model.", OllamaEvictionWarning: false));
        }

        // Configures the base scaffold text the default sub-agent instructions are composed with. Unconfigured
        // (the default) returns null/empty, which BaseInstructionComposer treats as a no-op.
        public void WithScaffold(string scaffoldText)
        {
            _instructionProvider.BaseScaffold = scaffoldText;
        }

        public SubAgentSpawnService Build()
        {
            return new SubAgentSpawnService(_capacity,
                new SpawnSerializer(),
                _definitionStore,
                _resolver,
                _toolRegistry,
                new EmptyClientLocalToolRegistry(),
                new EmptyMcpToolRegistry(),
                _chatClient,
                Options.Create(new SpawnOptions
                {
                    QueueWaitSeconds = 5
                }),
                _instructionProvider,
                _modelCapabilityResolver,
                NullLoggerFactory.Instance,
                _logger);
        }

        public void Dispose()
        {
            _chatClient.Dispose();
        }
    }

    // A catalog that resolves the curated child tool names. Includes spawn_subagent so a test can prove it is filtered
    // out of the child set even when the profile lists it.
    private sealed class FakeAgentToolRegistry : IAgentToolRegistry
    {
        private static readonly IReadOnlyList<AITool> Tools =
        [
            AIFunctionFactory.Create((string input) => input, "spawn_subagent"),
            AIFunctionFactory.Create((string input) => input, "read_file"),
            AIFunctionFactory.Create((string input) => input, "list_files")
        ];

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return Tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return [];
        }
    }

    private sealed class EmptyClientLocalToolRegistry : IClientLocalToolRegistry
    {
        public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
        {
            tool = null;
            return false;
        }
    }

    private sealed class EmptyMcpToolRegistry : IMcpToolRegistry
    {
        public bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool)
        {
            tool = null;
            return false;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return [];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            // Not used by the spawn tests.
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;

        public TrackingDisposable(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _onDispose();
            }
        }
    }
}
