namespace XE_Local_AI_Engine.Tests.Mcp;

using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Instructions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class McpExecutionBindingResolverTests
{
    private const string Model = "unsloth/Ornith-1.0-9B-GGUF:Q4_K_M";

    [Test]
    public async Task ResolveAsync_WhenBareModelIsAvailable_ReturnsToolLessBinding()
    {
        var harness = new Harness();

        var result = await harness.Resolver.ResolveAsync(new McpExecutionBindingRequest
        {
            ModelId = Model,
            Instructions = "Answer from the supplied task only."
        }, CancellationToken.None);

        var binding = AssertEx.NotNull(result.Binding);
        AssertEx.Empty(binding.AllowedTools);
    }

    [Test]
    public async Task ResolveAsync_WhenGeneralAgentResolvesToolsAndSkills_ReturnsToolLessBinding()
    {
        var harness = new Harness();
        var definition = Definition("General", AgentDefinitionSource.Manual, seedSlug: null);
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal),
            Tool("load_skill", ToolCategory.ReadLocal),
            Tool("read_skill_resource", ToolCategory.ReadLocal),
            Tool("run_skill_script", ToolCategory.WriteExecute),
            Tool("context_lookup", ToolCategory.ReadLocal));

        var result = await harness.Resolver.ResolveAsync(new McpExecutionBindingRequest
        {
            AgentKey = definition.Id.ToString()
        }, CancellationToken.None);

        var binding = AssertEx.NotNull(result.Binding);
        AssertEx.Empty(binding.AllowedTools);
    }

    [Test]
    public async Task ResolveAsync_WhenAgentOnlyUsesCoderDisplayName_ReturnsToolLessBinding()
    {
        var harness = new Harness();
        var definition = Definition(AgentDefaults.CoderAgentName, AgentDefinitionSource.Manual, seedSlug: null);
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal));

        var result = await harness.Resolver.ResolveAsync(new McpExecutionBindingRequest
        {
            AgentKey = definition.Id.ToString()
        }, CancellationToken.None);

        var binding = AssertEx.NotNull(result.Binding);
        AssertEx.Empty(binding.AllowedTools);
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderUsesExactSafeOfferAndModelOverride_ReturnsThreeToolBinding()
    {
        var harness = new Harness();
        var definition = Definition(AgentDefaults.CoderAgentName,
            AgentDefinitionSource.Seeded,
            AgentDefaults.CoderAgentSeedSlug,
            modelProfile: null);
        var expected = new[]
        {
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal)
        };
        harness.Register(definition,
            expected);

        var result = await harness.Resolver.ResolveAsync(new McpExecutionBindingRequest
        {
            AgentKey = definition.Id.ToString(),
            ModelOverrideId = Model
        }, CancellationToken.None);

        var binding = AssertEx.NotNull(result.Binding);
        AssertEx.Equal(3, binding.AllowedTools.Count);
        AssertEx.True(binding.AllowedTools.Select(static tool => tool.Name)
                             .SequenceEqual(["list_files", "read_file", "search_text"], StringComparer.Ordinal),
            "the MCP Coder binding must expose the exact ordinal three-tool allow-list.");
        AssertEx.True(binding.AllowedTools.All(static tool => tool.Category == ToolCategory.ReadLocal
                                                              && tool.Location == ToolLocation.ClientLocal
                                                              && !tool.RequiresApproval),
            "every retained Coder tool must be local, read-only, and unattended-safe.");
        AssertEx.True(binding.AllowedTools.Select(static tool => tool.Id)
                             .SequenceEqual(expected.Select(static tool => tool.Id)),
            "the canonical Coder projection must retain the three resolved tool identities.");
        AssertEx.Equal(Model, binding.ModelId);
    }

    [Test]
    public async Task ResolveAsync_WhenProductionCoderOfferIncludesApprovalRequiredAskUser_ReturnsExactlyThreeCoderTools()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal),
            Tool(AskUserTool.ToolName,
                ToolCategory.ReadLocal,
                requiresApproval: true,
                description: AskUserTool.Description,
                parameterSchema: AskUserTool.ParameterSchema));

        var result = await ResolveCoderAsync(harness, definition);

        var binding = AssertEx.NotNull(result.Binding);
        AssertEx.True(binding.AllowedTools.Select(static tool => tool.Name)
                             .SequenceEqual(["list_files", "read_file", "search_text"], StringComparer.Ordinal),
            "the production ask_user offer must be ignored at the unattended MCP boundary.");
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderHasUnrelatedMixedRiskOffers_ReturnsExactlyThreeCoderTools()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal),
            Tool("unknown_tool", ToolCategory.Unknown),
            Tool("network_tool", ToolCategory.Network),
            Tool("orchestration_tool", ToolCategory.Orchestration),
            Tool("write_tool", ToolCategory.WriteExecute),
            Tool("approval_tool", ToolCategory.ReadLocal, requiresApproval: true),
            Tool("load_skill", ToolCategory.ReadLocal),
            Tool("read_skill_resource", ToolCategory.ReadLocal),
            Tool("run_skill_script", ToolCategory.WriteExecute));

        var result = await ResolveCoderAsync(harness, definition);

        var binding = AssertEx.NotNull(result.Binding);
        AssertEx.True(binding.AllowedTools.Select(static tool => tool.Name)
                             .SequenceEqual(["list_files", "read_file", "search_text"], StringComparer.Ordinal),
            "unrelated tools must be ignored rather than exposed or allowed to invalidate the safe Coder binding.");
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderIsMissingAllowedTool_RejectsBinding()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal));

        var result = await ResolveCoderAsync(harness, definition);

        AssertEx.Null(result.Binding);
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderDuplicatesAllowedTool_RejectsBinding()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal));

        var result = await ResolveCoderAsync(harness, definition);

        AssertEx.Null(result.Binding);
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderToolIsMisclassified_RejectsBinding()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.Network),
            Tool("search_text", ToolCategory.ReadLocal));

        var result = await ResolveCoderAsync(harness, definition);

        AssertEx.Null(result.Binding);
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderToolIsNotClientLocal_RejectsBinding()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        var nonLocal = Tool("read_file", ToolCategory.ReadLocal) with
        {
            Location = ToolLocation.ApiSide
        };
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            nonLocal,
            Tool("search_text", ToolCategory.ReadLocal));

        var result = await ResolveCoderAsync(harness, definition);

        AssertEx.Null(result.Binding);
    }

    [Test]
    public async Task ResolveAsync_WhenSeededCoderToolRequiresApproval_RejectsBinding()
    {
        var harness = new Harness();
        var definition = UnboundSeededCoder();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal),
            Tool("read_file", ToolCategory.ReadLocal, requiresApproval: true),
            Tool("search_text", ToolCategory.ReadLocal));

        var result = await ResolveCoderAsync(harness, definition);

        AssertEx.Null(result.Binding);
    }

    [Test]
    public async Task ResolveAsync_WhenPinnedAgentReceivesModelOverride_RejectsOverride()
    {
        var harness = new Harness();
        var definition = Definition("General", AgentDefinitionSource.Manual, seedSlug: null);
        harness.Register(definition);

        var result = await harness.Resolver.ResolveAsync(new McpExecutionBindingRequest
        {
            AgentKey = definition.Id.ToString(),
            ModelOverrideId = "another/model:Q4_K_M"
        }, CancellationToken.None);

        AssertEx.Equal(McpExecutionFailureCodes.ModelOverrideNotAllowed, result.FailureCode);
        AssertEx.Null(result.Binding);
    }

    [Test]
    public async Task ResolveAsync_WhenCoderToolsAreProjected_UsesCanonicalDescriptionsAndSchemas()
    {
        var definition = UnboundSeededCoder();
        var harness = new Harness();
        harness.Register(definition,
            Tool("list_files", ToolCategory.ReadLocal, description: "untrusted description", parameterSchema: "{}"),
            Tool("read_file", ToolCategory.ReadLocal),
            Tool("search_text", ToolCategory.ReadLocal));

        var binding = AssertEx.NotNull((await ResolveCoderAsync(harness, definition)).Binding);
        var listFiles = AssertEx.NotNull(binding.AllowedTools.SingleOrDefault(static tool => tool.Name == "list_files"));
        var canonical = CoderToolDefinition.Descriptors.Single(static descriptor => descriptor.Name == "list_files");

        AssertEx.Equal(canonical.Description, listFiles.Description);
        AssertEx.Equal(canonical.ParameterSchema, listFiles.ParameterSchema);
    }

    private static Task<McpExecutionBindingResolution> ResolveCoderAsync(Harness harness, AgentDefinitionRecord definition)
    {
        return harness.Resolver.ResolveAsync(new McpExecutionBindingRequest
        {
            AgentKey = definition.Id.ToString(),
            ModelOverrideId = Model
        }, CancellationToken.None);
    }

    private static AgentDefinitionRecord UnboundSeededCoder()
    {
        return Definition(AgentDefaults.CoderAgentName,
            AgentDefinitionSource.Seeded,
            AgentDefaults.CoderAgentSeedSlug,
            modelProfile: null);
    }

    private static AgentDefinitionRecord Definition(string name,
        AgentDefinitionSource source,
        string? seedSlug,
        string? modelProfile = Model)
    {
        return new AgentDefinitionRecord(Guid.NewGuid(),
            name,
            Description: null,
            Instructions: "saved instructions",
            ModelProfile: modelProfile,
            ReasoningEffort: null,
            Kind: AgentDefinitionKind.Single,
            AllowedToolNames: [],
            ToolApprovals: new Dictionary<string, bool>(StringComparer.Ordinal),
            OrchestrationTopologyJson: null,
            Version: 7,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            Source: source,
            SeedSlug: seedSlug,
            AllowedSkillIds: [Guid.NewGuid()]);
    }

    private static AllowedToolDto Tool(string name,
        ToolCategory category,
        bool requiresApproval = false,
        string? description = null,
        string parameterSchema = "{\"type\":\"object\"}")
    {
        return new AllowedToolDto
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = ToolLocation.ClientLocal,
            Description = description ?? $"Test offer for {name}.",
            ParameterSchema = parameterSchema,
            RequiresApproval = requiresApproval,
            Category = category
        };
    }

    private sealed class Harness
    {
        private readonly IAgentDefinitionResolver _agentResolver = Substitute.For<IAgentDefinitionResolver>();
        private readonly IAgentDefinitionStore _definitions = Substitute.For<IAgentDefinitionStore>();
        private readonly IGgufModelStore _models = Substitute.For<IGgufModelStore>();

        public Harness()
        {
            _models.ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            IAgentInstructionProvider instructions = new FakeAgentInstructionProvider();
            var capabilities = Substitute.For<IModelCapabilityResolver>();
            capabilities.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((SupportsThinking: true, SupportsTools: true, IsCloud: false));
            var nodeKey = Substitute.For<INodeSqliteKeyHolder>();
            nodeKey.Key.Returns(new ReadOnlyMemory<byte>(Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray()));
            Resolver = new McpExecutionBindingResolver(_definitions, _agentResolver, _models, instructions, capabilities, nodeKey);
        }

        public McpExecutionBindingResolver Resolver { get; }

        public void Register(AgentDefinitionRecord definition, params AllowedToolDto[] tools)
        {
            _definitions.GetByIdAsync(definition.Id, Arg.Any<CancellationToken>()).Returns(definition);
            var effectiveModel = definition.ModelProfile ?? Model;
            _agentResolver.ResolveAsync(definition.Id,
                              effectiveModel,
                              Arg.Any<string?>(),
                              Arg.Any<bool>(),
                              Arg.Any<bool>(),
                              Arg.Any<bool>(),
                              Arg.Any<CancellationToken>())
                          .Returns(new ResolvedAgentRuntime("resolved instructions",
                              tools,
                              definition.ModelProfile,
                              ReasoningEffort: null,
                              definition.Version,
                              definition.Id,
                              definition.Name,
                              Skills: [new ResolvedSkill(Guid.NewGuid(), "repo-context", "Repository context", "skill body", 1)]));
        }
    }

    private sealed class FakeAgentInstructionProvider : IAgentInstructionProvider
    {
        public string GetLocalChatInstructions()
        {
            return "local chat instructions";
        }

        public string GetBaseScaffold()
        {
            return "base scaffold";
        }

        public string GetDefaultChatSystemPrompt()
        {
            return "base scaffold\n\ndefault chat instructions";
        }
    }
}
