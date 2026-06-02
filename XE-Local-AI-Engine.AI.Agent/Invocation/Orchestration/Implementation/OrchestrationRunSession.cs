namespace XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
///     Drives one handoff <see cref="Workflow" /> run and normalizes its <see cref="WorkflowEvent" /> stream into
///     <see cref="OrchestrationUpdate" />s. A single continuous <see cref="StreamingRun.WatchStreamAsync" />
///     enumeration carries the whole run, including a tool-approval pause/resume: the consumer answers a surfaced
///     <see cref="OrchestrationUpdateKind.ApprovalRequest" /> via <see cref="RespondToApprovalAsync" /> between
///     <c>MoveNext</c> calls (it queues the response on the held run for the next superstep) and keeps enumerating,
///     so the tool executes in a later superstep without re-entering the stream. Confines all
///     <c>Microsoft.Agents.AI.Workflows</c> types to this assembly.
/// </summary>
internal sealed class OrchestrationRunSession : IOrchestrationRunSession
{
    private readonly object _idleClockGate = new();
    private readonly TimeSpan _idleTimeout;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, OrchestrationParticipant> _participantsByAgentId;
    private readonly ConcurrentDictionary<string, ExternalRequest> _pendingApprovals = new(StringComparer.Ordinal);
    private readonly StreamingRun _run;

    // The live inter-event idle timer for the active WatchAsync enumeration, shared with RespondToApprovalAsync so it
    // can restart the clock after resuming a paused approval. Null when no watch is in flight.
    private CancellationTokenSource? _idleCts;

    public OrchestrationRunSession(StreamingRun run,
        IReadOnlyDictionary<string, OrchestrationParticipant> participantsByAgentId,
        TimeSpan idleTimeout,
        ILogger logger)
    {
        _run = run ?? throw new ArgumentNullException(nameof(run));
        _participantsByAgentId = participantsByAgentId ?? throw new ArgumentNullException(nameof(participantsByAgentId));
        _idleTimeout = idleTimeout;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async IAsyncEnumerable<OrchestrationUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // A handoff run drives itself to completion as the stream is pulled: the workflow advances supersteps
        // (triage → handoff → specialist → terminal WorkflowOutputEvent) and the stream then ENDS, so a full drain
        // is the natural terminator — no early break, which would otherwise change superstep timing and cut the
        // specialist's turn short. The idle CTS is a per-quiescence safety bound: it is reset after each event so a
        // legitimate multi-hop run is never cut off mid-stream, and it is SUSPENDED while a tool approval is pending
        // (the consumer blocks on a human round-trip for minutes after the ApprovalRequest is yielded, and
        // RespondToApprovalAsync restarts the clock on resume). True wall-clock lifetime is governed by the caller's
        // cancellation token, which is linked in here.
        var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_idleClockGate)
        {
            _idleCts = idleCts;
        }

        try
        {
            idleCts.CancelAfter(_idleTimeout);

            await foreach (var evt in _run.WatchStreamAsync(idleCts.Token).ConfigureAwait(false))
            {
                var update = MapEvent(evt);
                if (update is null)
                {
                    continue;
                }

                lock (_idleClockGate)
                {
                    // Set the idle bound BEFORE yielding (the consumer may block for minutes on the yielded update,
                    // and the clock must already reflect the new state by then): suspend it while a tool approval is
                    // outstanding (the consumer is awaiting a human decision; RespondToApprovalAsync restarts it on
                    // resume), otherwise reset it so each productive event renews the inter-event bound.
                    idleCts.CancelAfter(_pendingApprovals.IsEmpty ? _idleTimeout : Timeout.InfiniteTimeSpan);
                }

                yield return update;
            }
        }
        finally
        {
            lock (_idleClockGate)
            {
                _idleCts = null;
            }

            idleCts.Dispose();
        }
    }

    public async Task RespondToApprovalAsync(string requestId, bool approved, string? reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_pendingApprovals.TryRemove(requestId, out var request))
        {
            throw new InvalidOperationException($"No pending orchestration approval matches request id '{requestId}'.");
        }

        var response = BuildApprovalResponse(request, approved, reason);
        await _run.SendResponseAsync(response).ConfigureAwait(false);

        // The run resumes (the tool executes a later superstep); restart the idle clock the watch suspended while
        // this approval was outstanding, unless another approval is still pending.
        lock (_idleClockGate)
        {
            if (_idleCts is not null && _pendingApprovals.IsEmpty)
            {
                _idleCts.CancelAfter(_idleTimeout);
            }
        }
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
            _logger.LogDebug(exception, "Ignoring error while disposing the orchestration streaming run.");
        }
    }

    private static ExternalResponse BuildApprovalResponse(ExternalRequest request, bool approved, string? reason)
    {
        // The request payload is the ToolApprovalRequestContent (the FICC-surfaced approval). Build its response and
        // wrap it in the ExternalResponse the held run expects for a paused handoff workflow.
        if (request.Data?.AsType(typeof(object)) is not ToolApprovalRequestContent approvalRequest)
        {
            throw new InvalidOperationException("Orchestration approval request did not carry a tool-approval payload.");
        }

        return request.CreateResponse(approvalRequest.CreateResponse(approved, reason ?? string.Empty));
    }

    private OrchestrationUpdate? MapEvent(WorkflowEvent evt)
    {
        switch (evt)
        {
            case AgentResponseUpdateEvent updateEvent:
                return MapStreamingUpdate(updateEvent);

            case RequestInfoEvent requestInfo:
                return MapApprovalRequest(requestInfo);

            case WorkflowOutputEvent:
                return OrchestrationUpdate.Terminal();

            case ExecutorFailedEvent failed:
                var message = failed.Data?.Message ?? "Orchestration executor failed.";
                _logger.LogWarning("Orchestration executor '{ExecutorId}' failed: {Message}", failed.ExecutorId, message);
                return OrchestrationUpdate.Failed(message, null, null);

            default:
                return null;
        }
    }

    private OrchestrationUpdate? MapStreamingUpdate(AgentResponseUpdateEvent updateEvent)
    {
        var (participantKey, participantName) = ResolveParticipant(updateEvent.ExecutorId);

        // A single update can carry reasoning and/or visible text. Reasoning content is surfaced as a reasoning
        // delta; the remaining text is surfaced as a text delta. The MAF FunctionCallContent for a handoff carries
        // no user-visible text, so those updates map to null and are skipped.
        var reasoning = updateEvent.Update.Contents
                                   .OfType<TextReasoningContent>()
                                   .Select(static content => content.Text)
                                   .Where(static text => !string.IsNullOrEmpty(text))
                                   .ToList();
        if (reasoning.Count > 0)
        {
            return OrchestrationUpdate.ReasoningFragment(string.Concat(reasoning), participantKey, participantName);
        }

        var text = updateEvent.Update.Text;
        return string.IsNullOrEmpty(text)
            ? null
            : OrchestrationUpdate.TextFragment(text, participantKey, participantName);
    }

    private OrchestrationUpdate MapApprovalRequest(RequestInfoEvent requestInfo)
    {
        var request = requestInfo.Request;
        _pendingApprovals[request.RequestId] = request;

        var toolName = ResolveApprovalToolName(request);
        return OrchestrationUpdate.Approval(request.RequestId, toolName, null, null);
    }

    private static string ResolveApprovalToolName(ExternalRequest request)
    {
        // The approval's ToolCall is a FunctionCallContent (FICC surfaces approval-required AIFunctions); read its
        // Name for the UX. "unknown" is a display-only fallback — the RequestId, not the name, drives the resume.
        if (request.Data?.AsType(typeof(object)) is ToolApprovalRequestContent approvalRequest
            && approvalRequest.ToolCall is FunctionCallContent functionCall
            && !string.IsNullOrWhiteSpace(functionCall.Name))
        {
            return functionCall.Name;
        }

        return "unknown";
    }

    private (string? Key, string? Name) ResolveParticipant(string executorId)
    {
        if (string.IsNullOrEmpty(executorId))
        {
            return (null, null);
        }

        // MAF names the agent executor "{AgentName}_{AgentId}", so an exact agent-id match fails. Match on the
        // "_{agentId}" suffix (the id is the stable part; the name prefix can vary). Exact match is tried first.
        if (_participantsByAgentId.TryGetValue(executorId, out var exact))
        {
            return (exact.Key, exact.Name);
        }

        foreach (var (agentId, participant) in _participantsByAgentId)
        {
            if (executorId.EndsWith(agentId, StringComparison.Ordinal))
            {
                return (participant.Key, participant.Name);
            }
        }

        return (null, null);
    }
}
