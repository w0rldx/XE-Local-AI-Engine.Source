namespace XE_Local_AI_Engine.AI.Agent.PreviewWorkflows.Implementation;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

/// <summary>
///     Drains a single Preview workflow <see cref="StreamingRun" /> into provider-agnostic
///     <see cref="PreviewWorkflowUpdate" />s. Mirrors <c>OrchestrationRunSession</c>'s <c>WatchStreamAsync</c> drain
///     and <c>DisposeAsync</c> swallow-log. Holds the run in RAM across a Pause→resume round-trip.
///     Confines all <c>Microsoft.Agents.AI.Workflows</c> types behind <see cref="IPreviewWorkflowRunSession" />.
/// </summary>
internal sealed class PreviewWorkflowRunSession : IPreviewWorkflowRunSession
{
    private readonly IReadOnlyDictionary<string, string> _agentExecutorIdToNodeId;
    private readonly IReadOnlyDictionary<string, string> _debugExecutorIdToNodeId;
    private readonly ILogger _logger;
    private readonly object _pendingGate = new();

    // Pending pause requests surfaced via RequestInfoEvent, keyed by the request id we hand back to the caller. Held so
    // ResumeAsync can build the ExternalResponse the held run expects.
    private readonly Dictionary<string, ExternalRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _requestPortIdToNodeId;
    private readonly StreamingRun _run;

    public PreviewWorkflowRunSession(StreamingRun run,
        IReadOnlyDictionary<string, string> agentExecutorIdToNodeId,
        IReadOnlyDictionary<string, string> debugExecutorIdToNodeId,
        IReadOnlyDictionary<string, string> requestPortIdToNodeId,
        ILogger logger)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _agentExecutorIdToNodeId = agentExecutorIdToNodeId ?? throw new ArgumentNullException(nameof(agentExecutorIdToNodeId));
        _debugExecutorIdToNodeId = debugExecutorIdToNodeId ?? throw new ArgumentNullException(nameof(debugExecutorIdToNodeId));
        _requestPortIdToNodeId = requestPortIdToNodeId ?? throw new ArgumentNullException(nameof(requestPortIdToNodeId));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async IAsyncEnumerable<PreviewWorkflowUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var evt in _run.WatchStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var update = MapEvent(evt);
            if (update is null)
            {
                continue;
            }

            yield return update;

            // A Pause halts the run: stop draining so the caller can resume via ResumeAsync. The held StreamingRun
            // survives in RAM; re-enumerate WatchAsync after resuming.
            if (update.Kind == PreviewWorkflowUpdateKind.RunPaused)
            {
                yield break;
            }
        }
    }

    public async Task ResumeAsync(string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        cancellationToken.ThrowIfCancellationRequested();

        ExternalRequest request;
        lock (_pendingGate)
        {
            if (!_pendingRequests.TryGetValue(requestId, out var pending))
            {
                throw new InvalidOperationException($"No pending preview pause matches request id '{requestId}'.");
            }

            _ = _pendingRequests.Remove(requestId);
            request = pending;
        }

        // Echo the original upstream payload back as the port response so the post-pause adapter re-seeds the chain
        // with the REAL upstream output (not a bare "CONTINUE"); the pause is a halt-and-pass-through, not a rewrite.
        var upstream = request.Data?.AsType(typeof(string)) as string ?? "CONTINUE";
        var response = request.CreateResponse(upstream);
        await _run.SendResponseAsync(response).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _run.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Disposal must never throw out of an await-using; the run may already be torn down or cancelled.
            _logger.LogDebug(exception, "Ignoring error while disposing the preview streaming run.");
        }
    }

    private PreviewWorkflowUpdate? MapEvent(WorkflowEvent evt)
    {
        switch (evt)
        {
            case PreviewDebugEvent debug:
                return PreviewWorkflowUpdate.NodeDebug(debug.NodeId, debug.Payload);

            case ExecutorInvokedEvent invoked when _agentExecutorIdToNodeId.TryGetValue(invoked.ExecutorId, out var startedNode):
                return PreviewWorkflowUpdate.NodeStarted(startedNode);

            case AgentResponseUpdateEvent agentUpdate:
                return MapAgentUpdate(agentUpdate);

            case RequestInfoEvent requestInfo:
                return MapPause(requestInfo);

            case WorkflowOutputEvent output:
                return PreviewWorkflowUpdate.RunCompleted(output.As<string>());

            case ExecutorFailedEvent failed:
                var message = failed.Data?.Message ?? "Preview executor failed.";
                _logger.LogWarning("Preview executor '{ExecutorId}' failed: {Message}", failed.ExecutorId, message);
                if (_agentExecutorIdToNodeId.TryGetValue(failed.ExecutorId, out var failedNode))
                {
                    // Surface the node failure; the execution service folds it into a terminal run.failed.
                    return PreviewWorkflowUpdate.NodeFailed(failedNode, message);
                }

                return PreviewWorkflowUpdate.RunFailed(message);

            default:
                return null;
        }
    }

    private PreviewWorkflowUpdate? MapAgentUpdate(AgentResponseUpdateEvent agentUpdate)
    {
        // Attribute the streamed assistant text to the agent node and surface it as node output. The executor id on
        // the event is the agent's id (== node id, set in PreviewWorkflowRunner.BuildAgent).
        if (!_agentExecutorIdToNodeId.TryGetValue(agentUpdate.ExecutorId, out var nodeId))
        {
            return null;
        }

        var text = agentUpdate.Update?.Text;
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return PreviewWorkflowUpdate.NodeOutput(nodeId, text);
    }

    private PreviewWorkflowUpdate MapPause(RequestInfoEvent requestInfo)
    {
        var request = requestInfo.Request;
        var requestId = request.RequestId;

        lock (_pendingGate)
        {
            _pendingRequests[requestId] = request;
        }

        var upstream = request.Data?.AsType(typeof(string)) as string ?? string.Empty;
        var nodeId = _requestPortIdToNodeId.TryGetValue(request.PortInfo.PortId, out var mapped)
            ? mapped
            : request.PortInfo.PortId;

        return PreviewWorkflowUpdate.RunPaused(nodeId, upstream, requestId);
    }
}
