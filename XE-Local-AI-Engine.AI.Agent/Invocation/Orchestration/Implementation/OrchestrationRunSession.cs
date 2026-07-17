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
    private readonly TimeSpan _abandonmentGrace = IdleStreamGuard.DefaultAbandonmentGrace;
    private readonly Lock _idleClockGate = new();
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

            // The idle CTS is only a COOPERATIVE deadline: a workflow that ignores its token would never return from
            // MoveNextAsync, so neither the timer nor disposal could run. IdleStreamGuard adds the wall-clock bound —
            // it races each advancement against idleCts.Token and, on expiry, abandons a non-cooperative workflow
            // (observed off-thread, bounded disposal) instead of awaiting it forever. The guard binds the workflow's
            // own cancellation to the OUTER token so cooperative cancellation still works, and surfaces idle expiry as
            // an OperationCanceledException (as before), invoking the metric callbacks on timeout/abandonment.
            var guarded = IdleStreamGuard.GuardAsync(watchToken => _run.WatchStreamAsync(watchToken).GetAsyncEnumerator(watchToken),
                new IdleGuardContext(_abandonmentGrace,
                    static () => WorkflowWatchdogMetrics.RecordWatchdogTimeout(WorkflowWatchdogMetrics.OrchestrationSurface),
                    static () => WorkflowWatchdogMetrics.RecordAbandoned(WorkflowWatchdogMetrics.OrchestrationSurface),
                    idleCts.Token,
                    cancellationToken));

            await foreach (var evt in guarded.ConfigureAwait(false))
            {
                // One source event can normalize to MORE than one update — a single streaming update carrying both
                // reasoning and visible text yields a reasoning fragment AND a text fragment (GPTAUD-03b). Each is
                // surfaced in order (reasoning first) so no visible text is dropped.
                foreach (var update in MapEvent(evt))
                {
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
            // Bound disposal on the same discipline as the watch: a workflow that ignores cancellation could leave its
            // DisposeAsync pending forever and hold up shutdown. If it does not complete within the grace, abandon it
            // (observed off-thread) and record it rather than blocking indefinitely.
            if (!await IdleStreamGuard.DisposeBoundedAsync(_run, _abandonmentGrace).ConfigureAwait(false))
            {
                WorkflowWatchdogMetrics.RecordAbandoned(WorkflowWatchdogMetrics.OrchestrationSurface);
                _logger.LogWarning("Orchestration run disposal exceeded the {Grace} bound; abandoning it (its native resources may not be reclaimed until it returns).", _abandonmentGrace);
            }
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

    // Zero, one, or two normalized updates per source event. Eager materialization is deliberate: MapApprovalRequest
    // registers the request in _pendingApprovals as a side effect that must run before the update is yielded (the idle
    // clock reads _pendingApprovals when deciding whether to suspend), so the mapping cannot be deferred.
    private IReadOnlyList<OrchestrationUpdate> MapEvent(WorkflowEvent evt)
    {
        switch (evt)
        {
            case AgentResponseUpdateEvent updateEvent:
                return MapStreamingUpdate(updateEvent);

            case RequestInfoEvent requestInfo:
                return [MapApprovalRequest(requestInfo)];

            case WorkflowOutputEvent:
                return [OrchestrationUpdate.Terminal()];

            case ExecutorFailedEvent failed:
                var message = failed.Data?.Message ?? "Orchestration executor failed.";
                _logger.LogWarning("Orchestration executor '{ExecutorId}' failed: {Message}", failed.ExecutorId, message);
                return [OrchestrationUpdate.Failed(message, participantKey: null, participantName: null)];

            default:
                return [];
        }
    }

    private IReadOnlyList<OrchestrationUpdate> MapStreamingUpdate(AgentResponseUpdateEvent updateEvent)
    {
        var (participantKey, participantName) = ResolveParticipant(updateEvent.ExecutorId);
        return ComposeStreamingUpdates(updateEvent.Update.Contents, updateEvent.Update.Text, participantKey, participantName);
    }

    /// <summary>
    ///     Normalizes one streaming update's contents into ordered <see cref="OrchestrationUpdate" />s. A single update
    ///     can carry reasoning AND visible text (both co-occur on the wire), so this emits BOTH — a reasoning delta first,
    ///     then a text delta (GPTAUD-03b) — rather than returning early on reasoning and dropping the visible text (the
    ///     bug this fixes). A MAF handoff <c>FunctionCallContent</c> carries neither, so it maps to an empty list.
    ///     Pure and static so the mapping is unit-testable without the concrete MAF <c>StreamingRun</c> (which cannot be
    ///     faked).
    /// </summary>
    internal static IReadOnlyList<OrchestrationUpdate> ComposeStreamingUpdates(IEnumerable<AIContent> contents,
        string? text,
        string? participantKey,
        string? participantName)
    {
        ArgumentNullException.ThrowIfNull(contents);

        var updates = new List<OrchestrationUpdate>(capacity: 2);

        var reasoning = contents
                        .OfType<TextReasoningContent>()
                        .Select(static content => content.Text)
                        .Where(static fragment => !string.IsNullOrEmpty(fragment))
                        .ToList();
        if (reasoning.Count > 0)
        {
            updates.Add(OrchestrationUpdate.ReasoningFragment(string.Concat(reasoning), participantKey, participantName));
        }

        if (!string.IsNullOrEmpty(text))
        {
            updates.Add(OrchestrationUpdate.TextFragment(text, participantKey, participantName));
        }

        return updates;
    }

    private OrchestrationUpdate MapApprovalRequest(RequestInfoEvent requestInfo)
    {
        var request = requestInfo.Request;
        _pendingApprovals[request.RequestId] = request;

        var toolName = ResolveApprovalToolName(request);
        return OrchestrationUpdate.Approval(request.RequestId, toolName, participantKey: null, participantName: null);
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
