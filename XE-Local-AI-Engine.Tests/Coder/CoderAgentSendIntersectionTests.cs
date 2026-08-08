namespace XE_Local_AI_Engine.Tests.Coder;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Coder.Tools;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     The intersection gate. End-to-end across the REAL <see cref="LocalToolOfferProvider" /> (real
///     <see cref="LocalAgentToolRegistry" /> + the coder merge) and the REAL
///     <see cref="AgentDefinitionResolver" />: the seeded Coder agent's tool set is the non-empty intersection
///     <c>offered ∩ AllowedToolNames</c> for a capable model, and empty for an incapable one. If the merge regresses,
///     the intersection collapses to ∅ and this gate fails — proving the feature is wired, not merely resolvable.
/// </summary>
public sealed class CoderAgentSendIntersectionTests
{
    private const string CapableModel = "qwen3:8b";
    private const string IncapableModel = "tiny:1b";

    private static readonly string[] CoderToolNames =
    [
        CoderToolDefinition.ListFilesToolName,
        CoderToolDefinition.ReadFileToolName,
        CoderToolDefinition.SearchTextToolName
    ];

    [Test]
    public async Task AgentSend_Intersection_KeepsCoderTools()
    {
        var resolver = CreateResolver(out var store, CapableModel);
        var coder = SeededCoderDefinition();
        store.GetByIdAsync(coder.Id, Arg.Any<CancellationToken>()).Returns(coder);

        var resolved = await resolver.ResolveAsync(coder.Id, CapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        // The three coder tools plus ask_user, which is unioned into EVERY tool-enabled projection regardless of
        // AllowedToolNames (the Coder seed does not list it).
        AssertEx.Equal(expected: 4, resolved!.AllowedTools.Count);
        foreach (var toolName in CoderToolNames)
        {
            AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == toolName);
        }

        AssertEx.Contains(resolved.AllowedTools, tool => tool.Name == AskUserTool.ToolName);

        // Decision 7: the resolved coder tools carry the seed's ToolApprovals (all false), so none is approval-gated.
        // (ask_user is excluded: its approval flag is structural — it is what routes the call to the human round-trip.)
        AssertEx.True(resolved.AllowedTools.Where(tool => tool.Name != AskUserTool.ToolName).All(tool => !tool.RequiresApproval),
            "the seeded coder tools must resolve as auto-execute (ToolApprovals all false)");
    }

    [Test]
    public async Task AgentSend_Intersection_DropsCoderToolsForIncapableModel()
    {
        var resolver = CreateResolver(out var store, CapableModel);
        var coder = SeededCoderDefinition();
        store.GetByIdAsync(coder.Id, Arg.Any<CancellationToken>()).Returns(coder);

        var resolved = await resolver.ResolveAsync(coder.Id, IncapableModel).ConfigureAwait(false);

        AssertEx.NotNull(resolved);
        // The coder tools are capability-gated, so the intersection collapses to ∅. ask_user is capability-gated too, so
        // it does not survive either: AskUserToolOffer.EnsureOffered lifts the descriptor from the OFFER rather than
        // fabricating one, and this model was never offered it.
        foreach (var toolName in CoderToolNames)
        {
            AssertEx.False(resolved!.AllowedTools.Any(tool => tool.Name == toolName),
                $"'{toolName}' is capability-gated and must be withheld from a model outside ToolCapableModels");
        }

        AssertEx.Equal(expected: 0, resolved!.AllowedTools.Count);
    }

    private static AgentDefinitionRecord SeededCoderDefinition()
    {
        IReadOnlyList<string> allowed =
        [
            CoderToolDefinition.ListFilesToolName,
            CoderToolDefinition.ReadFileToolName,
            CoderToolDefinition.SearchTextToolName
        ];
        var approvals = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [CoderToolDefinition.ListFilesToolName] = false,
            [CoderToolDefinition.ReadFileToolName] = false,
            [CoderToolDefinition.SearchTextToolName] = false
        };

        return new AgentDefinitionRecord(Guid.NewGuid(),
            AgentDefaults.CoderAgentName,
            Description: "Read-only project access.",
            Instructions: "You are a read-only coding agent.",
            ModelProfile: null,
            ReasoningEffort: null,
            AgentDefinitionKind.Single,
            allowed,
            approvals,
            OrchestrationTopologyJson: null,
            Version: 1,
            CreatedAtUtc: 1,
            UpdatedAtUtc: 1,
            PlaybookEnabled: false,
            Source: AgentDefinitionSource.Seeded,
            SeedSlug: AgentDefaults.CoderAgentSeedSlug);
    }

    private static AgentDefinitionResolver CreateResolver(out IAgentDefinitionStore store, string capableModel)
    {
        // The REAL offer provider over the REAL registry + the merge — coder tools reach the offer only via the
        // merge, never via the registry.
        var offerProvider = new LocalToolOfferProvider(new LocalAgentToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            StubNodeRuntimeSettings.Create().WithToolCapableModels(capableModel).Build(),
            NullCustomToolScopeFactory.Instance,
            allowCloudKnowledgeAccess: false);

        store = Substitute.For<IAgentDefinitionStore>();
        var playbookStore = Substitute.For<IPlaybookActionStore>();
        playbookStore.ListEnabledByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult<IReadOnlyList<PlaybookActionRecord>>([]));
        var skillStore = Substitute.For<IAgentSkillStore>();
        skillStore.ListEnabledByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IReadOnlyList<AgentSkillRecord>>([]));

        return new AgentDefinitionResolver(store,
            playbookStore,
            skillStore,
            Substitute.For<ICustomToolStore>(),
            offerProvider,
            new LexicalPlaybookRetrievalRanker(),
            Options.Create(new PlaybookRetrievalOptions()),
            new FakeAgentInstructionProvider(),
            Substitute.For<IModelCapabilityResolver>(),
            new PermissiveToolApprovalPolicy(),
            NullLogger<AgentDefinitionResolver>.Instance);
    }
}
