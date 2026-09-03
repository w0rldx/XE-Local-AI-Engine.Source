namespace XE_Local_AI_Engine.Tests.Endpoints.DevelopmentWorkflows.V1;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Runs, their feeds, the node drill-down and the one decision route.</summary>
public sealed class DevWorkflowRunEndpointTests
{
    private const string Root = "/api/local/v1/development-workflows";
    private const string Runs = $"{Root}/runs";
    private const string Run = $"{Runs}/33333333-3333-3333-3333-333333333333";
    private const string NodeRun = $"{Run}/nodes/44444444-4444-4444-4444-444444444444";
    private const string WorkItemRuns = $"{Root}/work-items/11111111-1111-1111-1111-111111111111/runs";
    private const string ArtifactContent = $"{Run}/artifacts/55555555-5555-5555-5555-555555555555/content";

    private static readonly Guid WorkItemId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid DefinitionId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid GateNodeRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid ArtifactId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid ResearchNodeRunId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid SessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ConversationId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid OperationId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid RuleSetId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    /// <summary>What a node run recorded at materialization: the id, the name and the hash of the text that applied.</summary>
    private const string RecordedPolicy =
        """[{"id":"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa","name":"House rules","contentSha256":"content-hash","body":"Never touch production."}]""";

    /// <summary>The seeded Slice-A shape: one agent into one TERMINAL gate — no out-edge takes a rejection.</summary>
    private const string SampleGraph = """
                                       {"schemaVersion":1,
                                        "nodes":[{"nodeKey":"research","nodeType":"Agent","label":"Research","agentSeedSlug":"researcher","modelProfile":"qwen","maxAttempts":3},
                                                 {"nodeKey":"approval","nodeType":"HumanGate","label":"Approve the plan","instructions":"Read the plan, then answer."}],
                                        "edges":[{"from":"research","to":"approval"}]}
                                       """;

    [Test]
    [Arguments("GET", Runs)]
    [Arguments("POST", WorkItemRuns)]
    [Arguments("GET", Run)]
    [Arguments("POST", $"{Run}/pause")]
    [Arguments("POST", $"{Run}/resume")]
    [Arguments("POST", $"{Run}/cancel")]
    [Arguments("GET", $"{Run}/events")]
    [Arguments("GET", NodeRun)]
    [Arguments("POST", $"{NodeRun}/decision")]
    [Arguments("GET", $"{Run}/artifacts")]
    [Arguments("GET", ArtifactContent)]
    public async Task DevWorkflowRunRoute_WhenTheOperatorTokenIsMissing_ReturnsUnauthorized(string method, string route)
    {
        await using var factory = EnabledFactory(Store(), Substitute.For<IDevWorkflowRunService>());
        using var client = factory.CreateClient();
        using var request = Request(method, route);

        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {route} must require the operator token.");
    }

    /// <summary>
    ///     Every lifecycle command is fire-and-forget: the endpoint commits an intent and the dispatcher acts on its
    ///     own clock. A 200 would tell a schema-trusting client the transition had finished while the body it reads
    ///     says <c>Pausing</c>.
    /// </summary>
    [Test]
    [Arguments("POST", WorkItemRuns)]
    [Arguments("POST", $"{Run}/pause")]
    [Arguments("POST", $"{Run}/resume")]
    [Arguments("POST", $"{Run}/cancel")]
    public async Task LifecycleVerb_AnswersAccepted(string method, string route)
    {
        var runs = RunService();
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, method, route, StartBody()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode, $"{method} {route} must answer 202: the transition completes out of band.");
    }

    [Test]
    public async Task StartRun_TakesTheWorkItemFromTheRouteAndTheDefinitionFromTheBody()
    {
        var runs = RunService();
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "POST", WorkItemRuns, StartBody()).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await runs.Received(1).StartAsync(WorkItemId, DefinitionId, """{"depth":"quick"}""", OperationId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StartRun_WhenTheWorkItemAlreadyHasALiveRun_ReturnsTheRunInFlightConflict()
    {
        var runs = RunService();
        runs.StartAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DevWorkflowRunInFlightException("one live run per work item"));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "POST", WorkItemRuns, StartBody()).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowRunInFlight", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>The one place the nullable project is enforced, and enforced against the GRAPH rather than the work item.</summary>
    [Test]
    public async Task StartRun_WithARepositoryBoundGraphOnAProjectlessWorkItem_ReturnsBadRequest()
    {
        var runs = RunService();
        runs.StartAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DevWorkflowValidationException("This workflow runs commands in a repository (build), so the work item has to name one."));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "POST", WorkItemRuns, StartBody()).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(body, "generalErrors", StringComparison.Ordinal);
        AssertEx.Contains(body, "runs commands in a repository", StringComparison.Ordinal);
    }

    [Test]
    public async Task ResumingARunThatIsNotPaused_ReturnsTheInvalidTransitionConflict()
    {
        var runs = RunService();
        runs.ResumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DevWorkflowInvalidTransitionException("This run is Running, so there is nothing to resume."));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "POST", $"{Run}/resume", StartBody()).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowInvalidTransition", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>
    ///     Run detail is THE repaint fetch: the pinned graph and every node summary in one round trip, with the agent
    ///     name and model label already resolved. If a card needed a follow-up request, the live view would fan out.
    /// </summary>
    [Test]
    public async Task GetRun_CarriesThePinnedGraphTheNodeSummariesAndTheCounters()
    {
        var store = Store();
        var runs = RunService();
        await using var factory = EnabledFactory(store, runs);

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.Equal("WaitingForApproval", root.GetProperty("status").GetString());
        AssertEx.Equal("Research → Plan → Approval", root.GetProperty("definitionName").GetString());
        AssertEx.Equal(1, root.GetProperty("pendingDecisionCount").GetInt32());
        AssertEx.Equal(GateNodeRunId, root.GetProperty("blockingGateNodeRunId").GetGuid());
        AssertEx.Equal(2, root.GetProperty("graph").GetProperty("nodes").GetArrayLength());

        var gate = root.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("nodeKey").GetString() == "approval");
        AssertEx.Equal("Approve the plan", gate.GetProperty("label").GetString(), "the label comes from the pinned graph, not from the node run row.");
        AssertEx.Equal("Approve", gate.GetProperty("pendingDecisionKind").GetString());
        AssertEx.False(gate.GetProperty("hasStaleInputs").GetBoolean());

        var research = root.GetProperty("nodes").EnumerateArray().Single(node => node.GetProperty("nodeKey").GetString() == "research");
        AssertEx.Equal("researcher", research.GetProperty("agentDisplayName").GetString(), "a slug-bound node names its slug: there is no agent id on the row.");
        AssertEx.Equal("qwen",
            research.GetProperty("modelLabel").GetString(),
            "the node's own modelProfile is the pin its work session is created and resumed with, so it is the model the run actually loads — and it wins "
            + "over the bound agent's, which is why it is read off the RUN's pinned graph rather than the definition as it stands now.");
    }

    /// <summary>
    ///     The graph view repaints on every change notification, so the query budget is fixed: no per-node read may
    ///     creep into it.
    /// </summary>
    [Test]
    public async Task GetRun_IssuesNoPerNodeQuery()
    {
        var store = Store();
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.DidNotReceive().GetNodeRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await store.DidNotReceive().ListConsumedArtifactIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Join and dependency waiting is <c>Pending</c> plus the keys it waits on, computed server-side. A client that
    ///     re-derived it would duplicate the dispatcher's evaluation and drift from it.
    /// </summary>
    [Test]
    public async Task GetRun_NamesWhatAPendingNodeIsWaitingOn_AndSaysNothingForOneThatIsNot()
    {
        var runs = RunService(Detail(gateStatus: DevWorkflowNodeRunStatus.Pending, researchStatus: DevWorkflowNodeRunStatus.Running));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        var gate = nodes.Single(node => node.GetProperty("nodeKey").GetString() == "approval");
        var research = nodes.Single(node => node.GetProperty("nodeKey").GetString() == "research");
        AssertEx.Equal("research", gate.GetProperty("waitingOnNodeKeys")[0].GetString());
        AssertEx.Equal(JsonValueKind.Null, research.GetProperty("waitingOnNodeKeys").ValueKind, "a running node is not waiting on anything.");
    }

    /// <summary>
    ///     A join is not waiting on the materialization template that declares an edge into it. The template is the one
    ///     node deliberately never instantiated, so naming it would show every decomposing run as stuck on a node that
    ///     has no row and never will — and it is the dispatcher's own edge rule that says so, read here rather than
    ///     re-derived, so the page and the runtime cannot disagree about what a run is waiting for.
    /// </summary>
    [Test]
    public async Task GetRun_DoesNotReportAJoinAsWaitingOnAMaterializationTemplate()
    {
        const string DecompositionGraph = """
                                          {"schemaVersion":1,
                                           "nodes":[{"nodeKey":"decompose","nodeType":"Agent","label":"Decompose",
                                                     "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"join","maxChildren":4}},
                                                    {"nodeKey":"implement","nodeType":"DevTask"},
                                                    {"nodeKey":"join","nodeType":"Join"}],
                                           "edges":[{"from":"decompose","to":"join"},{"from":"implement","to":"join"}]}
                                          """;

        var decompose = GateNodeRun() with
        {
            NodeKey = "decompose",
            NodeType = DevWorkflowNodeType.Agent,
            Status = DevWorkflowNodeRunStatus.Succeeded,
            PendingDecisionKind = null
        };
        var join = GateNodeRun() with
        {
            Id = ResearchNodeRunId,
            NodeKey = "join",
            NodeType = DevWorkflowNodeType.Join,
            Status = DevWorkflowNodeRunStatus.Pending,
            PendingDecisionKind = null
        };
        var runs = RunService(new DevWorkflowRunDetail(RunSnapshot() with
            {
                GraphJson = DecompositionGraph
            },
            [decompose, join],
            PendingDecisionCount: 0,
            BlockingGateNodeRunId: null));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var waiting = document.RootElement.GetProperty("nodes")
                              .EnumerateArray()
                              .Single(static node => node.GetProperty("nodeKey").GetString() == "join")
                              .GetProperty("waitingOnNodeKeys");

        AssertEx.Equal(JsonValueKind.Null, waiting.ValueKind, $"the join is waiting on {waiting}, and the decomposition it really depends on has already succeeded.");
    }

    /// <summary>
    ///     The fan-out's identity and size are the SERVER's answer: the group is the decompose node run that produced
    ///     it, and the count is taken over the run's whole node-run list. A client counting the rows it drew is wrong by
    ///     construction past the render cap, which is the bug this replaces.
    ///     <para>
    ///         The template here is a TWO-node subtree cloned twice, which is the shape that catches the counting
    ///         mistake: four clone ROWS, two CHILDREN, and a <c>materializationIndex</c> that only ever counts children.
    ///         The badge reads "1 of 2", so the count has to be distinct indexes and not rows.
    ///     </para>
    /// </summary>
    [Test]
    public async Task GetRun_NamesAMaterializationGroupAndCountsItsChildrenRatherThanItsCloneRows()
    {
        const string DecompositionGraph = """
                                          {"schemaVersion":1,
                                           "nodes":[{"nodeKey":"decompose","nodeType":"Agent","label":"Decompose",
                                                     "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"join","maxChildren":4}},
                                                    {"nodeKey":"implement","nodeType":"DevTask"},
                                                    {"nodeKey":"review","nodeType":"Agent"},
                                                    {"nodeKey":"join","nodeType":"Join"}],
                                           "edges":[{"from":"decompose","to":"join"},{"from":"implement","to":"review"},{"from":"review","to":"join"}]}
                                          """;

        var decompose = GateNodeRun() with
        {
            NodeKey = "decompose",
            NodeType = DevWorkflowNodeType.Agent,
            Status = DevWorkflowNodeRunStatus.Succeeded,
            PendingDecisionKind = null
        };

        // Two children of a two-node template: every node of the subtree is cloned per child, so the group holds FOUR
        // rows carrying only TWO distinct indexes.
        var firstChildImplement = GateNodeRun() with
        {
            Id = Guid.NewGuid(),
            NodeKey = "implement#1",
            NodeType = DevWorkflowNodeType.DevTask,
            Status = DevWorkflowNodeRunStatus.Running,
            PendingDecisionKind = null,
            MaterializedFromNodeRunId = decompose.Id,
            MaterializationIndex = 0
        };
        var firstChildReview = firstChildImplement with
        {
            Id = Guid.NewGuid(),
            NodeKey = "review#1",
            NodeType = DevWorkflowNodeType.Agent
        };
        var secondChildImplement = firstChildImplement with
        {
            Id = Guid.NewGuid(),
            NodeKey = "implement#2",
            MaterializationIndex = 1
        };
        var secondChildReview = firstChildReview with
        {
            Id = Guid.NewGuid(),
            NodeKey = "review#2",
            MaterializationIndex = 1
        };
        var join = GateNodeRun() with
        {
            Id = ResearchNodeRunId,
            NodeKey = "join",
            NodeType = DevWorkflowNodeType.Join,
            Status = DevWorkflowNodeRunStatus.Pending,
            PendingDecisionKind = null
        };
        var runs = RunService(new DevWorkflowRunDetail(RunSnapshot() with
            {
                GraphJson = DecompositionGraph
            },
            [decompose, firstChildImplement, firstChildReview, secondChildImplement, secondChildReview, join],
            PendingDecisionCount: 0,
            BlockingGateNodeRunId: null));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToArray();
        var clones = nodes.Where(static node => node.GetProperty("isMaterialized").GetBoolean()).ToArray();

        AssertEx.Equal(4, clones.Length);
        AssertEx.True(clones.All(clone => clone.GetProperty("materializationGroupId").GetGuid() == decompose.Id),
            "one decompose node run materializes once, so its id names the group for the life of the run.");
        AssertEx.True(clones.All(static clone => clone.GetProperty("materializationCount").GetInt32() == 2),
            "two children, whatever the template's node count — the badge reads 1 of 2 against the child ordinal, never 1 of 4.");
        AssertEx.Equal(JsonValueKind.Null,
            nodes.Single(static node => node.GetProperty("nodeKey").GetString() == "join").GetProperty("materializationGroupId").ValueKind,
            "a node that was never cloned belongs to no group and carries no count.");
        AssertEx.True(document.RootElement.GetProperty("graph")
                              .GetProperty("nodes")
                              .EnumerateArray()
                              .Single(static node => node.GetProperty("nodeKey").GetString() == "implement")
                              .GetProperty("isTemplate")
                              .GetBoolean());
        AssertEx.True(document.RootElement.GetProperty("graph")
                              .GetProperty("nodes")
                              .EnumerateArray()
                              .Single(static node => node.GetProperty("nodeKey").GetString() == "review")
                              .GetProperty("isTemplate")
                              .GetBoolean(),
            "the whole subtree short of the join is template, which is what makes this the two-node case.");
    }

    /// <summary>
    ///     The same for a graph with TWO decompositions, each with a template of its own. Both joins are asked, because
    ///     the answer has to come from a walk per materialization: one join's template must not be mistaken for the
    ///     other's, and neither may be reported as something a node run is waiting for.
    /// </summary>
    [Test]
    public async Task GetRun_DoesNotReportEitherJoinOfATwoDecompositionGraphAsWaitingOnATemplate()
    {
        const string TwoDecompositions = """
                                         {"schemaVersion":1,
                                          "nodes":[{"nodeKey":"first","nodeType":"Agent","label":"Decompose one",
                                                    "materialization":{"templateNodeKey":"implement","artifactKind":"TaskPackage","joinNodeKey":"joinone","maxChildren":4}},
                                                   {"nodeKey":"implement","nodeType":"DevTask"},
                                                   {"nodeKey":"joinone","nodeType":"Join"},
                                                   {"nodeKey":"second","nodeType":"Agent","label":"Decompose two",
                                                    "materialization":{"templateNodeKey":"plan","artifactKind":"TaskPackage","joinNodeKey":"jointwo","maxChildren":4}},
                                                   {"nodeKey":"plan","nodeType":"DevTask"},
                                                   {"nodeKey":"jointwo","nodeType":"Join"}],
                                          "edges":[{"from":"first","to":"joinone"},{"from":"implement","to":"joinone"},{"from":"joinone","to":"second"},
                                                   {"from":"second","to":"jointwo"},{"from":"plan","to":"jointwo"}]}
                                         """;

        var keys = new[]
        {
            "first",
            "joinone",
            "second",
            "jointwo"
        };
        var rows = keys.Select((key, index) => GateNodeRun() with
                       {
                           Id = Guid.Parse($"aaaaaaaa-aaaa-aaaa-aaaa-00000000000{index}"),
                           NodeKey = key,
                           NodeType = key.StartsWith("join", StringComparison.Ordinal) ? DevWorkflowNodeType.Join : DevWorkflowNodeType.Agent,
                           Status = key == "first" ? DevWorkflowNodeRunStatus.Succeeded : DevWorkflowNodeRunStatus.Pending,
                           PendingDecisionKind = null
                       })
                       .ToList();
        var runs = RunService(new DevWorkflowRunDetail(RunSnapshot() with
            {
                GraphJson = TwoDecompositions
            },
            rows,
            PendingDecisionCount: 0,
            BlockingGateNodeRunId: null));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        var waiting = document.RootElement.GetProperty("nodes")
                              .EnumerateArray()
                              .ToDictionary(node => node.GetProperty("nodeKey").GetString()!, node => node.GetProperty("waitingOnNodeKeys").ToString());

        AssertEx.Equal(string.Empty, waiting["joinone"], "the first join waits on nothing: the branch into it is its own template's.");
        AssertEx.Equal("""["second"]""", waiting["jointwo"], "and the second waits only on the real node ahead of it, never on the template the two share.");
    }

    [Test]
    public async Task ListRuns_ForwardsTheFilters()
    {
        var store = Store();
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", $"{Runs}?workItemId={WorkItemId}&status=Running&limit=5").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1).ListRunSummariesAsync(WorkItemId, DevWorkflowRunStatus.Running, 5, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("status=Nonsense")]
    [Arguments("limit=0")]
    [Arguments("limit=201")]
    public async Task ListRuns_WithAnOutOfRangeQuery_ReturnsBadRequest(string query)
    {
        var store = Store();
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", $"{Runs}?{query}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await store.DidNotReceive().ListRunSummariesAsync(Arg.Any<Guid?>(), Arg.Any<DevWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The event feed reads one over the limit so "there is more" is observed rather than inferred, and reports the
    ///     page's HIGHEST sequence — sequences are strictly increasing but not contiguous, because the run's counter is
    ///     shared with node runs and artifacts.
    /// </summary>
    [Test]
    public async Task EventFeed_ForwardsTheWatermarkAndReportsHasMoreFromTheOneOverProbe()
    {
        var store = Store();
        store.ListEventsAsync(RunId, 7, 3, Arg.Any<CancellationToken>()).Returns([Event(9), Event(12), Event(14)]);
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", $"{Run}/events?sinceSeq=7&limit=2").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1).ListEventsAsync(RunId, 7, 3, Arg.Any<CancellationToken>());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(2, document.RootElement.GetProperty("items").GetArrayLength(), "the probe row is not part of the page.");
        AssertEx.Equal(12L, document.RootElement.GetProperty("lastSequence").GetInt64());
        AssertEx.True(document.RootElement.GetProperty("hasMore").GetBoolean());
    }

    [Test]
    [Arguments("sinceSeq=-1")]
    [Arguments("limit=501")]
    public async Task EventFeed_WithAnOutOfRangeQuery_ReturnsBadRequest(string query)
    {
        var store = Store();
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", $"{Run}/events?{query}").ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await store.DidNotReceive().ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("GET", $"{Run}/events", null)]
    [Arguments("GET", $"{Run}/artifacts", null)]
    [Arguments("GET", NodeRun, null)]
    [Arguments("GET", ArtifactContent, null)]
    public async Task DevWorkflowRunRoute_WhenTheRunIsUnknown_ReturnsBodylessNotFound(string method, string route, string? body)
    {
        var store = Store();
        var missing = new DevWorkflowNotFoundException("gone");
        store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        store.GetArtifactAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsyncForAnyArgs(missing);
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, method, route, body).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, $"{method} {route} must answer 404 for an unknown run.");
        AssertEx.Null(response.Content.Headers.ContentType, $"{method} {route} must not attach a content type to a bodyless 404.");
    }

    /// <summary>
    ///     The drill-down joins the work-session family on the loose id and projects the conversation, which is what
    ///     lets the pane link out to the EXISTING work-session views instead of this surface growing its own.
    /// </summary>
    [Test]
    public async Task GetNodeRun_ProjectsTheSessionJoinTheGateEvidenceAndTheAllowedAnswers()
    {
        var store = Store();
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        AssertEx.Equal("Approve the plan", root.GetProperty("label").GetString());
        AssertEx.Equal("Read the plan, then answer.", root.GetProperty("instructions").GetString(), "a gate's prompt is the node's instructions.");
        AssertEx.Equal(SessionId, root.GetProperty("workSessionId").GetGuid());
        AssertEx.Equal(ConversationId, root.GetProperty("conversationId").GetGuid());
        AssertEx.True(root.GetProperty("workSessionAvailable").GetBoolean());
        AssertEx.Equal(ArtifactId, root.GetProperty("primaryArtifactId").GetGuid(), "the headline output is the newest version the node produced.");
        AssertEx.Equal(ArtifactId, root.GetProperty("producedArtifactIds")[0].GetGuid());

        var allowed = root.GetProperty("allowedDecisions").EnumerateArray().Select(static value => value.GetString());
        AssertEx.Equal("Approve,Reject,RequestChanges",
            string.Join(",", allowed),
            "a gate offers its three answers and nothing else: no Retry, which has no attempt to repeat, and no Skip, which would walk past the approval.");

        // The seeded gate is terminal: no out-edge accepts a rejection, so rejecting it ENDS the run — and the confirm
        // dialog can only say so because the server answered this before the click.
        AssertEx.False(root.GetProperty("hasRejectBranch").GetBoolean());
    }

    [Test]
    public async Task GetNodeRun_WhenTheSessionIsGone_SaysSoAndDoesNotReachForTheConversation()
    {
        var store = Store();
        var sessions = Substitute.For<IAgentWorkSessionStore>();
        var runs = RunService(Detail(workSessionAvailable: false));
        await using var factory = EnabledFactory(store, runs, sessions);
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun(workSessionAvailable: false));

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.False(document.RootElement.GetProperty("workSessionAvailable").GetBoolean());
        AssertEx.Equal(JsonValueKind.Null, document.RootElement.GetProperty("conversationId").ValueKind);
        AssertEx.Empty(sessions.ReceivedCalls());
    }

    [Test]
    public async Task GetNodeRun_OfAnotherRun_ReadsAsAbsent()
    {
        var store = Store();
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun() with
        {
            RunId = Guid.NewGuid()
        });
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "one run's route must never surface another run's node.");
    }

    [Test]
    public async Task ArtifactFeed_ForwardsTheWatermarkAndNeverCarriesTheBlobReference()
    {
        const string ManagedReference = "dev-workflows/33333333/55555555.bin";
        var store = Store();
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", $"{Run}/artifacts?sinceSeq=4").ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await store.Received(1).ListArtifactsAsync(RunId, 4, Arg.Any<CancellationToken>());
        AssertEx.False(body.Contains(ManagedReference, StringComparison.Ordinal));
        AssertEx.False(body.Contains("managedReference", StringComparison.OrdinalIgnoreCase));
        using var document = JsonDocument.Parse(body);
        var artifact = document.RootElement.GetProperty("items")[0];
        AssertEx.True(artifact.GetProperty("isLatest").GetBoolean(), "isLatest ships computed, so no client re-derives it.");
        AssertEx.Equal("approval", artifact.GetProperty("producingNodeKey").GetString());
    }

    /// <summary>
    ///     Whether the bytes are base64 is decided from the declared media type, never by sniffing them: binary content
    ///     handed over as UTF-8 would arrive mangled and a text artifact base64'd would be unreadable in the viewer.
    /// </summary>
    [Test]
    [Arguments("text/markdown", false, "# the plan")]
    [Arguments("application/octet-stream", true, "AAECAw==")]
    public async Task ArtifactContent_DecidesBase64FromTheMediaType(string mediaType, bool expectBase64, string expectedContent)
    {
        var bytes = expectBase64
            ? new byte[]
            {
                0,
                1,
                2,
                3
            }
            : Encoding.UTF8.GetBytes("# the plan");
        var store = Store();
        store.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(mediaType: mediaType, sizeBytes: bytes.Length));
        var blobs = Substitute.For<IDevWorkflowArtifactBlobStore>();
        blobs.ReadAsync(RunId, ArtifactId, Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(new DevWorkflowArtifactBlobReadResult(DevWorkflowArtifactReadStatus.Found, bytes));
        await using var factory = EnabledFactory(store, RunService(), blobs: blobs);

        using var response = await SendAsync(factory, "GET", ArtifactContent).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expectBase64, document.RootElement.GetProperty("isBase64").GetBoolean());
        AssertEx.Equal(expectedContent, document.RootElement.GetProperty("content").GetString());
    }

    [Test]
    public async Task ArtifactContent_WhenTheBytesNoLongerVerify_ReturnsNotFound()
    {
        var store = Store();
        var blobs = Substitute.For<IDevWorkflowArtifactBlobStore>();
        blobs.ReadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
             .Returns(new DevWorkflowArtifactBlobReadResult(DevWorkflowArtifactReadStatus.HashMismatch, ReadOnlyMemory<byte>.Empty));
        await using var factory = EnabledFactory(store, RunService(), blobs: blobs);

        using var response = await SendAsync(factory, "GET", ArtifactContent).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode, "bytes the node cannot vouch for are not bytes it hands over.");
    }

    [Test]
    public async Task ArtifactContent_OverTheNodeCeiling_Returns413AndNeverReadsTheBlob()
    {
        var store = Store();
        store.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact(sizeBytes: 4096));
        var blobs = Substitute.For<IDevWorkflowArtifactBlobStore>();
        await using var factory = EnabledFactory(store, RunService(), sessions: null, blobs, ("DevWorkflows:MaxArtifactBytes", "1024"));

        using var response = await SendAsync(factory, "GET", ArtifactContent).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        await blobs.DidNotReceive().ReadAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ArtifactContent_OfAnotherRun_ReadsAsAbsent()
    {
        var store = Store();
        store.GetArtifactAsync(ArtifactId, Arg.Any<CancellationToken>()).Returns(Artifact() with
        {
            RunId = Guid.NewGuid()
        });
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", ArtifactContent).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    ///     The endpoint is transport and nothing else: it parses the kind, reads WHO is deciding from the token, and
    ///     hands both to the runtime, which owns the idempotency the operation id keys.
    /// </summary>
    [Test]
    public async Task Decision_ForwardsTheParsedKindThePayloadAndTheDecidingAccount()
    {
        var runs = RunService();
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory,
                "POST",
                $"{NodeRun}/decision",
                $$"""{"operationId":"{{OperationId}}","decision":"Approve","comment":"Looks right.","payloadJson":"{\"edited\":true}"}""")
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        await runs.Received(1)
                  .DecideAsync(RunId,
                      GateNodeRunId,
                      OperationId,
                      DevWorkflowDecisionKind.Approve,
                      "Looks right.",
                      """{"edited":true}""",
                      "node-admin-test",
                      Arg.Any<CancellationToken>());
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("Approve", document.RootElement.GetProperty("decision").GetProperty("decision").GetString());
        AssertEx.Equal("node-admin-test",
            document.RootElement.GetProperty("decision").GetProperty("decidedBySubject").GetString(),
            "the audit has to be able to say WHO approved, not only that someone did.");
        AssertEx.Equal("WaitingForApproval", document.RootElement.GetProperty("runStatus").GetString());
        AssertEx.Equal("WaitingForApproval", document.RootElement.GetProperty("nodeRunStatus").GetString());
    }

    [Test]
    [Arguments("""{"decision":"Approve"}""")]
    [Arguments("""{"operationId":"99999999-9999-9999-9999-999999999999","decision":"Nonsense"}""")]
    [Arguments("""{"operationId":"99999999-9999-9999-9999-999999999999"}""")]
    public async Task Decision_WhenTheShapeIsWrong_ReturnsBadRequestAndNeverReachesTheRuntime(string body)
    {
        var runs = RunService();
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "POST", $"{NodeRun}/decision", body).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await runs.DidNotReceive()
                  .DecideAsync(Arg.Any<Guid>(),
                      Arg.Any<Guid>(),
                      Arg.Any<Guid>(),
                      Arg.Any<DevWorkflowDecisionKind>(),
                      Arg.Any<string?>(),
                      Arg.Any<string?>(),
                      Arg.Any<string?>(),
                      Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A NEW operation id at an answered gate is not the idempotent replay a repeated one is — it is a second human
    ///     act on a closed gate, and the refusal carries what already stands so the UI can say what happened.
    /// </summary>
    [Test]
    public async Task Decision_WithADifferentOperationIdOnAnAnsweredGate_ReturnsTheStandingDecision()
    {
        var runs = RunService();
        runs.DecideAsync(Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<DevWorkflowDecisionKind>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DevWorkflowGateAlreadyDecidedException("already answered", DevWorkflowDecisionKind.RequestChanges));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory,
                "POST",
                $"{NodeRun}/decision",
                $$"""{"operationId":"{{Guid.NewGuid()}}","decision":"Approve"}""")
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowGateAlreadyDecided", document.RootElement.GetProperty("conflictType").GetString());
        AssertEx.Equal("RequestChanges", document.RootElement.GetProperty("standingDecision").GetString());
    }

    [Test]
    public async Task Decision_OnANodeRunThatIsNeitherWaitingNorBlocked_ReturnsTheInvalidTransitionConflict()
    {
        var runs = RunService();
        runs.DecideAsync(Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<DevWorkflowDecisionKind>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .ThrowsAsyncForAnyArgs(new DevWorkflowInvalidTransitionException("Node run 'research' is Running, so there is nothing to decide on it."));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "POST", $"{NodeRun}/decision", $$"""{"operationId":"{{OperationId}}","decision":"Approve"}""")
            .ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal("DevWorkflowInvalidTransition", document.RootElement.GetProperty("conflictType").GetString());
    }

    /// <summary>Enum members cross the wire as their NAMES; the client's narrowing helpers depend on it.</summary>
    [Test]
    public async Task RunResponses_SpellEveryEnumAsItsName()
    {
        await using var factory = EnabledFactory(Store(), RunService());

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);
        var nodes = document.RootElement.GetProperty("nodes").EnumerateArray().ToList();
        AssertEx.Equal("WaitingForApproval", document.RootElement.GetProperty("status").GetString());
        AssertEx.Equal("HumanGate", nodes.Single(node => node.GetProperty("nodeKey").GetString() == "approval").GetProperty("nodeType").GetString());
        AssertEx.Equal("Agent", nodes.Single(node => node.GetProperty("nodeKey").GetString() == "research").GetProperty("nodeType").GetString());
    }

    private static string StartBody() =>
        $$"""{"operationId":"{{OperationId}}","definitionId":"{{DefinitionId}}","inputsJson":"{\"depth\":\"quick\"}"}""";

    /// <summary>
    ///     P3.7: the node-run drill-down projects the resolution the ROW recorded, so it keeps naming the exact text
    ///     that applied — by hash — whether or not the rule set still exists.
    /// </summary>
    [Test]
    public async Task GetNodeRun_ProjectsTheRuleSetsTheRowRecorded()
    {
        var store = Store();
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun() with
        {
            PolicyResolutionJson = RecordedPolicy
        });
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var applied = document.RootElement.GetProperty("appliedRuleSets");

        AssertEx.Equal(expected: 1, applied.GetArrayLength(), "the recorded resolution reaches the node pane.");
        AssertEx.Equal("House rules", applied[0].GetProperty("name").GetString());
        AssertEx.Equal("content-hash", applied[0].GetProperty("contentSha256").GetString(), "the hash is what proves WHICH text applied.");
    }

    /// <summary>
    ///     Editing a rule set mid-run is allowed, so the pane has to be able to SAY the document moved on: the recorded
    ///     hash never changes, the current one comes from the row as it stands now, and a reader compares them. Without
    ///     the second half, "which rules applied" silently reads as "which rules exist".
    /// </summary>
    [Test]
    public async Task GetNodeRun_WhenTheRuleSetWasEditedSinceItApplied_ReportsBothHashes()
    {
        var store = Store();
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun() with
        {
            PolicyResolutionJson = RecordedPolicy
        });
        store.ListRuleSetsAsync(Arg.Any<CancellationToken>())
             .Returns([
                 new DevWorkflowRuleSetSummary(RuleSetId,
                     "House rules, renamed",
                     Description: null,
                     """{"projectIds":[],"nodeTypes":[]}""",
                     Enabled: true,
                     "content-hash-v2",
                     Version: 2,
                     CreatedAtUtc: 1,
                     UpdatedAtUtc: 3)
             ]);
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var applied = document.RootElement.GetProperty("appliedRuleSets")[0];

        AssertEx.Equal("content-hash", applied.GetProperty("contentSha256").GetString(), "the recorded hash is the audit and never moves.");
        AssertEx.Equal("content-hash-v2", applied.GetProperty("currentContentSha256").GetString(), "and the current one is what says the document has been edited since.");
        AssertEx.Equal("House rules", applied.GetProperty("name").GetString(), "the NAME stays the recorded one: renaming a rule set must not rewrite what the audit says applied.");
    }

    /// <summary>
    ///     The snapshotted TEXT stays off the wire. The node run carries it so the objective can be composed from what
    ///     applied, but a node-run response is an audit view: it names the document and its hashes, and a reader who
    ///     wants the text asks the rule set for it.
    /// </summary>
    [Test]
    public async Task GetNodeRun_DoesNotPutTheSnapshottedRuleSetTextOnTheWire()
    {
        var store = Store();
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun() with
        {
            PolicyResolutionJson = RecordedPolicy
        });
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertEx.False(body.Contains("Never touch production.", StringComparison.Ordinal), "the recorded policy TEXT must not be echoed by the node-run response.");
        using var document = JsonDocument.Parse(body);
        var applied = document.RootElement.GetProperty("appliedRuleSets")[0];
        AssertEx.False(applied.TryGetProperty("body", out _), "and the wire shape carries no body member at all.");
        AssertEx.Equal("content-hash", applied.GetProperty("contentSha256").GetString(), "the hashes are what the audit view is for.");
    }

    /// <summary>A deleted rule set reads as a null current hash — the recorded half is untouched, which is the point of recording it.</summary>
    [Test]
    public async Task GetNodeRun_WhenTheRuleSetWasDeleted_ReportsNoCurrentHashAndKeepsTheRecordedOne()
    {
        var store = Store();
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun() with
        {
            PolicyResolutionJson = RecordedPolicy
        });
        store.ListRuleSetsAsync(Arg.Any<CancellationToken>()).Returns([]);
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var applied = document.RootElement.GetProperty("appliedRuleSets")[0];

        AssertEx.Equal("content-hash", applied.GetProperty("contentSha256").GetString());
        AssertEx.Equal(JsonValueKind.Null, applied.GetProperty("currentContentSha256").ValueKind, "a deleted rule set has no current text, and null says so.");
    }

    /// <summary>
    ///     A recorded resolution nothing can parse costs this node its rule-set list, not the whole drill-down. It can
    ///     only come from a hand-edited row, and answering 500 would make one bad row hide every other thing the pane
    ///     is there to show.
    /// </summary>
    [Test]
    public async Task GetNodeRun_WithAnUnreadableRecordedResolution_AnswersAnEmptyListRatherThanFailing()
    {
        var store = Store();
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun() with
        {
            PolicyResolutionJson = "not json at all"
        });
        await using var factory = EnabledFactory(store, RunService());

        using var response = await SendAsync(factory, "GET", NodeRun).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        AssertEx.Equal(expected: 0, document.RootElement.GetProperty("appliedRuleSets").GetArrayLength());
    }

    /// <summary>
    ///     Whether a skip is WAIVED is the server's answer, because the row cannot be read on its own. An operator's
    ///     skip is excused and an <c>All</c> join carries on past it as long as a sibling arrived; a skip that cascaded
    ///     off a Failed ancestor is dead and the join skips with it. Both are <c>Skipped</c>, and the ancestor that
    ///     decides which is which — <c>broken</c> here — is not among the join's own dependencies, so a client reading
    ///     status alone would tell an operator the join carries on in exactly the case where the runtime skips it.
    /// </summary>
    [Test]
    public async Task GetRun_TellsAnExcusedSkipFromOneThatCascadedOffAFailure()
    {
        const string SkipGraph = """
                                 {"schemaVersion":1,
                                  "nodes":[{"nodeKey":"survey","nodeType":"Agent"},
                                           {"nodeKey":"excused","nodeType":"Agent"},
                                           {"nodeKey":"broken","nodeType":"Agent"},
                                           {"nodeKey":"cascaded","nodeType":"Agent"},
                                           {"nodeKey":"join","nodeType":"Join"}],
                                  "edges":[{"from":"survey","to":"join"},{"from":"survey","to":"excused"},
                                           {"from":"survey","to":"broken"},{"from":"excused","to":"join"},
                                           {"from":"broken","to":"cascaded"},{"from":"cascaded","to":"join"}]}
                                 """;

        var runs = RunService(new DevWorkflowRunDetail(RunSnapshot() with
            {
                GraphJson = SkipGraph
            },
            [
                WorkNodeRun(1, "survey", DevWorkflowNodeRunStatus.Succeeded),
                WorkNodeRun(2, "excused", DevWorkflowNodeRunStatus.Skipped),
                WorkNodeRun(3, "broken", DevWorkflowNodeRunStatus.Failed),
                WorkNodeRun(4, "cascaded", DevWorkflowNodeRunStatus.Skipped),
                WorkNodeRun(5, "join", DevWorkflowNodeRunStatus.Pending) with { NodeType = DevWorkflowNodeType.Join }
            ],
            PendingDecisionCount: 0,
            BlockingGateNodeRunId: null));
        await using var factory = EnabledFactory(Store(), runs);

        using var response = await SendAsync(factory, "GET", Run).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(body);
        var nodes = document.RootElement.GetProperty("nodes")
                            .EnumerateArray()
                            .ToDictionary(static node => node.GetProperty("nodeKey").GetString()!, static node => node.GetProperty("skipWaived"));

        AssertEx.True(nodes["excused"].GetBoolean(), "an operator skipped it with nothing dead behind it, so the join carries on past it.");
        AssertEx.False(nodes["cascaded"].GetBoolean(), "it cascaded off a Failed ancestor, so the join will skip with it.");
        AssertEx.Equal(JsonValueKind.Null, nodes["survey"].ValueKind, "the question only means something for a skipped row.");
    }

    /// <summary>One work node run of the run under test, distinguished only by its key and status.</summary>
    private static DevWorkflowNodeRunSnapshot WorkNodeRun(int ordinal, string nodeKey, DevWorkflowNodeRunStatus status) =>
        GateNodeRun() with
        {
            Id = Guid.Parse($"bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbb{ordinal}"),
            NodeKey = nodeKey,
            NodeType = DevWorkflowNodeType.Agent,
            Status = status,
            PendingDecisionKind = null,
            Sequence = ordinal
        };

    private static IDevWorkflowRunService RunService(DevWorkflowRunDetail? detail = null)
    {
        var runs = Substitute.For<IDevWorkflowRunService>();
        var composed = detail ?? Detail();
        runs.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(composed);
        runs.StartAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(composed);
        runs.PauseAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(composed);
        runs.ResumeAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(composed);
        runs.CancelAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(composed);
        runs.DecideAsync(Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<Guid>(),
                Arg.Any<DevWorkflowDecisionKind>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(call => new DevWorkflowDecisionResult(composed,
                new DevWorkflowDecisionSnapshot(Guid.NewGuid(),
                    RunId,
                    GateNodeRunId,
                    Attempt: 1,
                    call.ArgAt<DevWorkflowDecisionKind>(3),
                    call.ArgAt<string?>(4),
                    call.ArgAt<string?>(5),
                    call.ArgAt<string?>(6),
                    call.ArgAt<Guid>(2),
                    Sequence: 21,
                    DecidedAtUtc: 99)));
        return runs;
    }

    private static IDevWorkflowStore Store()
    {
        var store = Substitute.For<IDevWorkflowStore>();
        store.GetRunAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(RunSnapshot());
        store.GetNodeRunAsync(GateNodeRunId, Arg.Any<CancellationToken>()).Returns(GateNodeRun());
        store.ListRunSummariesAsync(Arg.Any<Guid?>(), Arg.Any<DevWorkflowRunStatus?>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        store.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        store.ListArtifactsAsync(Arg.Any<Guid>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns([Artifact()]);
        store.ListConsumedArtifactIdsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        store.ListDecisionsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        store.ListDefinitionsAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
             .Returns([
                 new DevWorkflowDefinitionSummary(DefinitionId,
                     "Research → Plan → Approval",
                     "graph-hash",
                     NodeCount: 2,
                     DevWorkflowDefinitionSource.Seeded,
                     "research-plan-approval",
                     Archived: false,
                     Version: 1,
                     CreatedAtUtc: 1,
                     UpdatedAtUtc: 2)
             ]);
        store.GetArtifactAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Artifact());
        return store;
    }

    private static DevWorkflowRunDetail Detail(DevWorkflowNodeRunStatus gateStatus = DevWorkflowNodeRunStatus.WaitingForApproval,
        DevWorkflowNodeRunStatus researchStatus = DevWorkflowNodeRunStatus.Succeeded,
        bool workSessionAvailable = true)
    {
        var gate = GateNodeRun(workSessionAvailable) with
        {
            Status = gateStatus,
            PendingDecisionKind = gateStatus == DevWorkflowNodeRunStatus.WaitingForApproval ? DevWorkflowDecisionKind.Approve : null
        };
        var research = new DevWorkflowNodeRunSnapshot(ResearchNodeRunId,
            RunId,
            "research",
            DevWorkflowNodeType.Agent,
            Attempt: 1,
            MaxAttempts: 3,
            SessionResumes: 0,
            researchStatus,
            QueueReason: null,
            PendingDecisionKind: null,
            Sequence: 2,
            WorkSessionId: null,
            WorkSessionAvailable: false,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: """{"workItemRequest":"Research and plan it."}""",
            OutputJson: null,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: 12,
            EndedAtUtc: 13,
            CreatedAtUtc: 10);

        var waiting = gateStatus is DevWorkflowNodeRunStatus.WaitingForApproval or DevWorkflowNodeRunStatus.Blocked;
        return new DevWorkflowRunDetail(RunSnapshot(), [research, gate], waiting ? 1 : 0, waiting ? GateNodeRunId : null);
    }

    private static DevWorkflowRunSnapshot RunSnapshot() =>
        new(RunId,
            WorkItemId,
            DefinitionId,
            DefinitionVersion: 4,
            "graph-hash",
            SampleGraph,
            GraphRevision: 0,
            DevWorkflowRunStatus.WaitingForApproval,
            LastSequence: 14,
            FailureClass: null,
            TerminalReason: null,
            StartedAtUtc: 11,
            EndedAtUtc: null,
            CreatedAtUtc: 10,
            UpdatedAtUtc: 20,
            Version: 6);

    private static DevWorkflowNodeRunSnapshot GateNodeRun(bool workSessionAvailable = true) =>
        new(GateNodeRunId,
            RunId,
            "approval",
            DevWorkflowNodeType.HumanGate,
            Attempt: 1,
            MaxAttempts: 1,
            SessionResumes: 0,
            DevWorkflowNodeRunStatus.WaitingForApproval,
            QueueReason: null,
            DevWorkflowDecisionKind.Approve,
            Sequence: 5,
            SessionId,
            workSessionAvailable,
            AgentDefinitionId: null,
            DevelopmentProjectId: null,
            DevelopmentTaskId: null,
            InputJson: null,
            OutputJson: null,
            PolicyResolutionJson: null,
            MaterializedFromNodeRunId: null,
            MaterializationIndex: null,
            FailureClass: null,
            TerminalReason: null,
            QueuedAtUtc: null,
            StartedAtUtc: 14,
            EndedAtUtc: null,
            CreatedAtUtc: 10);

    private static DevWorkflowArtifactSnapshot Artifact(string mediaType = "text/markdown", long sizeBytes = 10) =>
        new(ArtifactId,
            RunId,
            LineageId: Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "approval",
            GateNodeRunId,
            "plan.md",
            Version: 1,
            IsLatest: true,
            DevWorkflowArtifactKind.Plan,
            mediaType,
            "sha",
            sizeBytes,
            IsValid: true,
            IsStale: false,
            StaleSinceSequence: null,
            StaleBecauseArtifactId: null,
            StaleReason: null,
            "dev-workflows/33333333/55555555.bin",
            Sequence: 6,
            CreatedAtUtc: 15);

    private static DevWorkflowRunEventSnapshot Event(long sequence) =>
        new(Guid.NewGuid(), RunId, NodeRunId: null, sequence, "node.started", DetailJson: null, OperationId: null, Outcome: null, OccurredAtUtc: 100);

    private static async Task<HttpResponseMessage> SendAsync(TestServerWebAppFactory factory, string method, string route, string? body = null)
    {
        using var client = factory.CreateClient();
        using var request = Request(method, route, body);
        factory.AddNodeBearerToken(request);
        return await client.SendAsync(request).ConfigureAwait(false);
    }

    private static HttpRequestMessage Request(string method, string route, string? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), route);
        if (method is "POST" or "PATCH" or "PUT")
        {
            request.Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static TestServerWebAppFactory EnabledFactory(IDevWorkflowStore store,
        IDevWorkflowRunService runs,
        IAgentWorkSessionStore? sessions = null,
        IDevWorkflowArtifactBlobStore? blobs = null,
        params (string Key, string? Value)[] configuration)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DevWorkflows:Enabled"] = "true"
        };
        foreach (var (key, value) in configuration)
        {
            settings[key] = value;
        }

        var sessionStore = sessions ?? Substitute.For<IAgentWorkSessionStore>();
        sessionStore.GetAsync(SessionId, Arg.Any<CancellationToken>())
                    .Returns(new AgentWorkSessionSnapshot(SessionId,
                        "Research",
                        "objective",
                        AgentWorkSessionKind.Workflow,
                        AgentWorkSessionStatus.Completed,
                        Guid.NewGuid(),
                        ConversationId,
                        CurrentTaskId: null,
                        StepCount: 3,
                        LastCheckpointId: null,
                        LastSequence: 9,
                        ConfigVersion: 1,
                        CreatedAtUtc: 1,
                        UpdatedAtUtc: 2,
                        Version: 3));

        return new TestServerWebAppFactory
        {
            AdditionalConfiguration = settings,
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<IDevWorkflowStore>();
                services.AddSingleton(store);
                services.RemoveAll<IDevWorkflowRunService>();
                services.AddSingleton(runs);
                services.RemoveAll<IAgentWorkSessionStore>();
                services.AddSingleton(sessionStore);
                if (blobs is not null)
                {
                    services.RemoveAll<IDevWorkflowArtifactBlobStore>();
                    services.AddSingleton(blobs);
                }
            }
        };
    }
}
