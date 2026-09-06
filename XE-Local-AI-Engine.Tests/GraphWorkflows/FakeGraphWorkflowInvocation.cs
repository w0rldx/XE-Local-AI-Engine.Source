namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Models.Events;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>How one scripted turn ends.</summary>
internal enum GraphWorkflowTurnOutcome
{
    /// <summary>Streams its text and reports Completed.</summary>
    Completes,

    /// <summary>Reports Failed with a provider error nothing outside the node logs may repeat.</summary>
    Fails,

    /// <summary>Reports Cancelled without anyone asking — an operator force-eject of the model mid-turn.</summary>
    Cancels,

    /// <summary>Reports nothing at all, so the turn produces no terminal state to map.</summary>
    Silent,

    /// <summary>Throws, which is what an unforeseen failure inside the lane's task body looks like.</summary>
    Throws,

    /// <summary>
    ///     Never ends on its own. It ends <c>Cancelled</c> when the stop path reaches it — through
    ///     <see cref="FakeGraphWorkflowInvocation.Cancel" /> or through the token — and it reports that terminal and
    ///     returns NORMALLY, because the real runner swallows the cancellation rather than rethrowing it.
    /// </summary>
    Parks,

    /// <summary>
    ///     Never ends, and does not notice a stop either: a turn still winding down inside a provider stream, which is
    ///     the state a cancelling drain keeps meeting until a poll finally SEES it land. Only
    ///     <see cref="FakeGraphWorkflowInvocation.Release" /> ends one.
    /// </summary>
    Wedges
}

/// <summary>
///     One scripted turn, keyed by a fragment of the seed user turn the node sends. Defaults are the happy path: text
///     out, a full usage block, and a <c>stop</c> finish reason.
///     <para>
///         <paramref name="FailureCategory" /> applies to <see cref="GraphWorkflowTurnOutcome.Fails" /> only, and it is
///         scriptable because the runner's watchdog reports a TIMEOUT as an ordinary failed terminal — the category is
///         the only place that difference survives, and mapping it is what keeps a timed-out node off the plain
///         provider-failure class.
///     </para>
/// </summary>
internal sealed record GraphWorkflowScriptedTurn(
    GraphWorkflowTurnOutcome Outcome = GraphWorkflowTurnOutcome.Completes,
    string Text = "the fake agent answered",
    string FinishReason = "stop",
    FailureCategory FailureCategory = FailureCategory.ProviderUnreachable);

/// <summary>
///     The invocation-runner seam: there is no installed model here for a turn to run on. It is one of the FIVE seams
///     <see cref="GraphWorkflowAgentHostFixture" /> replaces, which lists the rest and why each one cannot answer
///     truthfully on a unit-test host.
///     <para>
///         Everything AROUND those five stays real, and that is the point rather than economy. The real
///         <c>WorkerEventDispatcher</c> holds a genuine one-slot semaphore, so <c>Running, Queued, Queued</c> across a
///         fan-out is OBSERVED here rather than simulated; the real package builder is what makes an assertion about
///         <c>IsUnattended</c> or the stripped offer mean anything.
///     </para>
///     <para>
///         Terminals are reported through the real dispatcher's own report methods rather than by raising its event,
///         which is not something an outside type can do — and would bypass the state accumulation the executor reads.
///     </para>
///     <para>
///         Three members do work; every other one throws, so a path that reaches an unimplemented corner of the runner
///         fails loudly instead of quietly answering a default.
///     </para>
/// </summary>
internal sealed class FakeGraphWorkflowInvocation(IServiceProvider services) : IInvocationRunner
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _parked = new();
    private readonly ConcurrentBag<Guid> _wedged = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, GraphWorkflowScriptedTurn> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Guid> _cancelled = new();
    private readonly ConcurrentQueue<RuntimePackage> _packages = new();

    /// <summary>
    ///     Resolved LAZILY, and it has to be: the real <c>WorkerEventDispatcher</c> takes an
    ///     <see cref="IInvocationRunner" /> of its own, so a fake that asked for the dispatcher in its constructor
    ///     would send the container back through this factory and recurse until the stack ran out. By the time a turn
    ///     runs, the executor has already resolved the dispatcher and the cycle is long closed.
    /// </summary>
    private readonly Lazy<IWorkerEventDispatcher> _eventDispatcher =
        new((services ?? throw new ArgumentNullException(nameof(services))).GetRequiredService<IWorkerEventDispatcher>);

    /// <summary>Every package this fake was handed, so a test can assert the contract the fake itself cannot see.</summary>
    public IReadOnlyList<RuntimePackage> Packages => [.. _packages];

    /// <summary>Every invocation id the stop path asked the runner to unwind, in order, WITH its repeats.</summary>
    public IReadOnlyList<Guid> Cancelled => [.. _cancelled];

    public int ActiveInvocationCount => _parked.Count;

    /// <summary>Scripts what a turn whose seed prompt contains <paramref name="promptFragment" /> does.</summary>
    public void Script(string promptFragment, GraphWorkflowScriptedTurn turn) =>
        _scripts[promptFragment] = turn;

    /// <summary>
    ///     Completes once a turn whose seed prompt contains <paramref name="promptFragment" /> has STARTED — which is
    ///     the moment after it took the node-wide invocation lease, and therefore the moment its row may say
    ///     <c>Running</c>. The alternative is sleeping and hoping.
    /// </summary>
    public Task WhenRunningAsync(string promptFragment) =>
        Started(promptFragment).Task;

    /// <summary>The package this fake was handed for a turn whose seed prompt contains <paramref name="promptFragment" />.</summary>
    public RuntimePackage PackageFor(string promptFragment) =>
        _packages.FirstOrDefault(package => Prompt(package).Contains(promptFragment, StringComparison.Ordinal))
        ?? throw new AssertionException($"No runtime package was built for a turn whose prompt contains '{promptFragment}'.");

    public async Task RunAsync(InvocationExecutionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var package = context.Package;
        _packages.Enqueue(package);
        var turn = ScriptFor(package);
        foreach (var fragment in _scripts.Keys.Where(fragment => Prompt(package).Contains(fragment, StringComparison.Ordinal)))
        {
            _ = Started(fragment).TrySetResult();
        }

        _ = Started(package.InvocationId.ToString()).TrySetResult();

        if (turn.Outcome is GraphWorkflowTurnOutcome.Parks or GraphWorkflowTurnOutcome.Wedges)
        {
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _parked[package.InvocationId] = parked;
            if (turn.Outcome == GraphWorkflowTurnOutcome.Wedges)
            {
                _wedged.Add(package.InvocationId);
                await parked.Task.ConfigureAwait(false);
            }
            else
            {
                using (cancellationToken.Register(() => parked.TrySetResult()))
                {
                    await parked.Task.ConfigureAwait(false);
                }
            }

            _ = _parked.TryRemove(package.InvocationId, out _);

            // Reported and RETURNED, never thrown: the real runner reports Cancelled to the dispatcher and returns
            // normally, which is exactly why the caller has to re-surface the cancellation itself.
            await _eventDispatcher.Value.ReportInvocationFailedAsync(package.InvocationId, "The turn was cancelled.", FailureCategory.Cancelled).ConfigureAwait(false);
            return;
        }

        switch (turn.Outcome)
        {
            case GraphWorkflowTurnOutcome.Throws:
                throw new InvalidOperationException("the fake runner could not reach its provider");

            case GraphWorkflowTurnOutcome.Silent:
                return;

            case GraphWorkflowTurnOutcome.Fails:
                await _eventDispatcher.Value.ReportInvocationFailedAsync(package.InvocationId, "provider said no: connection reset at 10.0.0.7", turn.FailureCategory)
                                      .ConfigureAwait(false);
                return;

            case GraphWorkflowTurnOutcome.Cancels:
                await _eventDispatcher.Value.ReportInvocationFailedAsync(package.InvocationId, "The turn was cancelled.", FailureCategory.Cancelled).ConfigureAwait(false);
                return;

            default:
                await _eventDispatcher.Value.ReportInvocationStreamChunkAsync(package.InvocationId, turn.Text).ConfigureAwait(false);
                await _eventDispatcher.Value.ReportInvocationCompletedAsync(package.InvocationId,
                                          inputTokens: 11,
                                          outputTokens: 22,
                                          totalTokens: 33,
                                          reasoningTokens: 4,
                                          generationDurationMs: 55,
                                          turn.FinishReason)
                                      .ConfigureAwait(false);
                return;
        }
    }

    public void Cancel(Guid invocationId)
    {
        _cancelled.Enqueue(invocationId);
        if (_wedged.Contains(invocationId))
        {
            // A wedged turn is one that has been ASKED and has not landed yet. That gap is the whole reason the stop
            // path must answer no on a repeat, so the fake keeps it open until a test closes it.
            return;
        }

        if (_parked.TryGetValue(invocationId, out var parked))
        {
            _ = parked.TrySetResult();
        }
    }

    /// <summary>Ends a wedged turn, which is the only thing that does.</summary>
    public void Release(Guid invocationId)
    {
        if (_parked.TryGetValue(invocationId, out var parked))
        {
            _ = parked.TrySetResult();
        }
    }

    /// <summary>Ends every turn still in flight, so a host teardown never waits on one that ignores its token.</summary>
    public void ReleaseAll()
    {
        foreach (var parked in _parked.Values)
        {
            _ = parked.TrySetResult();
        }
    }

    public Task<bool> DrainActiveInvocationsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<string> ExecuteApiToolCallAsync(Guid invocationId, string toolName, string parameters, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public void CancelDetached(Guid invocationId) =>
        throw new NotSupportedException();

    public void CancelAll() =>
        throw new NotSupportedException();

    public void CleanupStaleToolCalls(TimeSpan maxAge) =>
        throw new NotSupportedException();

    public void ResolveApprovalResult(ApprovalResolvedEvent evt, ApprovalScope scope = ApprovalScope.Once) =>
        throw new NotSupportedException();

    public void ResolveUserQuestionResult(UserQuestionAnsweredEvent evt) =>
        throw new NotSupportedException();

    public void ResolveToolCallResult(ToolCallResultEvent evt) =>
        throw new NotSupportedException();

    private static string Prompt(RuntimePackage package) =>
        package.ConversationContext.Count > 0 ? package.ConversationContext[0].Content : string.Empty;

    private GraphWorkflowScriptedTurn ScriptFor(RuntimePackage package)
    {
        var prompt = Prompt(package);
        foreach (var (fragment, turn) in _scripts)
        {
            if (prompt.Contains(fragment, StringComparison.Ordinal))
            {
                return turn;
            }
        }

        return new GraphWorkflowScriptedTurn();
    }

    private TaskCompletionSource Started(string key) =>
        _running.GetOrAdd(key, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}
