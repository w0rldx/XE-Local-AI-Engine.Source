namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The <c>DevTask</c> contract: its two configs, <c>DEVTASK-1</c> at save AND at run start, and the start binding to a
///     Development project. An <c>apply</c> changes a real repository, so every way of reaching one without a fresh approval is a row here.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowDevTaskGraphTests
{
    private const string Approve = """{ "path": "output.decision", "op": "Eq", "value": "Approve" }""";
    private const string Reject = """{ "path": "output.decision", "op": "Eq", "value": "Reject" }""";
    private const string NotReject = """{ "path": "output.decision", "op": "Ne", "value": "Reject" }""";
    private const string StartNode = """{ "key": "start", "kind": "Start" }""";
    private const string Implement = """{ "key": "impl", "kind": "DevTask", "config": { "action": "implement", "requirements": "Fix the bug." } }""";

    [ClassDataSource<GraphWorkflowDevTaskHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowDevTaskHostFixture Host { get; init; }

    /// <summary>DEVTASK-1 as a table: each graph is accepted or refused with its own sentence, by the save AND by a start of the same graph stored past the save.</summary>
    [Test]
    [Arguments("approved", null)]
    [Arguments("approve-written-as-ne-reject", null)]
    [Arguments("fed-by-reject", "edge 'e3' ('review' → 'apply') does not fire on Approve")]
    [Arguments("unconditional-from-pause", "edge 'e3' ('review' → 'apply') also fires on Reject")]
    [Arguments("fed-by-a-non-pause", "edge 'e5' ('fan' → 'apply') reaches it from a node that is not a Pause")]
    [Arguments("two-inbound-one-ungated", "edge 'e6' ('impl' → 'apply') reaches it from a node that is not a Pause")]
    [Arguments("pause-before-the-implement", "the Pause 'review' in front of it does not come after 'impl'")]
    [Arguments("implement-on-a-sibling-branch", "the Pause 'review' in front of it does not come after 'impl'")]
    [Arguments("names-a-missing-node", "applies the task of 'ghost', which is not a DevTask 'implement' node")]
    [Arguments("names-a-non-devtask", "applies the task of 'review', which is not a DevTask 'implement' node")]
    [Arguments("names-another-apply", "applies the task of 'apply', which is not a DevTask 'implement' node")]
    [Arguments("in-a-chat-graph", "is a DevTask node, which a Chat graph cannot carry")]
    public async Task DevTask1_IsEnforcedAtSaveAndAtStart(string row, string? refusal)
    {
        var graph = Row(row);
        var projectId = Host.BindProject();

        if (refusal is null)
        {
            var saved = await SaveAsync(graph);
            var run = await StartAsync(saved, Input(projectId));
            AssertEx.Equal(GraphWorkflowRunStatus.Pending, run.Run.Status);
            AssertEx.Empty(GraphWorkflowGraph.Parse(graph).Warnings.Where(static warning => warning.Key == "apply"),
                "an apply reads its task by key, so the pause-context advice (an edge DEVTASK-1 refuses) is never given for it.");
            return;
        }

        var atSave = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => SaveAsync(graph));
        AssertEx.Contains(atSave.Message, refusal);

        var stored = await StoreUnvalidatedAsync(graph, row == "in-a-chat-graph" ? GraphWorkflowDefinitionKind.Chat : GraphWorkflowDefinitionKind.Standard);
        var requestId = Guid.NewGuid();
        var atStart = await AssertEx.ThrowsAsync<GraphWorkflowValidationException>(() => StartAsync(stored, Input(projectId), requestId));
        AssertEx.Contains(atStart.Message, refusal);
        AssertEx.Null(await FindRunAsync(requestId), "a refused start writes no run.");
    }

    /// <summary>A valid DevTask graph starts only bound to a resolvable Development project; a refused start writes no run and never names the host path.</summary>
    [Test]
    [Arguments("no-input", "its run input needs 'developmentProjectId'")]
    [Arguments("not-a-guid", "its run input needs 'developmentProjectId'")]
    [Arguments("unknown-project", "names no Development project on this node")]
    [Arguments("unsafe-binding", "repository cannot be resolved. Reconnect it on the Development page.")]
    public async Task StartAsync_RefusesADevTaskRunWithoutAResolvableProject(string reason, string expected)
    {
        var definitionId = await SaveAsync(Row("approved"));
        var input = reason switch
        {
            "no-input" => null,
            "not-a-guid" => """{ "developmentProjectId": "not-a-guid" }""",
            "unknown-project" => Input(Guid.NewGuid()),
            _ => Input(Host.BindUnsafeProject())
        };
        var requestId = Guid.NewGuid();

        var refused = await AssertEx.ThrowsAsync<InvalidOperationException>(() => StartAsync(definitionId, input, requestId));

        AssertEx.Equal(reason == "unsafe-binding" ? nameof(GraphWorkflowRunConflictException) : nameof(GraphWorkflowValidationException), refused.GetType().Name,
            "an input the caller can fix is a 400; a binding only the operator can repair is a 409.");
        AssertEx.Contains(refused.Message, expected);
        AssertEx.False(refused.Message.Contains(GraphWorkflowDevTaskHostFixture.UnsafePath, StringComparison.Ordinal), "a binding failure never names the host path.");
        AssertEx.Null(await FindRunAsync(requestId), "a refused start writes no run.");
    }

    /// <summary>The Start node's <c>defaultInput</c> binds the project like an explicit input does.</summary>
    [Test]
    public async Task StartAsync_ReadsTheProjectFromTheStartDefaultInput()
    {
        var projectId = Host.BindProject();
        var graph = Row("approved").Replace(StartNode,
            $$"""{ "key": "start", "kind": "Start", "config": { "defaultInput": { "developmentProjectId": "{{projectId}}" } } }""",
            StringComparison.Ordinal);

        var run = await StartAsync(await SaveAsync(graph), inputJson: null);

        AssertEx.Equal(GraphWorkflowRunStatus.Pending, run.Run.Status);
    }

    /// <summary>Development Mode off at startup registers no binding service, so a DevTask graph cannot start there, while a graph without one still does.</summary>
    [Test]
    public async Task StartAsync_RefusesADevTaskRunWhileDevelopmentModeIsOff()
    {
        await using var host = GraphWorkflowDevTaskHostFixture.DevelopmentModeOff();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            AssertEx.Null(scope.ServiceProvider.GetService<IDevelopmentRepositoryBindingService>(), "the precondition: the module registered nothing.");
        }

        var devTask = await SaveAsync(host.Services, Row("approved"));
        var plain = await SaveAsync(host.Services, GraphWorkflowGraphs.InlineLinear);

        var refused = await AssertEx.ThrowsAsync<GraphWorkflowRunConflictException>(() => StartAsync(host.Services, devTask, Input(Guid.NewGuid()), Guid.NewGuid()));
        AssertEx.Contains(refused.Message, "Development Mode is off on this node");
        _ = await StartAsync(host.Services, plain, inputJson: null, Guid.NewGuid());
    }

    /// <summary>Both configs survive a save and reload, every member of the action read back.</summary>
    [Test]
    public async Task BothActions_SurviveASaveAndReload()
    {
        var graph = Row("approved").Replace(Implement,
            """
            { "key": "impl", "kind": "DevTask", "config": { "action": " implement ", "title": " Fix login ", "requirements": "Fix the login bug.",
              "acceptanceCriteria": ["Login works", " Tests pass "], "instructions": "Read the request first.",
              "inputBindings": { "request": "run.input.request" } } }
            """,
            StringComparison.Ordinal);

        var definitionId = await SaveAsync(graph);
        await using var scope = Host.Services.CreateAsyncScope();
        var reloaded = GraphWorkflowGraph.Parse((await scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>().GetAsync(definitionId)).GraphJson);

        var implement = AssertEx.NotNull(reloaded.Nodes["impl"].Config as GraphWorkflowDevTaskConfig);
        AssertEx.Equal(GraphWorkflowGraph.DevTaskImplement, implement.Action);
        AssertEx.False(implement.IsApply);
        AssertEx.Equal("Fix login", implement.Title);
        AssertEx.Equal("Fix the login bug.", implement.Requirements);
        AssertEx.Equal("Login works|Tests pass", string.Join("|", implement.AcceptanceCriteria));
        AssertEx.Equal("Read the request first.", implement.Instructions);
        AssertEx.Equal("run.input.request", implement.InputBindings["request"]);
        AssertEx.Null(implement.TaskNodeKey);

        var apply = AssertEx.NotNull(reloaded.Nodes["apply"].Config as GraphWorkflowDevTaskConfig);
        AssertEx.True(apply.IsApply);
        AssertEx.Equal("impl", apply.TaskNodeKey);
        AssertEx.Null(apply.Requirements);
        AssertEx.Equal(expected: 1, reloaded.Nodes["apply"].MaxAttempts, "a DevTask node gets one attempt unless it asks for more.");
    }

    /// <summary>Each config refusal names the member, so an author is told what to remove rather than that the graph is wrong.</summary>
    [Test]
    [Arguments("""{ "action": "implement" }""", "needs a non-empty 'requirements'")]
    [Arguments("""{ "requirements": "Go." }""", "needs a non-empty 'action'")]
    [Arguments("""{ "action": "merge", "requirements": "Go." }""", "has the DevTask action 'merge'; expected 'implement' or 'apply'")]
    [Arguments("""{ "action": "implement", "requirements": "Go.", "task": "impl" }""", "is a DevTask 'implement' node and its config declares 'task'")]
    [Arguments("""{ "action": "apply", "task": "impl", "requirements": "Go." }""", "is a DevTask 'apply' node and its config declares 'requirements'")]
    [Arguments("""{ "action": "apply" }""", "needs a non-empty 'task'")]
    [Arguments("""{ "action": "implement", "requirements": "Go.", "acceptanceCriteria": ["ok", 3] }""", "'acceptanceCriteria' on node 'node' must be an array of non-empty strings")]
    [Arguments("""{ "action": "implement", "requirements": "Go.", "toolName": "read_file" }""", "which no DevTask node reads")]
    public void Parse_RefusesAMisshapenDevTaskConfig(string config, string expected)
    {
        var graph = $$"""
                      { "schemaVersion": 1,
                        "nodes": [{{StartNode}}, { "key": "node", "kind": "DevTask", "config": {{config}} }, { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                        "edges": [{ "key": "e1", "from": "start", "to": "node" }, { "key": "e2", "from": "node", "to": "done" }] }
                      """;

        var refusal = AssertEx.Throws<GraphWorkflowValidationException>(() => GraphWorkflowGraph.Parse(graph));

        AssertEx.Contains(refusal.Message, expected);
    }

    /// <summary>An implement node's bindings are held to the semantic lint's <c>upstream</c> rule like every other binding.</summary>
    [Test]
    public void SemanticLint_RefusesAnImplementBindingToANodeWithNoEdgeIntoIt()
    {
        var graph = GraphWorkflowGraph.Parse(Row("approved").Replace(Implement,
            """{ "key": "impl", "kind": "DevTask", "config": { "action": "implement", "requirements": "Go.", "inputBindings": { "plan": "upstream.review.output" } } }""",
            StringComparison.Ordinal));

        AssertEx.Contains(string.Join(" | ", graph.SemanticLintErrors), "Node 'impl' reads 'upstream.review.output' in inputBindings.plan, but 'review' has no edge into 'impl'");
    }

    private static string Row(string row) =>
        row switch
        {
            "approved" => Standard(Approve),
            "approve-written-as-ne-reject" => Standard(NotReject),
            "fed-by-reject" => Graph([StartNode, Implement, Pause("review"), Apply("impl"), End("shipped"), End("declined")],
                [Edge("e1", "start", "impl"), Edge("e2", "impl", "review"), Edge("e3", "review", "apply", Reject), Edge("e4", "review", "declined", Approve), Edge("e5", "apply", "shipped")]),
            "unconditional-from-pause" => Graph([StartNode, Implement, Pause("review"), Apply("impl"), End("shipped"), End("declined")],
                [Edge("e1", "start", "impl"), Edge("e2", "impl", "review"), Edge("e3", "review", "apply"), Edge("e4", "review", "declined", Reject), Edge("e5", "apply", "shipped")]),
            "fed-by-a-non-pause" => Graph([StartNode, Implement, Pause("review"), """{ "key": "fan", "kind": "Parallel" }""", Apply("impl"), End("shipped"), End("declined")],
                [Edge("e1", "start", "impl"), Edge("e2", "impl", "review"), Edge("e3", "review", "fan", Approve), Edge("e4", "review", "declined", Reject), Edge("e5", "fan", "apply"), Edge("e6", "apply", "shipped")]),
            "two-inbound-one-ungated" => Graph([StartNode, Implement, Pause("review"), Apply("impl"), End("shipped"), End("declined")],
                [Edge("e1", "start", "impl"), Edge("e2", "impl", "review"), Edge("e3", "review", "apply", Approve), Edge("e4", "review", "declined", Reject), Edge("e5", "apply", "shipped"), Edge("e6", "impl", "apply")]),
            "pause-before-the-implement" => Graph([StartNode, Pause("review"), Implement, Apply("impl"), End("shipped"), End("declined"), End("built")],
                [Edge("e1", "start", "review"), Edge("e2", "review", "impl", Approve), Edge("e3", "review", "apply", Approve), Edge("e4", "review", "declined", Reject), Edge("e5", "apply", "shipped"), Edge("e6", "impl", "built")]),
            "implement-on-a-sibling-branch" => Graph([StartNode, Implement, Pause("review"), Apply("impl"), End("shipped"), End("declined"), End("built")],
                [Edge("e1", "start", "impl"), Edge("e2", "start", "review"), Edge("e3", "review", "apply", Approve), Edge("e4", "review", "declined", Reject), Edge("e5", "apply", "shipped"), Edge("e6", "impl", "built")]),
            "names-a-missing-node" => Standard(Approve, task: "ghost"),
            "names-a-non-devtask" => Standard(Approve, task: "review"),
            "names-another-apply" => Graph([StartNode, Implement, Pause("review"), Apply("impl"), Apply("apply", key: "apply2"), End("shipped"), End("shipped2"), End("declined")],
                [Edge("e1", "start", "impl"), Edge("e2", "impl", "review"), Edge("e3", "review", "apply", Approve), Edge("e4", "review", "declined", Reject), Edge("e5", "apply", "shipped"),
                    Edge("e6", "review", "apply2", Approve), Edge("e7", "apply2", "shipped2")]),
            "in-a-chat-graph" => Standard(Approve, kind: "Chat"),
            _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row")
        };

    /// <summary>implement → review → (condition) apply → shipped, with Reject → declined: the shape DEVTASK-1 exists to allow.</summary>
    private static string Standard(string applyCondition, string task = "impl", string kind = "Standard") =>
        Graph([StartNode, Implement, Pause("review"), Apply(task), End("shipped"), End("declined")],
            [Edge("e1", "start", "impl"), Edge("e2", "impl", "review"), Edge("e3", "review", "apply", applyCondition), Edge("e4", "review", "declined", Reject), Edge("e5", "apply", "shipped")],
            kind);

    private static string Graph(string[] nodes, string[] edges, string kind = "Standard") =>
        $$"""{ "schemaVersion": 1, "kind": "{{kind}}", "nodes": [{{string.Join(", ", nodes)}}], "edges": [{{string.Join(", ", edges)}}] }""";

    private static string Pause(string key) =>
        $$"""{ "key": "{{key}}", "kind": "Pause", "config": { "prompt": "Apply the patch?", "allowedDecisions": ["Approve", "Reject"] } }""";

    private static string Apply(string task, string key = "apply") =>
        $$"""{ "key": "{{key}}", "kind": "DevTask", "config": { "action": "apply", "task": "{{task}}" } }""";

    private static string End(string key) =>
        $$"""{ "key": "{{key}}", "kind": "End", "config": { "outcome": "{{key}}" } }""";

    private static string Edge(string key, string from, string to, string? condition = null) =>
        condition is null
            ? $$"""{ "key": "{{key}}", "from": "{{from}}", "to": "{{to}}" }"""
            : $$"""{ "key": "{{key}}", "from": "{{from}}", "to": "{{to}}", "condition": {{condition}} }""";

    private static string Input(Guid projectId) =>
        $$"""{ "developmentProjectId": "{{projectId}}" }""";

    private Task<Guid> SaveAsync(string graphJson) =>
        SaveAsync(Host.Services, graphJson);

    private static async Task<Guid> SaveAsync(IServiceProvider services, string graphJson)
    {
        await using var scope = services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>().CreateAsync($"DevTask {Guid.NewGuid():N}", description: null, graphJson)).Id;
    }

    /// <summary>Written straight through the store, past the save-time validation, so only the start's own re-parse stands between it and a run.</summary>
    private async Task<Guid> StoreUnvalidatedAsync(string graphJson, GraphWorkflowDefinitionKind kind)
    {
        using var document = JsonDocument.Parse(graphJson);
        await using var scope = Host.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().CreateDefinitionAsync(new CreateGraphWorkflowDefinitionCommand
        {
            DefinitionId = Guid.NewGuid(),
            Name = $"Stored past the save {Guid.NewGuid():N}",
            GraphJson = graphJson,
            NodeCount = document.RootElement.GetProperty("nodes").GetArrayLength(),
            Kind = kind
        })).Id;
    }

    private Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId, string? inputJson, Guid requestId = default) =>
        StartAsync(Host.Services, definitionId, inputJson, requestId == Guid.Empty ? Guid.NewGuid() : requestId);

    private static async Task<GraphWorkflowRunDetail> StartAsync(IServiceProvider services, Guid definitionId, string? inputJson, Guid requestId)
    {
        await using var scope = services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowRunService>()
                          .StartAsync(definitionId, requestId, inputJson, definitionVersion: null, GraphWorkflowRunOrigin.Manual);
    }

    private async Task<GraphWorkflowRunSnapshot?> FindRunAsync(Guid requestId)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IGraphWorkflowStore>().FindRunByRequestAsync(requestId);
    }
}

/// <summary>The graph-workflow host with Development Mode on and the repository bindings answered from projects a test binds.</summary>
public sealed class GraphWorkflowDevTaskHostFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The host path an unsafe binding's exception names, which no refusal may repeat.</summary>
    public const string UnsafePath = "/srv/private/unsafe-devtask-repository";

    private readonly HashSet<Guid> _projects = [];
    private readonly HashSet<Guid> _unsafe = [];
    private readonly Lock _gate = new();
    private readonly TestServerWebAppFactory _factory;

    public GraphWorkflowDevTaskHostFixture() =>
        _factory = GraphWorkflowHostFixture.NewFactory(Configure);

    private GraphWorkflowDevTaskHostFixture(TestServerWebAppFactory factory) =>
        _factory = factory;

    public IServiceProvider Services => _factory.Services;

    /// <summary>A host of its own started with Development Mode off, so no binding service is registered.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership transfers to the returned fixture, which disposes it.")]
    public static GraphWorkflowDevTaskHostFixture DevelopmentModeOff() =>
        new(new TestServerWebAppFactory
        {
            EnableDevelopmentMode = false,
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
            },
            ConfigureAdditionalTestServices = static services =>
            {
                services.RemoveAll<IGraphWorkflowDispatcherSignal>();
                services.AddSingleton<IGraphWorkflowDispatcherSignal, RecordingGraphWorkflowDispatcherSignal>();
            }
        });

    public Guid BindProject()
    {
        var projectId = Guid.NewGuid();
        lock (_gate)
        {
            _ = _projects.Add(projectId);
        }

        return projectId;
    }

    public Guid BindUnsafeProject()
    {
        var projectId = Guid.NewGuid();
        lock (_gate)
        {
            _ = _unsafe.Add(projectId);
        }

        return projectId;
    }

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        _factory.DisposeAsync();

    private void Configure(IServiceCollection services)
    {
        var bindings = Substitute.For<IDevelopmentRepositoryBindingService>();
        bindings.ResolveProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => Binding(call.ArgAt<Guid>(0)));
        services.RemoveAll<IDevelopmentRepositoryBindingService>();
        services.AddSingleton(bindings);
    }

    private DevelopmentRepositoryBinding Binding(Guid projectId)
    {
        lock (_gate)
        {
            if (_unsafe.Contains(projectId))
            {
                throw new DevelopmentWorkspaceSecurityException($"The repository at '{UnsafePath}' is not a safe workspace.");
            }

            return _projects.Contains(projectId)
                ? new DevelopmentRepositoryBinding
                {
                    ProjectId = projectId,
                    SelectedFolderId = Guid.NewGuid(),
                    Alias = "repository",
                    RepositoryRoot = Path.GetTempPath(),
                    RepositoryIdentityHash = "identity"
                }
                : throw new DevelopmentNotFoundException($"Development project '{projectId}' was not found.");
        }
    }
}
