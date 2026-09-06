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
using XE_Local_AI_Engine.Client.Services.Mcp;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
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
    public async Task Spawn_WhenTheParentIsADeclaredCloudExternalModel_IsRefused()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.TrustResolver.Register("hosted-box", "qwen3", ExternalProviderLocality.Cloud);
        var service = harness.Build();

        // The laundering path this closes: the parent runs on a hosted endpoint that is denied the workspace,
        // knowledge-base and custom tools directly, and delegates to a child bound to a node-local model that has all
        // three — whose answer is returned into the parent's transcript. The offer gate withholds spawn_subagent from
        // such a parent; this is the seam behind it, for a profile or a caller that reaches the service anyway.
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3, rootModelId: "ext:hosted-box/qwen3");
        var result = await service.SpawnAsync(ModelRequest("read the workspace and tell me"), CancellationToken.None);

        AssertEx.Contains(result, "outside this node's trust boundary");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task Spawn_WhenTheParentIsAnUnresolvedExternalModel_IsRefused()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        // Nothing registered for this id: a deleted connection, an unreadable store, or the pre-boot window. Only a
        // positively resolved local declaration may delegate.
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3, rootModelId: "ext:gone-box/qwen3");
        var result = await service.SpawnAsync(ModelRequest("do the thing"), CancellationToken.None);

        AssertEx.Contains(result, "outside this node's trust boundary");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task Spawn_WhenTheChildModelIsExternal_ItsOwnSendsArePinned()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        _ = harness.ExternalRegistry.Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var service = harness.Build();

        // The parent turn's pin is keyed by the PARENT model, so a child on its own external connection had no pin at
        // all: its sends fell through to the transport's weaker unpinned check while the child ran with a tool set
        // authorized against the declaration read at spawn time.
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            ModelId = ExternalProviderTestData.ModelId,
            Task = "do the thing"
        }, CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
        AssertEx.Equal(ExternalProviderTestData.ModelId, harness.ChatClient.LastModelId);
        var pin = AssertEx.NotNull(harness.ChatClient.LastBindingPin);
        AssertEx.Equal(ExternalProviderTestData.ModelId, pin.ModelId);
        AssertEx.Equal(ExternalProviderLocality.Local, pin.Locality);
    }

    [Test]
    public async Task Spawn_WhenTheChildModelIsLocal_SeedsNoPin()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        _ = await service.SpawnAsync(ModelRequest("do the thing"), CancellationToken.None);

        // A GGUF child has no external binding to pin, and inventing one would be a claim the registry never made.
        AssertEx.Null(harness.ChatClient.LastBindingPin);
    }

    [Test]
    public async Task Spawn_WhenTheParentIsADeclaredLocalExternalModel_IsAllowed()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.TrustResolver.Register("unsloth-box", "qwen3");
        var service = harness.Build();

        // Full local parity is the point of the declared-Local flag: the guard is locality, not externality.
        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3, rootModelId: "ext:unsloth-box/qwen3");
        var result = await service.SpawnAsync(ModelRequest("do the thing"), CancellationToken.None);

        AssertEx.Equal("sub-agent-result", result);
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

        await harness.Resolver.Received().ResolveAsync(Arg.Is<AgentDefinitionRecord>(record => record.Id == id),
            cloudModel,
            Arg.Any<string?>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<bool>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Spawn_WhenTheStoreReturnsTwoVersions_AssemblesTheChildFromOneSnapshot()
    {
        // A profile-bound child must be assembled from exactly ONE definition snapshot. The store here hands out a
        // DIFFERENT record on a second read, which is what a concurrent edit does: the spawn takes the model from its
        // own read and, if it re-resolves by id, the prompt from a second one — a child running version A's model on
        // version B's persona. One read, one snapshot, or the two halves can disagree.
        using var harness = new Harness();
        harness.AllowLocal();
        const string modelBeforeTheEdit = "model-before-the-edit";
        var id = Guid.NewGuid();
        var first = RacingDefinition(id, modelBeforeTheEdit, instructions: "instructions before the edit");
        var second = first with
        {
            ModelProfile = "model-after-the-edit",
            Instructions = "instructions after the edit",
            Version = 2
        };
        harness.RegisterRacingProfile(id, first, second);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = id.ToString(),
            Task = "do it"
        }, CancellationToken.None);

        // Model and prompt must come from the SAME record, and the definition must have been read exactly once.
        AssertEx.Equal(modelBeforeTheEdit, harness.ChatClient.LastModelId);
        AssertEx.Equal(ProjectedPromptFor(first), harness.ChatClient.LastInstructions);
        await harness.DefinitionStore.Received(1).GetByIdAsync(id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Spawn_WhenTheDefinitionIsMissing_RejectsAsUnresolved()
    {
        // A spawn naming a definition the store does not have is still rejected as unresolved, not fabricated. The
        // seeded root is load-bearing: with no ambient context the fan-out check rejects first and this would pin the
        // wrong reason.
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var result = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = Guid.NewGuid().ToString(),
            Task = "do it"
        }, CancellationToken.None);

        AssertEx.Contains(result, "could not be resolved");
        AssertEx.Equal(0, harness.ChatClient.CallCount);
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
    public async Task SpawnForMcp_WhenBareBindingRuns_UsesNoModelVisibleTools()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "bare instructions",
            AgentDefinitionId: null,
            AgentDefinitionVersion: null,
            AllowedTools: [],
            ReasoningEffort: null,
            SupportsThinking: false));
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            ModelId = Model
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.Empty(harness.ChatClient.LastToolNames);
        AssertEx.Equal(0, harness.WorkspaceSessionFactory.OpenCallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhenGeneralSavedBindingRuns_SuppressesToolsSkillsAndContextProviders()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "general instructions without skill discovery",
            AgentDefinitionId: Guid.NewGuid(),
            AgentDefinitionVersion: 4,
            AllowedTools: [],
            ReasoningEffort: null,
            SupportsThinking: false));
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            AgentKey = "General"
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.Empty(harness.ChatClient.LastToolNames);
        AssertEx.Equal("general instructions without skill discovery", harness.ChatClient.LastInstructions);
        AssertEx.Equal(0, harness.WorkspaceSessionFactory.OpenCallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhenAgenticSavedBindingRuns_ResolvesFullOfferAndAdaptsApprovalWrapper()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.AgenticToolAdapter.Adapt(Arg.Any<ApprovalRequiredAIFunction>(),
                   ToolCategory.WriteExecute,
                   Arg.Any<McpInboundExecutionContext>(),
                   Arg.Any<Guid>())
               .Returns(AIFunctionFactory.Create((string input) => input, "read_file"));
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "agentic instructions",
            AgentDefinitionId: Guid.NewGuid(),
            AgentDefinitionVersion: 4,
            AllowedTools:
            [
                McpTool("read_file") with
                {
                    Category = ToolCategory.WriteExecute,
                    RequiresApproval = true
                },
                McpTool("list_files")
            ],
            ReasoningEffort: null,
            SupportsThinking: false));
        var requestId = Guid.NewGuid();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            AgentKey = "General",
            InboundContext = new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, "xemcp_abc123"),
            ExecutionRequestId = requestId
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.True(harness.ChatClient.LastToolNames.OrderBy(static name => name, StringComparer.Ordinal)
                             .SequenceEqual(["list_files", "read_file"], StringComparer.Ordinal));
        _ = harness.AgenticToolAdapter.Received(1).Adapt(Arg.Any<ApprovalRequiredAIFunction>(),
            ToolCategory.WriteExecute,
            Arg.Is<McpInboundExecutionContext>(context => context.IsAgentic && context.KeyPrefix == "xemcp_abc123"),
            requestId);
    }

    [Test]
    public async Task SpawnForMcp_WhenAgenticCustomToolIsEnabled_ResolvesAsyncAndStrictlyAuditsInvocation()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        var events = new List<string>();
        var audit = Substitute.For<IMcpAgenticApprovalAuditRecorder>();
        audit.RecordAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<ToolCategory>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(_ =>
             {
                 events.Add("audit");
                 return Task.CompletedTask;
             });
        var customInner = AIFunctionFactory.Create(() => events.Add("inner"), "custom__weather");
        harness.CustomToolCatalog.Set("custom__weather", new ApprovalRequiredAIFunction(customInner));
        harness.UseAgenticToolAdapter(new McpAgenticToolAdapter(audit, NullLogger<McpAgenticToolAdapter>.Instance));
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "agentic custom tool instructions",
            AgentDefinitionId: Guid.NewGuid(),
            AgentDefinitionVersion: 4,
            AllowedTools:
            [
                McpTool("custom__weather") with
                {
                    Category = ToolCategory.WriteExecute,
                    RequiresApproval = true
                }
            ],
            ReasoningEffort: null,
            SupportsThinking: false));
        var requestId = Guid.NewGuid();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await harness.Build().SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            AgentKey = "General",
            InboundContext = new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, "xemcp_abc123"),
            ExecutionRequestId = requestId
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);
        var executable = (AIFunction)harness.ChatClient.LastTools.Single(static tool => tool.Name == "custom__weather");
        _ = await executable.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.True(events.SequenceEqual(["audit", "inner"], StringComparer.Ordinal));
        AssertEx.Equal(expected: 1, harness.CustomToolCatalog.ResolveCallCount("custom__weather"));
        await audit.Received(1).RecordAsync(requestId,
            "custom__weather",
            ToolCategory.WriteExecute,
            "xemcp_abc123",
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SpawnForMcp_WhenAgenticCustomToolIsDisabledOrUnresolved_FailsClosed()
    {
        foreach (var name in new[]
                 {
                     "custom__disabled",
                     "custom__unknown"
                 })
        {
            using var harness = new Harness();
            harness.AllowLocal();
            harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
                Model,
                "agentic custom tool instructions",
                Guid.NewGuid(),
                AgentDefinitionVersion: 1,
                [
                    McpTool(name) with
                    {
                        RequiresApproval = true
                    }
                ],
                ReasoningEffort: null,
                SupportsThinking: false));
            using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);

            var outcome = await harness.Build().SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "General",
                InboundContext = new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, "xemcp_abc123"),
                ExecutionRequestId = Guid.NewGuid()
            }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

            AssertEx.Equal(SpawnOutcomeKind.Rejected, outcome.Kind);
            AssertEx.Equal(0, harness.ChatClient.CallCount);
            AssertEx.Equal(expected: 1, harness.CustomToolCatalog.ResolveCallCount(name));
        }
    }

    [Test]
    public async Task SpawnForMcp_WhenAgenticOfferIsMissingOrDuplicated_FailsClosed()
    {
        foreach (var tools in new[]
                 {
                     new[]
                     {
                         McpTool("missing")
                     },
                     new[]
                     {
                         McpTool("read_file"),
                         McpTool("read_file")
                     },
                     new[]
                     {
                         McpTool("custom__duplicate"),
                         McpTool("custom__duplicate")
                     }
                 })
        {
            using var harness = new Harness();
            harness.AllowLocal();
            harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
                Model,
                "agentic instructions",
                Guid.NewGuid(),
                AgentDefinitionVersion: 1,
                tools,
                ReasoningEffort: null,
                SupportsThinking: false));
            using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);

            var outcome = await harness.Build().SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "General",
                InboundContext = new McpInboundExecutionContext(McpServerApiKeyScope.Agentic, "xemcp_abc123"),
                ExecutionRequestId = Guid.NewGuid()
            }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

            AssertEx.Equal(SpawnOutcomeKind.Rejected, outcome.Kind);
            AssertEx.Equal(0, harness.ChatClient.CallCount);
            if (tools[0].Name == "custom__duplicate")
            {
                AssertEx.Equal(0, harness.CustomToolCatalog.ResolveCallCount("custom__duplicate"));
            }
        }
    }

    [Test]
    public async Task SpawnForMcp_WhenAuthorizedWorkspaceCoderBindingRuns_UsesExactlyThreeReadOnlyExecutables()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "coder instructions",
            AgentDefinitionId: Guid.NewGuid(),
            AgentDefinitionVersion: 2,
            AllowedTools:
            [
                McpTool("list_files"),
                McpTool("read_file"),
                McpTool("search_text")
            ],
            ReasoningEffort: null,
            SupportsThinking: false));
        using var workspaceSession = new TrackingWorkspaceSession();
        harness.WorkspaceSessionFactory.Result = McpWorkspaceExecutionSessionOpenResult.Success(workspaceSession);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            AgentKey = "Coder (read-only)",
            ModelOverrideId = Model
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None, Guid.NewGuid());

        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.True(harness.ChatClient.LastToolNames.OrderBy(static name => name, StringComparer.Ordinal)
                             .SequenceEqual(["list_files", "read_file", "search_text"], StringComparer.Ordinal),
            "the final MCP Coder agent must expose exactly the three read-only workspace executables.");
        AssertEx.True(workspaceSession.IsDisposed, "the successful workspace session must be released after inference.");
    }

    [Test]
    public async Task SpawnForMcp_WhenWorkspaceCoderHasNoWorkspace_RejectsBeforeCapacityOrInference()
    {
        using var harness = new Harness();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspaceNotAuthorized, outcome.FailureCode!);
        AssertEx.Equal(0, harness.ChatClient.CallCount);
        AssertEx.Equal(0, harness.WorkspaceSessionFactory.OpenCallCount);
        await harness.Capacity.DidNotReceiveWithAnyArgs().DecideAsync(default!, default, default);
    }

    [Test]
    public async Task SpawnForMcp_WhenCoderExecutableIsUnregistered_RejectsBindingWithoutRunning()
    {
        using var harness = new Harness(includeSearchText: false);
        harness.AllowLocal();
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "coder instructions",
            AgentDefinitionId: Guid.NewGuid(),
            AgentDefinitionVersion: 2,
            AllowedTools:
            [
                McpTool("list_files"),
                McpTool("read_file"),
                McpTool("search_text")
            ],
            ReasoningEffort: null,
            SupportsThinking: false));
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            AgentKey = "Coder (read-only)",
            ModelOverrideId = Model
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Rejected, outcome.Kind);
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhenCoderBindingDuplicatesToolName_RejectsBindingWithoutRunning()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(new McpExecutionBinding("fingerprint",
            Model,
            "coder instructions",
            AgentDefinitionId: Guid.NewGuid(),
            AgentDefinitionVersion: 2,
            AllowedTools:
            [
                McpTool("list_files"),
                McpTool("list_files"),
                McpTool("read_file"),
                McpTool("search_text")
            ],
            ReasoningEffort: null,
            SupportsThinking: false));
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
        {
            AgentKey = "Coder (read-only)",
            ModelOverrideId = Model
        }, "inspect", expectedBindingFingerprint: null, CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Rejected, outcome.Kind);
        AssertEx.Equal(0, harness.ChatClient.CallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhenCapacityRejectsWorkspace_DoesNotResolveOrLeaseWorkspace()
    {
        using var harness = new Harness();
        harness.RejectInsufficient();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            Guid.NewGuid());

        AssertEx.Equal(McpExecutionFailureCodes.CapacityDeclined, outcome.FailureCode!);
        AssertEx.Equal(0, harness.WorkspaceSessionFactory.OpenCallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhileCapacityDecisionIsPending_DoesNotResolveOrLeaseWorkspace()
    {
        using var harness = new Harness();
        var decisionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<CapacityDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.DelayCapacity(decisionEntered, decision);
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var pending = service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            Guid.NewGuid());
        await decisionEntered.Task.ConfigureAwait(false);

        AssertEx.Equal(0, harness.WorkspaceSessionFactory.OpenCallCount);
        decision.SetResult(new CapacityDecision(CapacityVerdict.RejectInsufficient, "Insufficient capacity.", OllamaEvictionWarning: false));
        _ = await pending.ConfigureAwait(false);
    }

    [Test]
    public async Task SpawnForMcp_WhenWorkspaceIsRevokedAfterAdmission_FailsBeforeInference()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        harness.WorkspaceSessionFactory.Result = McpWorkspaceExecutionSessionOpenResult.Rejected(McpExecutionFailureCodes.WorkspaceNotAuthorized,
            "Cannot run: the selected workspace is not authorized.");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            Guid.NewGuid());

        AssertEx.Equal(McpExecutionFailureCodes.WorkspaceNotAuthorized, outcome.FailureCode!);
        AssertEx.Equal(0, harness.ChatClient.CallCount);
        AssertEx.True(harness.ReservationDisposed, "capacity reservation must be released when authorization changed.");
        AssertEx.Equal(1, harness.WorkspaceSessionFactory.OpenCallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhenWorkspaceAllowed_HoldsSessionThroughInferenceAndReleasesBeforeReturn()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        var workspaceId = Guid.NewGuid();
        using var session = new TrackingWorkspaceSession();
        var ambientDisposed = false;
        session.AmbientFactory = () => new TrackingDisposable(() => ambientDisposed = true);
        harness.WorkspaceSessionFactory.Result = McpWorkspaceExecutionSessionOpenResult.Success(session);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            workspaceId);

        AssertEx.Equal(SpawnOutcomeKind.Success, outcome.Kind);
        AssertEx.True(ambientDisposed, "ambient access must end before the MCP execution call returns.");
        AssertEx.True(session.IsDisposed, "the owner-node lease must be released before the MCP execution call returns.");
        AssertEx.True(harness.ReservationDisposed, "the capacity reservation must be released on the same return path.");
        AssertEx.Equal(1, session.EnterAmbientScopeCallCount);
    }

    [Test]
    public async Task SpawnForMcp_WhenWorkspaceLeaseIsBusy_ReturnsStableBusyCodeWithoutInference()
    {
        using var harness = new Harness();
        harness.QueueSameModel();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        var workspaceId = Guid.NewGuid();
        harness.WorkspaceSessionFactory.Result = McpWorkspaceExecutionSessionOpenResult.Rejected(McpExecutionFailureCodes.WorkspaceBusy,
            "Cannot run: the selected workspace is busy.");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            workspaceId);

        AssertEx.Equal(McpExecutionFailureCodes.WorkspaceBusy, outcome.FailureCode!);
        AssertEx.Equal(0, harness.ChatClient.CallCount);
        AssertEx.False(outcome.DisplayMessage.Contains("/not-observable", StringComparison.Ordinal), "busy response must not expose host paths.");
    }

    [Test]
    public async Task SpawnForMcp_WhenWorkspaceRecoveryCannotProveClean_RefusesInferenceAndReleasesCapacity()
    {
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        harness.WorkspaceSessionFactory.Result = McpWorkspaceExecutionSessionOpenResult.Rejected(McpExecutionFailureCodes.WorkspacePreparationFailed,
            "Cannot run: the selected workspace could not be prepared safely.");
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            Guid.NewGuid());

        AssertEx.Equal(McpExecutionFailureCodes.WorkspacePreparationFailed, outcome.FailureCode!);
        AssertEx.Equal(0, harness.ChatClient.CallCount);
        AssertEx.True(harness.ReservationDisposed, "capacity must release after workspace isolation fails closed.");
    }

    [Test]
    public async Task SpawnForMcp_WhenQueuedWorkspaceRuns_HoldsSessionUntilInferenceCompletes()
    {
        using var harness = new Harness();
        harness.QueueSameModel();
        harness.ResolveMcpBinding(WorkspaceCoderBinding());
        var workspaceId = Guid.NewGuid();
        var inferenceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ChatClient.HoldUntil(inferenceGate.Task);
        using var session = new TrackingWorkspaceSession();
        harness.WorkspaceSessionFactory.Result = McpWorkspaceExecutionSessionOpenResult.Success(session);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var pending = service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                AgentKey = "Coder"
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None,
            workspaceId);
        await harness.ChatClient.WaitUntilRunningAsync().ConfigureAwait(false);

        AssertEx.False(session.IsDisposed, "queued execution must retain the workspace lease through inference.");
        inferenceGate.SetResult();
        _ = await pending.ConfigureAwait(false);
        AssertEx.True(session.IsDisposed, "queued execution must release the workspace lease before returning.");
    }

    [Test]
    public async Task SpawnForMcp_WhenNodeMessageTimeoutElapses_FailsWithTimedOutCode()
    {
        // An inbound MCP run once had no whole-turn deadline — only the dispatcher's coarse watchdog and the transport's
        // own timeout bounded it. The deadline lives at THIS boundary so both front doors (synchronous run_agent and the
        // detached executor) are bounded once, by the same operator knob as a local send. The chat client hangs on its
        // token, so this fails if the deadline is dropped; the outcome must be a distinguishable typed failure.
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(BareBinding());
        harness.WithMaxMessageRequestTimeoutSeconds(seconds: 0);
        var inferenceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ChatClient.HoldUntil(inferenceGate.Task);
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var outcome = await service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                ModelId = Model
            },
            "inspect",
            expectedBindingFingerprint: null,
            CancellationToken.None);

        AssertEx.Equal(SpawnOutcomeKind.Failed, outcome.Kind);
        AssertEx.Equal(McpExecutionFailureCodes.TimedOut, outcome.FailureCode!);
        inferenceGate.SetResult();
    }

    [Test]
    public async Task SpawnForMcp_WhenCallerCancels_DoesNotMasqueradeAsATimeout()
    {
        // The caller's own cancellation (operator cancel / dispatcher watchdog / host shutdown) must still escape so the
        // durable stop marker chooses the terminal outcome — the timeout catch must not swallow it.
        using var harness = new Harness();
        harness.AllowLocal();
        harness.ResolveMcpBinding(BareBinding());
        harness.WithMaxMessageRequestTimeoutSeconds(seconds: 3600);
        var inferenceGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.ChatClient.HoldUntil(inferenceGate.Task);
        var service = harness.Build();
        using var caller = new CancellationTokenSource();

        using var root = SpawnContext.BeginRoot(fanOutCap: 3, cloudSpawnCap: 3);
        var pending = service.SpawnForMcpAsync(new McpExecutionBindingRequest
            {
                ModelId = Model
            },
            "inspect",
            expectedBindingFingerprint: null,
            caller.Token);
        await harness.ChatClient.WaitUntilRunningAsync().ConfigureAwait(false);
        await caller.CancelAsync().ConfigureAwait(false);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => pending).ConfigureAwait(false);
        inferenceGate.SetResult();
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
    public async Task Spawn_WhenFanOutIsSaturatedAndTheBindingIsUnresolvable_RejectsForFanOut()
    {
        // A saturated turn rejects BEFORE it pays for a binding resolution — a definition store read plus a full
        // projection — so the fan-out reason wins over the unresolved one and the resolver is never asked. The first
        // spawn is a bare model binding on purpose: that path short-circuits without touching the resolver, so the
        // zero-calls assertion is about the SECOND spawn alone rather than being vacuous.
        using var harness = new Harness();
        harness.AllowLocal();
        var service = harness.Build();

        using var root = SpawnContext.BeginRoot(fanOutCap: 1, cloudSpawnCap: 3);
        var gate = new TaskCompletionSource();
        harness.ChatClient.HoldUntil(gate.Task);

        var first = service.SpawnAsync(ModelRequest("first"), CancellationToken.None);
        await harness.ChatClient.WaitUntilRunningAsync();

        var secondResult = await service.SpawnAsync(new SubAgentSpawnRequest
        {
            SubAgentKey = Guid.NewGuid().ToString(),
            Task = "second"
        }, CancellationToken.None);

        AssertEx.Contains(secondResult, "concurrent sub-agents");
        AssertEx.Empty(harness.Resolver.ReceivedCalls(), "a spawn rejected for fan-out must not pay for a binding resolution");

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

    // A definition whose model and persona are both distinctive, so a child assembled from two versions of it is visible
    // in the model id AND in the instructions rather than in only one of them.
    private static AgentDefinitionRecord RacingDefinition(Guid id, string modelProfile, string instructions)
    {
        return new AgentDefinitionRecord(id,
            "racing-child",
            Description: null,
            instructions,
            modelProfile,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);
    }

    private static string ProjectedPromptFor(AgentDefinitionRecord definition)
    {
        return $"SCAFFOLD+{definition.Instructions}";
    }

    private static AllowedToolDto McpTool(string name)
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            ParameterSchema = "{\"type\":\"object\"}",
            RequiresApproval = false,
            Category = ToolCategory.ReadLocal
        };
    }

    // A bare model binding: no agent definition, no tools, no workspace — the shortest path from SpawnForMcpAsync to
    // the inner run, used by the deadline tests that only care about how the run ends.
    private static McpExecutionBinding BareBinding() =>
        new("fingerprint",
            Model,
            "bare instructions",
            AgentDefinitionId: null,
            AgentDefinitionVersion: null,
            AllowedTools: [],
            ReasoningEffort: null,
            SupportsThinking: false);

    private static McpExecutionBinding WorkspaceCoderBinding() =>
        new("fingerprint",
            Model,
            "coder instructions",
            Guid.NewGuid(),
            AgentDefinitionVersion: 1,
            AllowedTools: [McpTool("list_files"), McpTool("read_file"), McpTool("search_text")],
            ReasoningEffort: null,
            SupportsThinking: false);

    // Assembles the spawn service over a mocked capacity verdict + a real SpawnSerializer + a gateable RecordingChatClient.
    private sealed class Harness : IDisposable
    {
        private readonly GateableChatClient _chatClient;
        private readonly ICapacityService _capacity = Substitute.For<ICapacityService>();
        private readonly IAgentDefinitionResolver _resolver = Substitute.For<IAgentDefinitionResolver>();
        private readonly IAgentDefinitionStore _definitionStore = Substitute.For<IAgentDefinitionStore>();
        private readonly IModelCapabilityResolver _modelCapabilityResolver = Substitute.For<IModelCapabilityResolver>();
        public FakeModelTrustResolver TrustResolver { get; } = new();
        private readonly FakeAgentInstructionProvider _instructionProvider = new();
        private readonly IAgentToolRegistry _toolRegistry;
        private readonly IMcpExecutionBindingResolver _mcpExecutionBindingResolver = Substitute.For<IMcpExecutionBindingResolver>();
        private readonly FakeCustomToolCatalog _customToolCatalog = new();
        private IMcpAgenticToolAdapter _mcpAgenticToolAdapter = Substitute.For<IMcpAgenticToolAdapter>();
        private readonly FakeMcpWorkspaceExecutionSessionFactory _mcpWorkspaceSessionFactory = new();
        private readonly CapturingLogger<SubAgentSpawnService> _logger = new();
        private readonly INodeSettingsStore _nodeSettingsStore = Substitute.For<INodeSettingsStore>();
        private bool _reservationDisposed;

        public Harness(TimeSpan? delayBeforeResponse = null, bool includeSearchText = true)
        {
            _chatClient = new GateableChatClient(delayBeforeResponse: delayBeforeResponse);
            _toolRegistry = new FakeAgentToolRegistry(includeSearchText);
            // Default: the child model advertises the thinking capability, so a resolved reasoning effort is honored on
            // the Ollama think key. A test can flip this to prove a non-thinking model omits the field.
            _modelCapabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                                    .Returns(new ModelCapabilitySnapshot(SupportsThinking: true, SupportsTools: true, IsCloud: false));
            WithMaxMessageRequestTimeoutSeconds(StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds);
        }

        // The node "Maximum message request timeout" that bounds an inbound MCP run inside SpawnForMcpAsync.
        public void WithMaxMessageRequestTimeoutSeconds(int seconds)
        {
            _nodeSettingsStore.LoadAsync(Arg.Any<CancellationToken>())
                              .Returns(new StoredNodeSettings
                              {
                                  MaxMessageRequestTimeoutSeconds = seconds
                              });
        }

        // Overrides the child model's advertised thinking capability (default true), gating whether a resolved reasoning
        // effort reaches the Ollama think key or is omitted (a non-thinking model 400s on think:true/level).
        public void WithThinkingCapability(bool supportsThinking)
        {
            _modelCapabilityResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
                                    .Returns(new ModelCapabilitySnapshot(SupportsThinking: supportsThinking, SupportsTools: true, IsCloud: false));
        }

        public GateableChatClient ChatClient => _chatClient;

        /// <summary>The registry the child's own binding pin is read from — seed it to make a child model external.</summary>
        public FakeExternalProviderRegistry ExternalRegistry { get; } = new();

        public ICapacityService Capacity => _capacity;

        public IAgentDefinitionResolver Resolver => _resolver;

        /// <summary>The definition store the spawn reads its snapshot from — exposed so a test can count the reads.</summary>
        public IAgentDefinitionStore DefinitionStore => _definitionStore;

        public FakeMcpWorkspaceExecutionSessionFactory WorkspaceSessionFactory => _mcpWorkspaceSessionFactory;

        public IMcpAgenticToolAdapter AgenticToolAdapter => _mcpAgenticToolAdapter;

        public FakeCustomToolCatalog CustomToolCatalog => _customToolCatalog;


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
            _resolver.ResolveAsync(Arg.Is<AgentDefinitionRecord>(record => record.Id == id),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<CancellationToken>())
                     .Returns(new ResolvedAgentRuntime("child instructions", offered, modelProfile, null, 1, id, name, []));
            return id;
        }

        // The resolver's projection, faithful in the one respect the racing test turns on: the resolved prompt and the
        // resolved model profile both come from the SAME record, so a mismatch between them names the version each half
        // came from.
        private static ResolvedAgentRuntime Project(AgentDefinitionRecord definition)
        {
            return new ResolvedAgentRuntime(ProjectedPromptFor(definition),
                [],
                definition.ModelProfile,
                definition.ReasoningEffort,
                definition.Version,
                definition.Id,
                definition.Name,
                []);
        }

        // Registers a definition whose store hands out a DIFFERENT record on a second read, and a resolver that behaves
        // like the real one on BOTH overloads: the id overload re-reads the store (it is the second read), the record
        // overload projects the snapshot it was handed and reads nothing. That difference is the whole subject — with
        // one read the two halves of the child cannot come from two versions.
        public void RegisterRacingProfile(Guid id, AgentDefinitionRecord first, AgentDefinitionRecord second)
        {
            _definitionStore.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns(first, second);

            _resolver.ResolveAsync(id, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                     .Returns<Task<ResolvedAgentRuntime?>>(async call =>
                     {
                         var reread = await _definitionStore.GetByIdAsync(id, call.Arg<CancellationToken>()).ConfigureAwait(false);
                         return Project(reread!);
                     });

            _resolver.ResolveAsync(Arg.Any<AgentDefinitionRecord>(),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<CancellationToken>())
                     .Returns<Task<ResolvedAgentRuntime?>>(call => Task.FromResult<ResolvedAgentRuntime?>(Project(call.Arg<AgentDefinitionRecord>())));
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
            _resolver.ResolveAsync(Arg.Is<AgentDefinitionRecord>(record => record.Id == id),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<CancellationToken>())
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
            _resolver.ResolveAsync(Arg.Is<AgentDefinitionRecord>(record => record.Id == id),
                         Arg.Any<string?>(),
                         Arg.Any<string?>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<bool>(),
                         Arg.Any<CancellationToken>())
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

        public void DelayCapacity(TaskCompletionSource entered, TaskCompletionSource<CapacityDecision> decision)
        {
            _capacity.DecideAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                     .Returns(async _ =>
                     {
                         entered.TrySetResult();
                         return await decision.Task.ConfigureAwait(false);
                     });
        }

        // Configures the base scaffold text the default sub-agent instructions are composed with. Unconfigured
        // (the default) returns null/empty, which BaseInstructionComposer treats as a no-op.
        public void WithScaffold(string scaffoldText)
        {
            _instructionProvider.BaseScaffold = scaffoldText;
        }

        public void ResolveMcpBinding(McpExecutionBinding binding)
        {
            _mcpExecutionBindingResolver.ResolveAsync(Arg.Any<McpExecutionBindingRequest>(), Arg.Any<CancellationToken>())
                                        .Returns(McpExecutionBindingResolution.Success(binding));
        }

        public void UseAgenticToolAdapter(IMcpAgenticToolAdapter adapter)
        {
            _mcpAgenticToolAdapter = adapter;
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
                _customToolCatalog,
                _chatClient,
                Options.Create(new SpawnOptions
                {
                    QueueWaitSeconds = 5
                }),
                _instructionProvider,
                _modelCapabilityResolver,
                TrustResolver,
                _mcpExecutionBindingResolver,
                _mcpAgenticToolAdapter,
                _mcpWorkspaceSessionFactory,
                _nodeSettingsStore,
                ExternalRegistry,
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
        private readonly IReadOnlyList<AITool> _tools;

        public FakeAgentToolRegistry(bool includeSearchText)
        {
            var tools = new List<AITool>
            {
                AIFunctionFactory.Create((string input) => input, "spawn_subagent"),
                AIFunctionFactory.Create((string input) => input, "read_file"),
                AIFunctionFactory.Create((string input) => input, "list_files")
            };
            if (includeSearchText)
            {
                tools.Add(AIFunctionFactory.Create((string input) => input, "search_text"));
            }

            _tools = tools;
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return _tools;
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

    internal sealed class FakeCustomToolCatalog : ICustomToolCatalog
    {
        private readonly Dictionary<string, int> _calls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);
        }

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(names);
            cancellationToken.ThrowIfCancellationRequested();

            // One batch call now covers every requested name, so the per-name counter is incremented once per REQUESTED
            // name. ResolveCallCount(name) therefore keeps meaning "how many times was this name put to the catalog".
            var resolved = new Dictionary<string, AITool>(StringComparer.Ordinal);
            foreach (var name in names)
            {
                _calls[name] = ResolveCallCount(name) + 1;
                if (_tools.TryGetValue(name, out var tool))
                {
                    resolved[name] = tool;
                }
            }

            return Task.FromResult<IReadOnlyDictionary<string, AITool>>(resolved);
        }

        public void Set(string name, AITool tool)
        {
            _tools[name] = tool;
        }

        public int ResolveCallCount(string name)
        {
            return _calls.GetValueOrDefault(name);
        }
    }

    private sealed class FakeMcpWorkspaceExecutionSessionFactory : IMcpWorkspaceExecutionSessionFactory
    {
        public int OpenCallCount { get; private set; }

        public McpWorkspaceExecutionSessionOpenResult? Result { get; set; }

        public Task<McpWorkspaceExecutionSessionOpenResult> OpenAsync(Guid workspaceId,
            CancellationToken cancellationToken)
        {
            if (workspaceId == Guid.Empty)
            {
                throw new ArgumentException("Workspace id must be opaque and non-empty.", nameof(workspaceId));
            }

            cancellationToken.ThrowIfCancellationRequested();
            OpenCallCount++;
            return Task.FromResult(Result ?? throw new InvalidOperationException("No workspace session result was configured for this test."));
        }
    }

    private sealed class TrackingWorkspaceSession : IMcpWorkspaceExecutionSession
    {
        private int _disposed;

        public Func<IDisposable> AmbientFactory { get; set; } = static () => new TrackingDisposable(static () => { });

        public int EnterAmbientScopeCallCount { get; private set; }

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public IDisposable EnterAmbientScope()
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);

            EnterAmbientScopeCallCount++;
            return AmbientFactory();
        }

        public void Dispose()
        {
            _ = Interlocked.Exchange(ref _disposed, 1);
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
