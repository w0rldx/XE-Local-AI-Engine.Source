namespace XE_Local_AI_Engine.Tests.Endpoints.DevelopmentWorkflows.V1;

using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The static mapper over an already-composed run view: it labels each node off the run's pinned wire graph and
///     carries every derived field through as the service computed it, re-deriving none.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class DevWorkflowRunResponseMapperTests
{
    private static readonly Guid RunId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid NodeRunId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string GraphJson =
        """
        {"schemaVersion":1,"nodes":[{"nodeKey":"research","nodeType":"Agent","label":"Research"},
        {"nodeKey":"approval","nodeType":"HumanGate","label":"Approve"}],
        "edges":[{"from":"research","to":"approval"}]}
        """;

    [Test]
    public void ToResponse_LabelsOffThePinnedGraphAndCarriesTheDerivedFieldsThrough()
    {
        var view = new DevWorkflowRunView
        {
            Run = Run(),
            DefinitionName = "Ship it",
            Nodes =
            [
                new DevWorkflowNodeRunView
                {
                    NodeRun = NodeRun(),
                    WaitingOnNodeKeys = ["research"],
                    MaterializedFromNodeKey = null,
                    MaterializationCount = null,
                    AgentDisplayName = "Researcher",
                    ModelLabel = "qwen",
                    HasStaleInputs = true,
                    OperatorRetries = 2,
                    SkipWaived = null,
                    ValidationNotApplicable = false
                }
            ],
            QueuedNodeCount = 1,
            RunningNodeCount = 0,
            PendingDecisionCount = 3,
            BlockingGateNodeRunId = NodeRunId,
            Cost = new DevWorkflowRunCost
            {
                InputTokens = 35,
                OutputTokens = null,
                ToolCalls = 2,
                ProviderCalls = null,
                AgentTurnMs = 9
            }
        };

        var response = view.ToResponse();

        var node = response.Nodes.Single();
        AssertEx.Equal("Approve", node.Label, "the label comes off the run's pinned graph, not off the node key.");
        AssertEx.Equal("research", node.WaitingOnNodeKeys!.Single());
        AssertEx.True(node.HasStaleInputs);
        AssertEx.Equal(expected: 2, node.OperatorRetries);
        AssertEx.Equal("Researcher", node.AgentDisplayName);
        AssertEx.Equal("Ship it", response.DefinitionName);
        AssertEx.Equal(expected: 3, response.PendingDecisionCount);
        AssertEx.Equal(expected: 2, response.Graph.Nodes.Count);
        AssertEx.Equal(expected: 35L, response.Cost.InputTokens!.Value);
        AssertEx.Null(response.Cost.OutputTokens, "a member nobody measured stays null on the wire too.");
    }

    private static DevWorkflowRunSnapshot Run() =>
        new()
        {
            Id = RunId,
            WorkItemId = Guid.NewGuid(),
            DefinitionId = Guid.NewGuid(),
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

    private static DevWorkflowNodeRunSnapshot NodeRun() =>
        new()
        {
            Id = NodeRunId,
            RunId = RunId,
            NodeKey = "approval",
            NodeType = DevWorkflowNodeType.HumanGate,
            Attempt = 1,
            MaxAttempts = 3,
            SessionResumes = 0,
            Status = DevWorkflowNodeRunStatus.Pending,
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
}
