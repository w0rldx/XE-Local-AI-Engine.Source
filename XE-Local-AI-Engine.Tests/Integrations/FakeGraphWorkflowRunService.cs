namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     One scripted graph run for the integration coordinator's graph branch: what the start records, the status and events
///     each poll reads, and what a cancel does. Only the members that branch calls are implemented.
/// </summary>
internal sealed class FakeGraphWorkflowRunService : IGraphWorkflowRunService
{
    private readonly List<GraphWorkflowRunEventSnapshot> _events = [];

    public Guid RunId { get; } = Guid.NewGuid();

    /// <summary>The node runs the start answers with, keyed by node key.</summary>
    public Dictionary<string, GraphWorkflowNodeKind> Nodes { get; } = new(StringComparer.Ordinal)
    {
        ["start"] = GraphWorkflowNodeKind.Start,
        ["lights"] = GraphWorkflowNodeKind.Tool,
        ["end"] = GraphWorkflowNodeKind.End
    };

    public GraphWorkflowRunStatus Status { get; set; } = GraphWorkflowRunStatus.Running;

    public GraphWorkflowFailureClass FailureClass { get; set; } = GraphWorkflowFailureClass.None;

    public string? OutputJson { get; set; }

    /// <summary>Thrown by the start instead of answering, as the definition's trigger-source refusal does.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Runs on every status read, so a test can move the run or signal a cancel between polls.</summary>
    public Action<FakeGraphWorkflowRunService>? OnPoll { get; set; }

    public int StartCount { get; private set; }

    public Guid? StartedDefinitionId { get; private set; }

    public Guid? StartedRequestId { get; private set; }

    public string? StartedInputJson { get; private set; }

    public GraphWorkflowRunOrigin? StartedOrigin { get; private set; }

    public int CancelCount { get; private set; }

    /// <summary>Appends one event at the next sequence.</summary>
    public void Emit(string eventType, string? nodeKey) =>
        _events.Add(new GraphWorkflowRunEventSnapshot
        {
            Id = Guid.NewGuid(),
            RunId = RunId,
            Seq = _events.Count + 1,
            EventType = eventType,
            NodeKey = nodeKey,
            DetailJson = null,
            CreatedAtUtc = 0
        });

    public Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        GraphWorkflowRunOrigin origin,
        CancellationToken cancellationToken = default)
    {
        StartCount++;
        if (StartFailure is { } failure)
        {
            return Task.FromException<GraphWorkflowRunDetail>(failure);
        }

        StartedDefinitionId = definitionId;
        StartedRequestId = requestId;
        StartedInputJson = inputJson;
        StartedOrigin = origin;
        return Task.FromResult(Detail());
    }

    public Task<GraphWorkflowRunDetail> StartAsync(Guid definitionId,
        Guid requestId,
        string? inputJson,
        int? definitionVersion,
        GraphWorkflowRunOrigin origin,
        GraphWorkflowRunBinding? binding,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The integration branch starts unbound runs only.");

    public Task<GraphWorkflowRunDetail> CancelAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        CancelCount++;
        if (Status is GraphWorkflowRunStatus.Completed or GraphWorkflowRunStatus.Failed or GraphWorkflowRunStatus.Cancelled)
        {
            // As the real service answers a terminal run.
            return Task.FromException<GraphWorkflowRunDetail>(new GraphWorkflowRunConflictException($"This run is already {Status}, so there is nothing to cancel."));
        }

        Status = GraphWorkflowRunStatus.Cancelled;
        Emit(GraphWorkflowEventTypes.NodeCancelled, "lights");
        Emit(GraphWorkflowEventTypes.RunCancelled, nodeKey: null);
        return Task.FromResult(Detail());
    }

    public Task<GraphWorkflowRunDetail> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        OnPoll?.Invoke(this);
        return Task.FromResult(Detail());
    }

    public Task<GraphWorkflowRunEventPage> ListEventsAsync(Guid runId, long afterSeq, CancellationToken cancellationToken = default)
    {
        var page = _events.Where(runEvent => runEvent.Seq > afterSeq).ToArray();
        return Task.FromResult(new GraphWorkflowRunEventPage
        {
            Events = page,
            LastSeq = page.Length == 0 ? afterSeq : page[^1].Seq,
            ReplayTruncated = false
        });
    }

    public Task DeleteAsync(Guid runId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<GraphWorkflowRunSnapshot>> ListRunsAsync(GraphWorkflowRunStatus? status, int limit, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowNodeRunSnapshot> GetNodeRunAsync(Guid runId, string nodeKey, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowDecisionResult> DecideAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        GraphWorkflowDecisionKind decision,
        string? comment,
        string? payloadJson,
        string? decidedBySubject,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<GraphWorkflowRunDetail> SteerAsync(Guid runId,
        string nodeKey,
        Guid operationId,
        string message,
        string? steeredBySubject,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    private GraphWorkflowRunDetail Detail() =>
        new()
        {
            Run = new GraphWorkflowRunSnapshot
            {
                Id = RunId,
                RequestId = StartedRequestId ?? Guid.Empty,
                DefinitionId = StartedDefinitionId ?? Guid.Empty,
                DefinitionVersion = 1,
                GraphHash = "hash",
                Status = Status,
                FailureClass = FailureClass,
                GraphJson = "{}",
                InputJson = StartedInputJson,
                OutputJson = OutputJson,
                Seq = _events.Count,
                Version = 1,
                CancelRequestedAtUtc = null,
                StartedAtUtc = 1_000,
                CompletedAtUtc = Status is GraphWorkflowRunStatus.Completed or GraphWorkflowRunStatus.Failed or GraphWorkflowRunStatus.Cancelled ? 1_250 : null,
                CreatedAtUtc = 1_000
            },
            NodeRuns =
            [
                .. Nodes.Select(node => new GraphWorkflowNodeRunSnapshot
                {
                    Id = Guid.NewGuid(),
                    RunId = RunId,
                    NodeKey = node.Key,
                    Kind = node.Value,
                    Status = GraphWorkflowNodeRunStatus.Pending,
                    Attempt = 1,
                    PendingDecisionKind = null,
                    DecisionOperationId = null,
                    DecidedBySubject = null,
                    FailureClass = GraphWorkflowFailureClass.None,
                    Error = null,
                    InputJson = null,
                    OutputJson = null,
                    InvocationId = null,
                    StartedAtUtc = null,
                    CompletedAtUtc = null,
                    UpdatedAtUtc = 0
                })
            ]
        };
}
