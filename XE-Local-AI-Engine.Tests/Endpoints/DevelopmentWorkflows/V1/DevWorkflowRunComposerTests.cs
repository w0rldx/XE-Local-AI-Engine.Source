namespace XE_Local_AI_Engine.Tests.Endpoints.DevelopmentWorkflows.V1;

using NSubstitute;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The run composer directly, over substituted stores wrapped in the REAL application services it now injects —
///     <see cref="DevWorkflowRunQueryService" /> and <see cref="DevWorkflowAuthoringService" /> are sealed
///     pass-throughs, so wrapping rather than substituting them keeps each case's arrange on the store call the
///     composer actually causes.
///     <para>
///         Complementary to <see cref="DevWorkflowRunEndpointTests" />, which drives the same two methods over HTTP:
///         these cases exist for the multi-row reads the wire shape does not make obvious — the definition-name
///         lookup, the staleness join, and the node drill-down's consumed-artifact and decision feeds.
///     </para>
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DevWorkflowRunComposerTests
{
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid DefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ResearchNodeRunId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid GateNodeRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ArtifactId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid SessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ConversationId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid AgentId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    private const string GraphJson =
        """
        {"schemaVersion":1,"nodes":[{"nodeKey":"research","nodeType":"Agent","label":"Research"},
        {"nodeKey":"approval","nodeType":"HumanGate","label":"Approve"}],
        "edges":[{"from":"research","to":"approval"}]}
        """;

    [Test]
    public async Task ComposeAsync_NamesTheDefinitionBindsTheAgentAndRollsUpTheRunsSpend()
    {
        var store = Substitute.For<IDevWorkflowStore>();
        store.ListDefinitionsAsync(includeArchived: true, Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<DevWorkflowDefinitionSummary>>([Definition(DefinitionId, "Ship it"), Definition(Guid.NewGuid(), "Something else")]);
        var agents = Substitute.For<IAgentDefinitionService>();
        agents.ListAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<AgentDefinitionRecord>>([Agent()]);
        var composer = Composer(store, agents);

        var response = await composer.ComposeAsync(Detail([
            NodeRun(ResearchNodeRunId, "research", DevWorkflowNodeType.Agent, DevWorkflowNodeRunStatus.Running) with
            {
                AgentDefinitionId = AgentId,
                InputTokens = 30,
                OutputTokens = 12,
                ToolCalls = 2
            },
            NodeRun(GateNodeRunId, "approval", DevWorkflowNodeType.HumanGate, DevWorkflowNodeRunStatus.Pending) with
            {
                InputTokens = 5
            }
        ]), CancellationToken.None);

        // The name comes from the definition LIST, which is read including archived rows: a run whose definition was
        // archived after it started must still say what it ran.
        AssertEx.Equal("Ship it", response.DefinitionName);
        AssertEx.Equal(expected: 2, response.Nodes.Count);
        AssertEx.Equal(expected: 1, response.RunningNodeCount);
        AssertEx.Equal(expected: 0, response.QueuedNodeCount);
        AssertEx.Equal("Researcher", response.Nodes[0].AgentDisplayName);
        AssertEx.Equal("Approve", response.Nodes[1].Label, "the label comes off the run's pinned graph, not off the node key.");
        AssertEx.Equal(expected: 35L, response.Cost.InputTokens!.Value, "the rollup is summed over the node runs already loaded.");
        AssertEx.Equal(expected: 12L, response.Cost.OutputTokens!.Value);
        AssertEx.Equal(expected: 2, response.Cost.ToolCalls!.Value);
        AssertEx.Null(response.Cost.ProviderCalls, "a member nobody measured stays null rather than reading zero.");
    }

    /// <summary>
    ///     The staleness join: one artifact read for the run, then a consumed-ids read per node run — and only once a
    ///     stale artifact actually exists. Both feeds now arrive through the query service.
    /// </summary>
    [Test]
    public async Task ComposeAsync_FlagsOnlyTheNodeRunsThatConsumedASupersededArtifact()
    {
        var store = Substitute.For<IDevWorkflowStore>();
        store.ListArtifactsAsync(RunId, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<DevWorkflowArtifactSnapshot>>([Artifact(ArtifactId, isStale: true)]);
        store.ListConsumedArtifactIdsAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([ArtifactId]);
        store.ListConsumedArtifactIdsAsync(ResearchNodeRunId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([]);
        var composer = Composer(store);

        var response = await composer.ComposeAsync(Detail([
            NodeRun(ResearchNodeRunId, "research", DevWorkflowNodeType.Agent, DevWorkflowNodeRunStatus.Succeeded),
            NodeRun(GateNodeRunId, "approval", DevWorkflowNodeType.HumanGate, DevWorkflowNodeRunStatus.Pending)
        ]), CancellationToken.None);

        AssertEx.False(response.Nodes[0].HasStaleInputs, "it consumed nothing that was superseded.");
        AssertEx.True(response.Nodes[1].HasStaleInputs, "it consumed the artifact that has since been superseded.");
    }

    /// <summary>
    ///     The three reads this composer gained an application-layer door for: the node-run row, its consumed
    ///     artifacts, and the run's decision log filtered to the node being drawn. The conversation id is read from
    ///     the work-session family on the loose id, never stored on the node run.
    /// </summary>
    [Test]
    public async Task ComposeNodeAsync_ReadsTheNodeRunItsConsumedArtifactsItsDecisionsAndItsConversation()
    {
        var store = Substitute.For<IDevWorkflowStore>();
        store.GetRunAsync(RunId, Arg.Any<CancellationToken>()).Returns(Run());
        store.GetNodeRunAsync(ResearchNodeRunId, Arg.Any<CancellationToken>())
             .Returns(NodeRun(ResearchNodeRunId, "research", DevWorkflowNodeType.Agent, DevWorkflowNodeRunStatus.Succeeded) with
             {
                 WorkSessionId = SessionId,
                 WorkSessionAvailable = true
             });
        store.ListArtifactsAsync(RunId, Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<DevWorkflowArtifactSnapshot>>([Artifact(ArtifactId, isStale: false)]);
        store.ListConsumedArtifactIdsAsync(ResearchNodeRunId, Arg.Any<CancellationToken>()).Returns<IReadOnlyList<Guid>>([ArtifactId]);
        store.ListDecisionsAsync(RunId, Arg.Any<CancellationToken>())
             .Returns<IReadOnlyList<DevWorkflowDecisionSnapshot>>([Decision(ResearchNodeRunId, sequence: 4), Decision(GateNodeRunId, sequence: 9)]);
        var sessions = Substitute.For<IWorkSessionService>();
        sessions.GetAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Session());
        var composer = Composer(store, sessions: sessions);

        var response = await composer.ComposeNodeAsync(RunId, ResearchNodeRunId, CancellationToken.None);

        AssertEx.Equal(ResearchNodeRunId, response.Id);
        AssertEx.Equal(ArtifactId, response.ProducedArtifactIds[0], "the artifact feed is filtered to what this node produced.");
        AssertEx.Equal(ArtifactId, response.ConsumedArtifactIds[0]);
        AssertEx.Equal(expected: 1, response.Decisions.Count, "the run's decision log is filtered to this node run.");
        AssertEx.Equal(ConversationId, response.ConversationId!.Value);
    }

    /// <summary>
    ///     A node run read through another run's route reads as ABSENT, so one run's URL can never surface another's
    ///     rows. The row exists and is fetched by id alone, which is why the ownership check lives here.
    /// </summary>
    [Test]
    public async Task ComposeNodeAsync_WhenTheNodeRunBelongsToAnotherRun_ReadsAsAbsent()
    {
        var otherRunId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var store = Substitute.For<IDevWorkflowStore>();
        store.GetRunAsync(otherRunId, Arg.Any<CancellationToken>()).Returns(Run() with
        {
            Id = otherRunId
        });
        store.GetNodeRunAsync(ResearchNodeRunId, Arg.Any<CancellationToken>())
             .Returns(NodeRun(ResearchNodeRunId, "research", DevWorkflowNodeType.Agent, DevWorkflowNodeRunStatus.Succeeded));
        var composer = Composer(store);

        _ = await AssertEx.ThrowsAsync<DevWorkflowNotFoundException>(() => composer.ComposeNodeAsync(otherRunId, ResearchNodeRunId, CancellationToken.None));
    }

    private static DevWorkflowRunComposer Composer(IDevWorkflowStore store,
        IAgentDefinitionService? agents = null,
        IWorkSessionService? sessions = null) =>
        new(new DevWorkflowRunQueryService(store),
            new DevWorkflowAuthoringService(store),
            agents ?? Substitute.For<IAgentDefinitionService>(),
            sessions ?? Substitute.For<IWorkSessionService>());

    private static DevWorkflowRunDetail Detail(IReadOnlyList<DevWorkflowNodeRunSnapshot> nodeRuns) =>
        new()
        {
            Run = Run(),
            NodeRuns = nodeRuns,
            PendingDecisionCount = 0,
            BlockingGateNodeRunId = null
        };

    private static DevWorkflowRunSnapshot Run() =>
        new()
        {
            Id = RunId,
            WorkItemId = Guid.NewGuid(),
            DefinitionId = DefinitionId,
            DefinitionVersion = 1,
            DefinitionGraphHash = "graph-hash",
            GraphJson = GraphJson,
            GraphRevision = 0,
            Status = DevWorkflowRunStatus.Running,
            LastSequence = 14,
            FailureClass = null,
            TerminalReason = null,
            StartedAtUtc = 11,
            EndedAtUtc = null,
            CreatedAtUtc = 10,
            UpdatedAtUtc = 20,
            Version = 6
        };

    private static DevWorkflowNodeRunSnapshot NodeRun(Guid id, string nodeKey, DevWorkflowNodeType nodeType, DevWorkflowNodeRunStatus status) =>
        new()
        {
            Id = id,
            RunId = RunId,
            NodeKey = nodeKey,
            NodeType = nodeType,
            Attempt = 1,
            MaxAttempts = 1,
            SessionResumes = 0,
            Status = status,
            QueueReason = null,
            PendingDecisionKind = null,
            Sequence = 5,
            WorkSessionId = null,
            WorkSessionAvailable = false,
            AgentDefinitionId = null,
            DevelopmentProjectId = null,
            DevelopmentTaskId = null,
            InputJson = null,
            OutputJson = null,
            PolicyResolutionJson = null,
            MaterializedFromNodeRunId = null,
            MaterializationIndex = null,
            FailureClass = null,
            TerminalReason = null,
            QueuedAtUtc = null,
            StartedAtUtc = null,
            EndedAtUtc = null,
            CreatedAtUtc = 10
        };

    private static DevWorkflowDefinitionSummary Definition(Guid id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            GraphHash = "graph-hash",
            NodeCount = 2,
            Source = DevWorkflowDefinitionSource.Manual,
            SeedSlug = null,
            Archived = false,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 2
        };

    private static DevWorkflowArtifactSnapshot Artifact(Guid id, bool isStale) =>
        new()
        {
            Id = id,
            RunId = RunId,
            LineageId = Guid.NewGuid(),
            ProducingNodeKey = "research",
            ProducedByNodeRunId = ResearchNodeRunId,
            Name = "report.md",
            Version = 1,
            IsLatest = true,
            Kind = DevWorkflowArtifactKind.Report,
            MediaType = "text/markdown",
            ContentSha256 = "content-hash",
            SizeBytes = 12,
            IsValid = true,
            IsStale = isStale,
            StaleSinceSequence = null,
            StaleBecauseArtifactId = null,
            StaleReason = null,
            ManagedReference = "managed://report.md",
            Sequence = 3,
            CreatedAtUtc = 5
        };

    private static DevWorkflowDecisionSnapshot Decision(Guid nodeRunId, long sequence) =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = RunId,
            NodeRunId = nodeRunId,
            Attempt = 1,
            Decision = DevWorkflowDecisionKind.Approve,
            Comment = null,
            PayloadJson = null,
            DecidedBySubject = null,
            OperationId = Guid.NewGuid(),
            Sequence = sequence,
            DecidedAtUtc = 6
        };

    private static AgentDefinitionRecord Agent() =>
        new()
        {
            Id = AgentId,
            Name = "Researcher",
            Description = null,
            Instructions = "Do the research.",
            ModelProfile = "qwen",
            ReasoningEffort = null,
            Kind = AgentDefinitionKind.Single,
            AllowedToolNames = [],
            ToolApprovals = new Dictionary<string, bool>(),
            OrchestrationTopologyJson = null,
            Version = 1,
            CreatedAtUtc = 1,
            UpdatedAtUtc = 2
        };

    private static WorkSessionDetail Session() =>
        new()
        {
            Id = SessionId,
            Title = "Research",
            Objective = "Find out.",
            Kind = AgentWorkSessionKind.General,
            Status = AgentWorkSessionStatus.Running,
            AgentDefinitionId = AgentId,
            ConversationId = ConversationId,
            CurrentTaskId = null,
            StepCount = 1,
            MaxStepsPerRun = 10,
            LastCheckpointId = null,
            LastSequence = 2,
            Version = 1,
            CreatedUtc = 1,
            UpdatedUtc = 2
        };
}
