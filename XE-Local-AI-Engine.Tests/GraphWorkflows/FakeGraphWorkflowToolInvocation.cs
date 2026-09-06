namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     One scripted tool call, keyed by the tool NAME the node asks for. Defaults are the happy path: executed, with a
///     line of text out.
///     <para>
///         <paramref name="Parks" /> is how a test holds a lane slot without sleeping: the call never ends on its own,
///         and it ends <c>Cancelled</c> when the token fires — which is exactly what the real service does, because it
///         answers a cancellation with an outcome rather than by throwing.
///     </para>
///     <para>
///         <paramref name="Throws" /> is that contract BROKEN, so a lane that quietly depended on it can be shown not
///         to.
///     </para>
///     <para>
///         <paramref name="Blocks" /> holds the CALLING THREAD rather than a task, which <paramref name="Parks" />
///         cannot: a tool that scans a filesystem does its work before its first await, and a lane that started it
///         inline would do that scanning inside the dispatcher's tick.
///     </para>
/// </summary>
internal sealed record GraphWorkflowScriptedTool(
    ToolInvocationOutcomeKind Kind = ToolInvocationOutcomeKind.Executed,
    string? Result = "the fake tool answered",
    string Reason = "read-local",
    bool Parks = false,
    bool Throws = false,
    bool Blocks = false);

/// <summary>
///     The tool-invocation seam, scripted per tool name. The ONE thing
///     <see cref="GraphWorkflowToolHostFixture" /> replaces, and only where a test needs an outcome the real service
///     cannot be made to produce on demand — a fault, a timeout, or a call that parks long enough to observe a lane
///     slot being held.
///     <para>
///         The envelope itself is NEVER faked: which tools may run is asserted against the real service at its own
///         definition site by <see cref="ToolInvocationServiceTests" />, and re-deciding it here would be a second copy
///         of a rule that exists to have exactly one.
///     </para>
///     <para>
///         Scripting is by NAME rather than per test, so one shared host can serve tests that need different worlds:
///         a test that scripts <c>probe_x</c> is not state a sibling scripting <c>probe_y</c> can read. A test that
///         PARKS still takes a host of its own — a parked call holds a lane slot, and <see cref="ReleaseAll" /> is
///         node-wide.
///     </para>
/// </summary>
internal sealed class FakeGraphWorkflowToolInvocation : IToolInvocationService, IDisposable
{
    /// <summary>How long a synchronous block waits before giving up, so a failed assertion cannot hang the suite.</summary>
    private static readonly TimeSpan BlockCeiling = TimeSpan.FromSeconds(30);

    private readonly ManualResetEventSlim _unblocked = new(initialState: false);

    private readonly ConcurrentQueue<GraphWorkflowToolCall> _calls = new();
    private readonly ConcurrentBag<TaskCompletionSource> _parked = [];
    private readonly ConcurrentDictionary<string, GraphWorkflowScriptedTool> _scripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _started = new(StringComparer.Ordinal);
    private int _open;

    /// <summary>Every call this fake was handed, in order and WITH its repeats.</summary>
    public IReadOnlyList<GraphWorkflowToolCall> Calls => [.. _calls];

    /// <summary>Scripts what a call to <paramref name="toolName" /> does.</summary>
    public void Script(string toolName, GraphWorkflowScriptedTool call) =>
        _scripts[toolName] = call;

    /// <summary>
    ///     Advertises <paramref name="toolName" /> on the happy path, for a graph that names a tool but does not care
    ///     how it answers. It is not decoration: the save-time and run-start gates refuse a Tool node naming anything
    ///     <see cref="ListInvocableToolsAsync" /> does not list, so a graph's tools have to exist before it is seeded.
    /// </summary>
    public void Declare(string toolName) =>
        Script(toolName, new GraphWorkflowScriptedTool());

    /// <summary>The first call made to <paramref name="toolName" />, which is where its arguments are asserted.</summary>
    public GraphWorkflowToolCall CallFor(string toolName) =>
        _calls.FirstOrDefault(call => string.Equals(call.ToolName, toolName, StringComparison.Ordinal))
        ?? throw new AssertionException($"The fake tool service was never asked to invoke '{toolName}'.");

    public int CallCountFor(string toolName) =>
        _calls.Count(call => string.Equals(call.ToolName, toolName, StringComparison.Ordinal));

    /// <summary>
    ///     Completes once a call to <paramref name="toolName" /> has STARTED — which for this lane is the moment it
    ///     holds its slot, and therefore the moment its row may say <c>Running</c>. The alternative is sleeping.
    /// </summary>
    public Task WhenRunningAsync(string toolName) =>
        Started(toolName).Task;

    /// <summary>
    ///     Ends every parked call and lets every LATER one through as well, which is what makes a bounded fan-out
    ///     observable: the rows still queued take their slots as the parked ones free them, without a second gate for
    ///     each.
    /// </summary>
    public void ReleaseAll()
    {
        _ = Interlocked.Exchange(ref _open, value: 1);
        _unblocked.Set();
        foreach (var parked in _parked)
        {
            _ = parked.TrySetResult();
        }
    }

    public void Dispose() =>
        _unblocked.Dispose();

    public async Task<ToolInvocationOutcome> InvokeAsync(string toolName,
        string argumentsJson,
        ToolInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        _calls.Enqueue(new GraphWorkflowToolCall(toolName, argumentsJson, context));
        var script = _scripts.TryGetValue(toolName, out var scripted) ? scripted : new GraphWorkflowScriptedTool();
        _ = Started(toolName).TrySetResult();

        // The one thing the real service promises never to do. Scripted anyway, because the lane must not depend on
        // that promise being kept — a faulted task it awaited would rethrow into the dispatcher forever.
        if (script.Throws)
        {
            throw new InvalidOperationException("the fake tool service could not answer");
        }

        // Blocking the THREAD, on purpose, and bounded so a failed assertion cannot hang the suite. real-timer: the
        // ceiling is a backstop, never the path a passing test takes — ReleaseAll is.
        if (script.Blocks && Volatile.Read(ref _open) == 0)
        {
            _ = _unblocked.Wait(BlockCeiling, cancellationToken);
        }

        if (script.Parks && Volatile.Read(ref _open) == 0)
        {
            var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _parked.Add(parked);
            using (cancellationToken.Register(() => parked.TrySetResult()))
            {
                await parked.Task.ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Answered and RETURNED, never thrown: the real service maps its own cancellation to an outcome, which
                // is what lets the lane's poll settle a landed call without ever having to rethrow.
                return new ToolInvocationOutcome(ToolInvocationOutcomeKind.Cancelled, Result: null, $"The invocation of '{toolName}' was cancelled.");
            }
        }

        // A refused call carries no result, exactly as the real service leaves one.
        return new ToolInvocationOutcome(script.Kind, script.Kind == ToolInvocationOutcomeKind.Executed ? script.Result : null, script.Reason);
    }

    /// <summary>The scripted names, which is all a picker on this host could offer.</summary>
    public Task<IReadOnlyList<InvocableToolDescriptor>> ListInvocableToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InvocableToolDescriptor>>([
            .. _scripts.Keys.Order(StringComparer.Ordinal)
                       .Select(static name => new InvocableToolDescriptor(name, $"The fake {name}.", """{"type":"object","properties":{}}"""))
        ]);

    private TaskCompletionSource Started(string toolName) =>
        _started.GetOrAdd(toolName, static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
}

/// <summary>One invocation, as the lane asked for it. The arguments arrive as the JSON the executor serialized.</summary>
internal sealed record GraphWorkflowToolCall(string ToolName, string ArgumentsJson, ToolInvocationContext Context);
