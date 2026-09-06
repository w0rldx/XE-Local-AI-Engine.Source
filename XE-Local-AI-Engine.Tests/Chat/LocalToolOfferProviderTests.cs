namespace XE_Local_AI_Engine.Tests.Chat;

using System.Collections.ObjectModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Tools;
using XE_Local_AI_Engine.Client.Services.Capacity.Tools;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     Capability gate (AgentHome Decision 7): the loopback offer omits <c>run_in_agent_home</c> and every MCP tool
///     when the active model is not in <see cref="AgentHomeOptions.ToolCapableModels" />, and offers them when it is.
///     The offer/known-name/known-tool surfaces merge the live MCP snapshot, MCP tools join the
///     capable-only set, and <c>GetKnownTools</c> tags each entry with its source.
/// </summary>
public sealed class LocalToolOfferProviderTests
{
    [Test]
    public void GetOfferedTools_WhenModelIsToolCapable_OffersAgentHomeTool()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.Contains(offered, tool => tool.Name == AgentHomeToolDefinition.ToolName);
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsNotToolCapable_OmitsAgentHomeToolButKeepsOthers()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("some-other-model");

        AssertEx.False(offered.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "run_in_agent_home must be withheld from a model that is not in ToolCapableModels");
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetOfferedTools_WhenActiveModelEqualsToolCapableEntry_OffersAgentHomeTool()
    {
        // Regression: the live-evidence model id (qwen3:8b, the default ToolCapableModels entry) MUST satisfy
        // the gate when it is the offer-time active model — the bug was that this model never reached this seam, not
        // that the seam mismatched it. An exact match offers run_in_agent_home.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.Contains(offered, tool => tool.Name == AgentHomeToolDefinition.ToolName);
    }

    [Test]
    public void GetOfferedTools_WhenAllowlistChangesAfterConstruction_TakesEffectWithoutARestart()
    {
        // THE regression test. The allow-list used to be captured into a HashSet at DI composition, so
        // an operator could add their model in Node Settings, save successfully, and still be offered no tools until the
        // node restarted — with no restart hint on that field. The provider now reads INodeRuntimeSettings live on every
        // offer, so a change between two calls on the SAME instance must be observed.
        //
        // The mutable list is the whole point: a Build()-time snapshot would pass a weaker version of this test even
        // with the old seeded implementation, because construction would capture the already-correct value.
        var toolCapableModels = new List<string>();
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetToolCapableModels().Returns(_ => toolCapableModels);

        var provider = new LocalToolOfferProvider(new FakeAgentToolRegistry([
                new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", "{\"type\":\"object\"}", RequiresApproval: true),
                new LocalChatToolDescriptor("open_url", "Opens a URL.", "{\"type\":\"object\"}", RequiresApproval: false)
            ]),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            runtimeSettings,
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);

        var beforeEdit = provider.GetOfferedTools("unsloth/gemma-4-12b-it-GGUF:Q5_K_M");
        AssertEx.False(beforeEdit.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "A model absent from the allow-list must not be offered the capable-only tools.");

        // The operator adds the model in Node Settings and saves. No restart.
        toolCapableModels.Add("unsloth/gemma-4-12b-it-GGUF:Q5_K_M");

        var afterEdit = provider.GetOfferedTools("unsloth/gemma-4-12b-it-GGUF:Q5_K_M");
        AssertEx.Contains(afterEdit, tool => tool.Name == AgentHomeToolDefinition.ToolName,
            "The allow-list is read live, so an edit must take effect on the very next offer without a node restart.");

        // And the reverse: a removal must also take effect immediately, or the gate could not be tightened at runtime.
        toolCapableModels.Clear();

        var afterRemoval = provider.GetOfferedTools("unsloth/gemma-4-12b-it-GGUF:Q5_K_M");
        AssertEx.False(afterRemoval.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "Removing a model from the allow-list must withhold the capable-only tools immediately.");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenAllowlistChangesAfterConstruction_TakesEffectWithoutARestart()
    {
        // The profile pool is the second decision point reading the same allow-list (the Coder agent's path, which is
        // where the live evaluation actually observed the failure). Both had to be repointed; this pins the second one.
        var toolCapableModels = new List<string>();
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetToolCapableModels().Returns(_ => toolCapableModels);

        var provider = new LocalToolOfferProvider(new FakeAgentToolRegistry([
                new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", "{\"type\":\"object\"}", RequiresApproval: true)
            ]),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            runtimeSettings,
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);

        AssertEx.False(provider.GetOfferedToolsForProfile("some-model").Any(tool => tool.Name == SpawnSubAgentToolDefinition.ToolName),
            "A non-capable model must not reach the profile-only spawn tool.");

        toolCapableModels.Add("some-model");

        AssertEx.Contains(provider.GetOfferedToolsForProfile("some-model"), tool => tool.Name == SpawnSubAgentToolDefinition.ToolName,
            "The profile pool reads the allow-list live too.");
    }

    [Test]
    public void GetOfferedTools_WhenActiveModelDiffersOnlyByCase_OmitsAgentHomeTool()
    {
        // The capability gate is intentionally an Ordinal (exact) match: a model id that differs only by case is NOT
        // tool-capable. This pins the matching contract so a future change cannot silently loosen it.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("QWEN3:8B");

        AssertEx.False(offered.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "the capability gate is an Ordinal exact match, so a case-only variant is not tool-capable");
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsNull_OmitsAgentHomeTool()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools(null);

        AssertEx.False(offered.Any(tool => tool.Name == AgentHomeToolDefinition.ToolName),
            "a null/unknown model is treated as not tool-capable, so the high-risk tool is withheld");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsToolCapable_DoesNotOfferSpawnSubAgentToDefaultPath()
    {
        // spawn_subagent is profile-opt-in only: the WHOLE offer (mode-off / Default-Assistant path) never carries it,
        // even on a tool-capable model. A plain chat turn must not be able to load another model.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.False(offered.Any(tool => tool.Name == "spawn_subagent"),
            "spawn_subagent must NOT be in the default/mode-off whole offer");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenModelIsToolCapable_IncludesSpawnSubAgentInProfilePool()
    {
        // The profile-intersection pool DOES include spawn_subagent, so a profile that lists it in AllowedToolNames on a
        // tool-capable model resolves it.
        var provider = CreateProvider("qwen3:8b");

        var pool = provider.GetOfferedToolsForProfile("qwen3:8b");

        AssertEx.Contains(pool, tool => tool.Name == "spawn_subagent");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenModelIsNotToolCapable_WithholdsSpawnSubAgentTool()
    {
        // Even in the profile pool, spawn_subagent stays capability-gated: a non-tool-capable model never gets it, so a
        // profile opt-in cannot bypass the capability gate.
        var provider = CreateProvider("qwen3:8b");

        var pool = provider.GetOfferedToolsForProfile("some-other-model");

        AssertEx.False(pool.Any(tool => tool.Name == "spawn_subagent"),
            "spawn_subagent must be withheld from a model that is not tool-capable, even in the profile pool");
    }

    [Test]
    public void GetKnownToolNames_IncludesSpawnSubAgentTool()
    {
        // The UI tool picker + CRUD validation must still list spawn_subagent so an operator can add it to a profile's
        // AllowedToolNames — its absence from the default offer is a runtime gating choice, not a catalog removal.
        var provider = CreateProvider("qwen3:8b");

        var names = provider.GetKnownToolNames();

        AssertEx.Contains(names, "spawn_subagent");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsToolCapable_DoesNotOfferRunPythonToTheDefaultPath()
    {
        // run_python is profile-opt-in only, for a sharper reason than spawn_subagent: it executes model-authored code
        // on the node. A default/mode-off chat turn must never carry a code-execution tool, capable model or not.
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.False(offered.Any(tool => tool.Name == ComputeToolDefinition.ToolName),
            "run_python must NOT be in the default/mode-off whole offer");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenModelIsToolCapable_IncludesRunPythonInProfilePool()
    {
        var provider = CreateProvider("qwen3:8b");

        var pool = provider.GetOfferedToolsForProfile("qwen3:8b");

        var compute = AssertEx.NotNull(pool.FirstOrDefault(tool => tool.Name == ComputeToolDefinition.ToolName));
        AssertEx.True(compute.RequiresApproval, "run_python must reach the model carrying its approval requirement");
        AssertEx.Equal(ToolCategory.WriteExecute, compute.Category);
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenModelIsNotToolCapable_WithholdsRunPython()
    {
        var provider = CreateProvider("qwen3:8b");

        var pool = provider.GetOfferedToolsForProfile("some-other-model");

        AssertEx.False(pool.Any(tool => tool.Name == ComputeToolDefinition.ToolName),
            "run_python must be withheld from a model that is not tool-capable, even in the profile pool");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenModelIsCloudHosted_WithholdsRunPythonAndSpawn()
    {
        // The run_python gate is NOT the knowledge/coder tools' content-leak rationale: what is withheld is a REMOTE
        // model's ability to direct code execution on the operator's machine. It is therefore unconditional, not behind
        // the AllowCloudModelAccess opt-in.
        //
        // spawn_subagent is withheld by the same gate, which this test previously asserted the OPPOSITE of. Delegation
        // reaches every tool the direct gates withhold: the child resolves its own model and its own tool set, so a
        // cloud parent could bind a child to a node-local model, have it read the workspace or the knowledge base, and
        // receive the result into its own transcript. An ungated spawn offer is a bypass of all three direct gates
        // rather than a capability of its own.
        var provider = CreateProvider("qwen3:8b");

        var pool = provider.GetOfferedToolsForProfile("qwen3:8b", isCloudModel: true);

        AssertEx.False(pool.Any(tool => tool.Name == ComputeToolDefinition.ToolName),
            "run_python must never be offered to a cloud-hosted model");
        AssertEx.False(pool.Any(tool => tool.Name == "spawn_subagent"),
            "spawn_subagent must never be offered to a cloud-hosted model");
    }

    [Test]
    public async Task GetOfferedToolsForProfileAsync_WhenModelIsCloudHosted_WithholdsRunPythonAndSpawn()
    {
        // The async pool is a separate code path (it folds in custom tools), so both gates are pinned on both paths or
        // one of them can drift open.
        var provider = CreateProvider("qwen3:8b");

        var pool = await provider.GetOfferedToolsForProfileAsync("qwen3:8b", isCloudModel: true);

        AssertEx.False(pool.Any(tool => tool.Name == ComputeToolDefinition.ToolName),
            "run_python must never be offered to a cloud-hosted model on the async pool either");
        AssertEx.False(pool.Any(tool => tool.Name == "spawn_subagent"),
            "spawn_subagent must never be offered to a cloud-hosted model on the async pool either");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenTheModelIsADeclaredCloudExternalEndpoint_WithholdsSpawn()
    {
        const string modelId = "ext:hosted-box/qwen3";
        var trustResolver = new FakeModelTrustResolver().Register("hosted-box", "qwen3", ExternalProviderLocality.Cloud);
        var provider = CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            allowCloudKnowledgeAccess: false,
            trustResolver,
            modelId);

        // The turn's own flag says local — this is an agent PINNED to an external id — so the declared locality is the
        // only thing that can catch it, exactly as it is for run_python.
        var pool = provider.GetOfferedToolsForProfile(modelId, isCloudModel: false);

        AssertEx.False(pool.Any(tool => tool.Name == "spawn_subagent"),
            "a declared-cloud external model must not be offered spawn_subagent");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenTheExternalRegistrationCannotBeResolved_WithholdsSpawn()
    {
        const string modelId = "ext:hosted-box/qwen3";
        var trustResolver = new FakeModelTrustResolver
        {
            CacheIsCold = true
        };
        var provider = CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            allowCloudKnowledgeAccess: false,
            trustResolver,
            modelId);

        // Unresolved is not "probably fine": a deleted connection, an unreadable store, or the pre-boot window all land
        // here, and only a positively resolved local declaration may earn delegation.
        var pool = provider.GetOfferedToolsForProfile(modelId, isCloudModel: false);

        AssertEx.False(pool.Any(tool => tool.Name == "spawn_subagent"),
            "an unresolved external model must not be offered spawn_subagent");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenTheModelIsADeclaredLocalExternalEndpoint_StillOffersSpawn()
    {
        const string modelId = "ext:unsloth-box/qwen3";
        var trustResolver = new FakeModelTrustResolver().Register("unsloth-box", "qwen3");
        var provider = CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            allowCloudKnowledgeAccess: false,
            trustResolver,
            modelId);

        // The gate is locality, not externality: full local parity is the whole point of the declared-Local flag.
        var pool = provider.GetOfferedToolsForProfile(modelId, isCloudModel: false);

        AssertEx.Contains(pool, tool => tool.Name == "spawn_subagent");
    }

    [Test]
    public async Task GetOfferedToolsForProfileAsync_WhenModelIsLocalAndCapable_IncludesRunPython()
    {
        var provider = CreateProvider("qwen3:8b");

        var pool = await provider.GetOfferedToolsForProfileAsync("qwen3:8b", isCloudModel: false);

        AssertEx.Contains(pool, tool => tool.Name == ComputeToolDefinition.ToolName);
    }

    [Test]
    public void GetKnownToolNames_IncludesRunPython()
    {
        // The UI tool picker + CRUD validation must list run_python, or an operator could not add it to a profile's
        // AllowedToolNames at all — which is the ONLY way the tool is ever offered.
        var provider = CreateProvider("qwen3:8b");

        AssertEx.Contains(provider.GetKnownToolNames(), ComputeToolDefinition.ToolName);
        AssertEx.Contains(provider.GetKnownTools(), entry => entry.Name == ComputeToolDefinition.ToolName);
    }

    [Test]
    public void GetOfferedTools_WhenModelIsToolCapable_IncludesSnapshottedMcpTools()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b");

        AssertEx.Contains(offered, tool => tool.Name == "mcp__weather__get_forecast");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsNotToolCapable_WithholdsMcpTools()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var offered = provider.GetOfferedTools("some-other-model");

        AssertEx.False(offered.Any(tool => tool.Name == "mcp__weather__get_forecast"),
            "MCP tools are capability-gated, so an incapable model is never offered them");
        AssertEx.Contains(offered, tool => tool.Name == "open_url");
    }

    [Test]
    public void GetKnownToolNames_IncludesBuiltinsAndSnapshottedMcpTools()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var names = provider.GetKnownToolNames();

        AssertEx.Contains(names, AgentHomeToolDefinition.ToolName);
        AssertEx.Contains(names, "open_url");
        AssertEx.Contains(names, "mcp__weather__get_forecast");
    }

    [Test]
    public void GetKnownTools_TagsBuiltinAndMcpSourcesAndIgnoresCapabilityGating()
    {
        var mcpRegistry = new McpToolRegistry(NullLogger<McpToolRegistry>.Instance);
        mcpRegistry.ReplaceSnapshot([BuildMcpTool("mcp__weather__get_forecast")]);
        var provider = CreateProvider(mcpRegistry, "qwen3:8b");

        var catalog = provider.GetKnownTools();

        var builtin = catalog.Single(entry => entry.Name == "open_url");
        AssertEx.Equal("builtin", builtin.Source);

        var agentHome = catalog.Single(entry => entry.Name == AgentHomeToolDefinition.ToolName);
        AssertEx.Equal("builtin", agentHome.Source);

        var mcp = catalog.Single(entry => entry.Name == "mcp__weather__get_forecast");
        AssertEx.Equal("mcp:weather", mcp.Source);
        AssertEx.True(mcp.RequiresApproval, "every MCP tool defaults to requiring approval");
        AssertEx.Equal("Gets the weather forecast.", mcp.Description);
    }

    [Test]
    public void GetKnownTools_WhenNoMcpServers_ReturnsBuiltinsOnly()
    {
        var provider = CreateProvider("qwen3:8b");

        var catalog = provider.GetKnownTools();

        AssertEx.True(catalog.All(entry => entry.Source == "builtin"),
            "with no MCP snapshot the catalog is built-ins only");
    }

    private const string KnowledgeSearchToolName = "search_knowledge_base";
    private const string CoderReadFileToolName = "read_file";

    [Test]
    public void GetOfferedTools_WhenModelIsLocal_OffersKnowledgeTools()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b", isCloudModel: false);

        AssertEx.Contains(offered, tool => tool.Name == KnowledgeSearchToolName);
    }

    [Test]
    public void GetOfferedTools_WhenModelIsCloud_WithholdsCoderFileToolsByDefault()
    {
        // RR3-4 Part C: the coder workspace file tools read node-local attachment/workspace content, so they are gated
        // off a cloud model by default alongside the knowledge tools.
        var provider = CreateProvider("qwen3:8b");

        var localOffer = provider.GetOfferedTools("qwen3:8b", isCloudModel: false);
        var cloudOffer = provider.GetOfferedTools("qwen3:8b", isCloudModel: true);

        AssertEx.Contains(localOffer, tool => tool.Name == CoderReadFileToolName);
        AssertEx.False(cloudOffer.Any(tool => tool.Name == CoderReadFileToolName),
            "a cloud model must not be offered the coder workspace file tools by default");
    }

    [Test]
    public void GetOfferedTools_WhenModelIsCloud_AndOperatorOptedIn_OffersCoderFileTools()
    {
        var provider = CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), allowCloudKnowledgeAccess: true, "qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b", isCloudModel: true);

        AssertEx.Contains(offered, tool => tool.Name == CoderReadFileToolName);
    }

    [Test]
    public void GetOfferedTools_WhenModelIsCloud_WithholdsKnowledgeToolsByDefault()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b", isCloudModel: true);

        AssertEx.False(offered.Any(tool => tool.Name == KnowledgeSearchToolName),
            "a cloud model must not be offered the knowledge tools by default (node-local content must not leave the node)");
        // The rest of the capable offer is unaffected — only the knowledge tools are gated off.
        AssertEx.Contains(offered, tool => tool.Name == AgentHomeToolDefinition.ToolName);
    }

    [Test]
    public void GetOfferedTools_WhenModelIsCloud_AndOperatorOptedIn_OffersKnowledgeTools()
    {
        var provider = CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), allowCloudKnowledgeAccess: true, "qwen3:8b");

        var offered = provider.GetOfferedTools("qwen3:8b", isCloudModel: true);

        AssertEx.Contains(offered, tool => tool.Name == KnowledgeSearchToolName);
    }

    [Test]
    public void GetOfferedTools_WhenModelIsCodex_WithholdsKnowledgeToolsEvenWithoutCloudFlag()
    {
        // A Codex id is detected as cloud synchronously, so the gate fires even if the caller passes isCloudModel: false
        // (e.g. a custom agent pinned to a Codex model while the turn's active model was local).
        var provider = CreateProvider("gpt-5.5");

        var offered = provider.GetOfferedTools("gpt-5.5", isCloudModel: false);

        AssertEx.False(offered.Any(tool => tool.Name == KnowledgeSearchToolName),
            "a Codex model must not be offered the knowledge tools regardless of the threaded cloud flag");
    }

    [Test]
    public void GetOfferedToolsForProfile_WhenModelIsCloud_WithholdsKnowledgeToolsByDefault()
    {
        var provider = CreateProvider("qwen3:8b");

        var offered = provider.GetOfferedToolsForProfile("qwen3:8b", isCloudModel: true);

        AssertEx.False(offered.Any(tool => tool.Name == KnowledgeSearchToolName),
            "the profile-intersection pool applies the same knowledge-tool locality gate");
    }

    [Test]
    public void GetOfferedTools_OffersAskUserToACapableModel_LocalOrCloud_AndWithholdsItFromANonCapableOne()
    {
        // ask_user sits on exactly ONE of the two offer gates.
        //   * Capability gate: ON. A model that is not tool-capable cannot call it, and an offered schema is not free —
        //     llama.cpp compiles the whole offered tools array into one GBNF grammar with a hard repetition ceiling, and
        //     this is the most deeply nested schema in the catalog (docs/agent-knowledge.md §3).
        //   * Locality gate: OFF. A tool-capable CLOUD model is still offered it: the payload is the model's own
        //     question, so the cloud-egress gate has nothing to withhold.
        var provider = CreateProvider("qwen3:8b");

        AssertEx.Contains(provider.GetOfferedTools("qwen3:8b"), tool => tool.Name == AskUserTool.ToolName,
            "a tool-capable LOCAL model must be offered ask_user");
        AssertEx.Contains(provider.GetOfferedTools("qwen3:8b", isCloudModel: true), tool => tool.Name == AskUserTool.ToolName,
            "a tool-capable CLOUD model must still be offered ask_user — no node-local content travels through it");
        AssertEx.False(provider.GetOfferedTools("some-other-model").Any(tool => tool.Name == AskUserTool.ToolName),
            "a model that is not tool-capable must NOT be offered ask_user");
        AssertEx.Contains(provider.GetOfferedToolsForProfile("qwen3:8b"), tool => tool.Name == AskUserTool.ToolName,
            "the profile-intersection pool must carry ask_user so a bound agent's projection can union it in");
        AssertEx.False(provider.GetOfferedToolsForProfile("some-other-model").Any(tool => tool.Name == AskUserTool.ToolName),
            "the profile pool applies the same capability gate, so an opt-in cannot bypass it");
    }

    [Test]
    public void ProductionCatalog_WhenModelIsNotToolCapable_OffersNothingBeyondTheUngatedArithmeticBuiltins()
    {
        // The point of gating ask_user: a non-tool-capable model must not be handed a schema it cannot drive. Against the
        // REAL catalog the non-capable offer collapses to the two ungated LocalAgentToolRegistry builtins (the clock and
        // arithmetic tools docs/agent-knowledge.md §3 records as the partial-failure residue) — every gated tool,
        // ask_user included, is withheld.
        var provider = new LocalToolOfferProvider(new LocalAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels("qwen3:8b").Build(),
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);

        var offered = provider.GetOfferedTools("some-other-model");

        AssertEx.Equal("Calculate,GetCurrentTime",
            string.Join(',', offered.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal)));
    }

    [Test]
    public void GetOfferedTools_AskUser_IsApprovalGatedReadLocalAndCarriesTheSharedSchema()
    {
        // RequiresApproval is STRUCTURAL here, not a risk judgement: it is what makes the runner surface the call as an
        // approval request and do the human wait OUTSIDE the stream-idle watchdog. A handler that simply blocked would
        // trip StreamIdleTimeout. The schema/description come from the single AskUserTool source, so the offered
        // contract cannot drift from what the handler validates.
        var provider = CreateProvider("qwen3:8b");

        var askUser = provider.GetOfferedTools("qwen3:8b").Single(tool => tool.Name == AskUserTool.ToolName);

        AssertEx.True(askUser.RequiresApproval, "ask_user must be approval-gated — that is what routes it to the human round-trip");
        AssertEx.Equal(ToolCategory.ReadLocal, askUser.Category);
        AssertEx.Equal(AskUserTool.ParameterSchema, askUser.ParameterSchema);
    }

    [Test]
    public void GetKnownTools_IncludesAskUserWithItsSharedDescription()
    {
        // The catalog surface (agent form + CRUD name validation) must know ask_user, so an operator who lists it in an
        // agent's AllowedToolNames is not warned about an unknown tool.
        var provider = CreateProvider("qwen3:8b");

        AssertEx.Contains(provider.GetKnownToolNames(), AskUserTool.ToolName);
        var entry = provider.GetKnownTools().Single(candidate => candidate.Name == AskUserTool.ToolName);
        AssertEx.Equal("builtin", entry.Source);
        AssertEx.Equal(AskUserTool.Description, entry.Description);
        AssertEx.True(entry.RequiresApproval);
    }

    private static McpRegisteredTool BuildMcpTool(string qualifiedName)
    {
        var executable = AIFunctionFactory.Create((string input) => input, qualifiedName);
        var descriptor = new LocalChatToolDescriptor(qualifiedName, "Gets the weather forecast.", ParameterSchema: """{"type":"object"}""", RequiresApproval: true);
        return new McpRegisteredTool(qualifiedName, executable, descriptor);
    }

    [Test]
    public void ProductionCatalog_EveryOfferedTool_DeclaresANonUnknownCategory()
    {
        // Fail-closed guard: every tool the REAL catalog offers must declare a concrete ToolCategory. An
        // accidental Unknown would make the node approval policy treat that tool as fail-closed (approval-requiring), so
        // catch a missing category here rather than in production. Uses the real LocalAgentToolRegistry (builtin clock /
        // arithmetic) plus the merged coder + knowledge tools and the profile-only spawn_subagent.
        var provider = new LocalToolOfferProvider(new LocalAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels("qwen3:8b").Build(),
            NullCustomToolScopeFactory.Instance,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);

        var offered = provider.GetOfferedToolsForProfile("qwen3:8b");

        AssertEx.True(offered.Count > 0, "The tool-capable profile pool must offer at least the built-in tools.");
        AssertEx.False(offered.Any(tool => tool.Category == ToolCategory.Unknown),
            "Every built-in offered tool must declare a concrete (non-Unknown) ToolCategory.");
        // Spot-check the specific classifications the taxonomy pins (reliable const tool names).
        AssertEx.Equal(ToolCategory.Orchestration, offered.Single(tool => tool.Name == "spawn_subagent").Category);
        AssertEx.Equal(ToolCategory.ReadLocal, offered.Single(tool => tool.Name == "list_files").Category);
        AssertEx.Equal(ToolCategory.ReadLocal, offered.Single(tool => tool.Name == "search_knowledge_base").Category);
        // ask_user reads an answer from the node-local operator and has no side effect of its own.
        AssertEx.Equal(ToolCategory.ReadLocal, offered.Single(tool => tool.Name == AskUserTool.ToolName).Category);
    }

    [Test]
    public async Task GetOfferedToolsAsync_WhenModelIsLocalCapableAndKillSwitchOn_IncludesCustomToolAsClientLocalApprovalForced()
    {
        var provider = CreateProviderWithCustomTools(customToolsEnabled: true, CustomWeatherDescriptor);

        var offered = await provider.GetOfferedToolsAsync("qwen3:8b", isCloudModel: false);

        var custom = AssertEx.NotNull(offered.SingleOrDefault(tool => tool.Name == "custom__weather"));
        AssertEx.True(custom.RequiresApproval, "a custom tool is always offered approval-forced");
        AssertEx.Equal(ToolLocation.ClientLocal, custom.Location);
        AssertEx.Equal(ToolCategory.Network, custom.Category);
    }

    [Test]
    public async Task GetOfferedToolsAsync_WhenModelIsCloud_OmitsCustomTool()
    {
        var provider = CreateProviderWithCustomTools(customToolsEnabled: true, CustomWeatherDescriptor);

        var offered = await provider.GetOfferedToolsAsync("qwen3:8b", isCloudModel: true);

        AssertEx.False(offered.Any(tool => tool.Name == "custom__weather"),
            "custom tools are node-local-only and must never be offered to a cloud model");
    }

    [Test]
    public async Task GetOfferedToolsAsync_WhenKillSwitchOff_OmitsCustomTool()
    {
        var provider = CreateProviderWithCustomTools(customToolsEnabled: false, CustomWeatherDescriptor);

        var offered = await provider.GetOfferedToolsAsync("qwen3:8b", isCloudModel: false);

        AssertEx.False(offered.Any(tool => tool.Name == "custom__weather"),
            "with the node kill-switch off, no custom tool may be offered");
    }

    [Test]
    public async Task GetKnownToolsAsync_TagsCustomToolWithCustomSource_UngatedByKillSwitch()
    {
        // Known-tools are ungated by the kill-switch, since an authored tool exists on the node whether or not it is
        // switched on. The agent form and CRUD collision therefore see it, tagged with the custom source for the badge.
        var provider = CreateProviderWithCustomTools(customToolsEnabled: false, CustomWeatherDescriptor);

        var known = await provider.GetKnownToolsAsync();

        var entry = AssertEx.NotNull(known.SingleOrDefault(tool => tool.Name == "custom__weather"));
        AssertEx.Equal("custom", entry.Source);
    }

    private static readonly LocalChatToolDescriptor CustomWeatherDescriptor =
        new("custom__weather", "Fetches weather.", "{\"type\":\"object\"}", RequiresApproval: true, ToolCategory.Network);

    private static LocalToolOfferProvider CreateProviderWithCustomTools(bool customToolsEnabled, params LocalChatToolDescriptor[] customDescriptors)
    {
        var registry = new FakeAgentToolRegistry([
            new LocalChatToolDescriptor("open_url", "Opens a URL.", "{\"type\":\"object\"}", RequiresApproval: false)
        ]);

        var scopeFactory = new ServiceCollection()
                           .AddSingleton<ICustomToolCatalog>(new StubCustomToolCatalog(customDescriptors))
                           .BuildServiceProvider()
                           .GetRequiredService<IServiceScopeFactory>();

        return new LocalToolOfferProvider(registry,
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels("qwen3:8b").WithCustomToolsEnabled(customToolsEnabled).Build(),
            scopeFactory,
            new FakeModelTrustResolver(),
            allowCloudKnowledgeAccess: false);
    }

    private sealed class StubCustomToolCatalog(IReadOnlyList<LocalChatToolDescriptor> descriptors) : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(descriptors);
        }

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, AITool>>(ReadOnlyDictionary<string, AITool>.Empty);
        }
    }

    private static LocalToolOfferProvider CreateProvider(params string[] toolCapableModels)
    {
        return CreateProvider(new McpToolRegistry(NullLogger<McpToolRegistry>.Instance), allowCloudKnowledgeAccess: false, toolCapableModels);
    }

    private static LocalToolOfferProvider CreateProvider(IMcpToolRegistry mcpToolRegistry, params string[] toolCapableModels)
    {
        return CreateProvider(mcpToolRegistry, allowCloudKnowledgeAccess: false, toolCapableModels);
    }

    private static LocalToolOfferProvider CreateProvider(IMcpToolRegistry mcpToolRegistry, bool allowCloudKnowledgeAccess, params string[] toolCapableModels)
    {
        return CreateProvider(mcpToolRegistry, allowCloudKnowledgeAccess, new FakeModelTrustResolver(), toolCapableModels);
    }

    private static LocalToolOfferProvider CreateProvider(IMcpToolRegistry mcpToolRegistry,
        bool allowCloudKnowledgeAccess,
        FakeModelTrustResolver trustResolver,
        params string[] toolCapableModels)
    {
        var registry = new FakeAgentToolRegistry([
            new LocalChatToolDescriptor(AgentHomeToolDefinition.ToolName, "Runs an agent task.", "{\"type\":\"object\"}", RequiresApproval: true),
            new LocalChatToolDescriptor("open_url", "Opens a URL.", "{\"type\":\"object\"}", RequiresApproval: false)
        ]);

        return new LocalToolOfferProvider(registry,
            mcpToolRegistry,
            StubNodeRuntimeSettings.Create().WithToolCapableModels(toolCapableModels).Build(),
            NullCustomToolScopeFactory.Instance,
            trustResolver,
            allowCloudKnowledgeAccess);
    }

    private sealed class FakeAgentToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<LocalChatToolDescriptor> _descriptors;

        public FakeAgentToolRegistry(IReadOnlyList<LocalChatToolDescriptor> descriptors)
        {
            _descriptors = descriptors;
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return [];
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return _descriptors;
        }
    }
}
