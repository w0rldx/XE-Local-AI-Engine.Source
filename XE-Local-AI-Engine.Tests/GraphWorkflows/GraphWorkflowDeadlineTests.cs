namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Node deadlines, derived from the ROW rather than armed in memory — which is what a restart and a re-attempt both
///     rely on. Pure functions over a snapshot and a clock, so none of this needs a host.
/// </summary>
public sealed class GraphWorkflowDeadlineTests
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

    /// <summary>A node with a five-second budget, and one with none of its own so the default answers for it.</summary>
    private const string ImpatientGraph = """
                                          {
                                            "schemaVersion": 1,
                                            "nodes": [
                                              { "key": "start", "kind": "Start" },
                                              { "key": "impatient", "kind": "Agent", "timeoutSeconds": 5, "config": { "instructions": "Be quick." } },
                                              { "key": "patient", "kind": "Agent", "config": { "instructions": "Take your time." } },
                                              { "key": "done", "kind": "End", "joinPolicy": "Any", "config": { "outcome": "completed" } }
                                            ],
                                            "edges": [
                                              { "key": "e1", "from": "start", "to": "impatient" },
                                              { "key": "e2", "from": "start", "to": "patient" },
                                              { "key": "e3", "from": "impatient", "to": "done" },
                                              { "key": "e4", "from": "patient", "to": "done" }
                                            ]
                                          }
                                          """;

    [Test]
    public void HasExpired_WhenTheNodesOwnBudgetAndTheGraceHaveBothPassed_IsTrue()
    {
        var clock = new ManualTimeProvider(StartedAt.AddSeconds(5).Add(GraphWorkflowDeadline.Grace).AddSeconds(1));

        AssertEx.True(GraphWorkflowDeadline.HasExpired(Node("impatient"), Running(), Options(), clock));
    }

    /// <summary>
    ///     The grace is not decoration: the lane bounds its own turn by the same number counted from a moment slightly
    ///     later, so ending the row the instant the budget is reached would race the real answer and sometimes win.
    /// </summary>
    [Test]
    public void HasExpired_WhileOnlyTheGraceIsLeft_IsFalseSoTheLaneCanStillAnswer()
    {
        var clock = new ManualTimeProvider(StartedAt.AddSeconds(5).AddSeconds(1));

        AssertEx.False(GraphWorkflowDeadline.HasExpired(Node("impatient"), Running(), Options(), clock));
    }

    [Test]
    public void HasExpired_BeforeTheBudgetIsSpent_IsFalse()
    {
        var clock = new ManualTimeProvider(StartedAt.AddSeconds(1));

        AssertEx.False(GraphWorkflowDeadline.HasExpired(Node("impatient"), Running(), Options(), clock));
    }

    /// <summary>
    ///     A row that never started has no deadline at all. That is what leaves a re-attempt and a restart collapse
    ///     nothing to expire: both clear the start instant, so the next admission stamps a fresh one.
    /// </summary>
    [Test]
    public void HasExpired_ForARowThatNeverStarted_IsFalseHoweverLongTheOutage()
    {
        var clock = new ManualTimeProvider(StartedAt.AddDays(1));

        AssertEx.False(GraphWorkflowDeadline.HasExpired(Node("impatient"), Running(started: false), Options(), clock));
        AssertEx.Null(GraphWorkflowDeadline.Expiry(Node("impatient"), Running(started: false), Options()));
    }

    /// <summary>
    ///     Unlike the development-workflow original, a node that declares no timeout still has one: graph workflows
    ///     carry a node-timeout default, and the author's own number only overrides it.
    /// </summary>
    [Test]
    public void HasExpired_ForANodeThatDeclaresNoTimeout_FallsBackToTheConfiguredDefault()
    {
        var options = Options(defaultNodeTimeoutSeconds: 30);

        AssertEx.False(GraphWorkflowDeadline.HasExpired(Node("patient"), Running(), options, new ManualTimeProvider(StartedAt.AddSeconds(29))));
        AssertEx.True(GraphWorkflowDeadline.HasExpired(Node("patient"),
            Running(),
            options,
            new ManualTimeProvider(StartedAt.AddSeconds(30).Add(GraphWorkflowDeadline.Grace).AddSeconds(1))));
    }

    [Test]
    public void Expiry_IsTheNodesOwnBudgetWhenItDeclaresOneAndTheDefaultWhenItDoesNot()
    {
        var options = Options(defaultNodeTimeoutSeconds: 600);

        AssertEx.Equal(StartedAt.AddSeconds(5), GraphWorkflowDeadline.Expiry(Node("impatient"), Running(), options));
        AssertEx.Equal(StartedAt.AddSeconds(600), GraphWorkflowDeadline.Expiry(Node("patient"), Running(), options));
    }

    private static GraphWorkflowGraphNode Node(string nodeKey) =>
        GraphWorkflowGraph.Parse(ImpatientGraph).Nodes[nodeKey];

    private static GraphWorkflowOptions Options(int defaultNodeTimeoutSeconds = 600) =>
        new()
        {
            Enabled = true,
            DefaultNodeTimeoutSeconds = defaultNodeTimeoutSeconds
        };

    private static GraphWorkflowNodeRunSnapshot Running(bool started = true) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            "impatient",
            GraphWorkflowNodeKind.Agent,
            GraphWorkflowNodeRunStatus.Running,
            Attempt: 1,
            PendingDecisionKind: null,
            DecisionOperationId: null,
            DecidedBySubject: null,
            GraphWorkflowFailureClass.None,
            Error: null,
            InputJson: null,
            OutputJson: null,
            InvocationId: null,
            started ? StartedAt.ToUnixTimeMilliseconds() : null,
            CompletedAtUtc: null,
            UpdatedAtUtc: 0);
}
