// The WorkflowBuilder probe: the raw Microsoft.Agents.AI.Workflows WorkflowBuilder API, at the pinned version
// (Directory.Packages.props). PreviewWorkflows was the only production caller of the raw builder and has been
// deleted; one builder-shaped test is kept deliberately so the API stays exercised across MAF bumps. Fully deterministic: plain code executors, NO IChatClient, no model, no
// network — this probe deliberately has nothing to do with agents.
//
// This probe intentionally uses underscore-rich test names (CA1707), direct awaits in test code (CA2007), and
// MAF's experimental workflow API (MAAIW001).

#pragma warning disable CA1707, CA2007, MAAIW001
namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Collections.Concurrent;
using Microsoft.Agents.AI.Workflows;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     D8 builder-API probe. Pins the raw <see cref="WorkflowBuilder" /> surface that no production code calls
///     any more, so a MAF version bump that changes it fails here instead of silently rotting.
///     <para>
///         A future MAF bump MUST keep all of the following compiling and behaving as asserted:
///         <list type="bullet">
///             <item><see cref="Executor{TInput,TOutput}" /> / <see cref="Executor{TInput}" /> subclassing, with
///             <c>HandleAsync(TInput, IWorkflowContext, CancellationToken)</c> and a base ctor taking just an id.</item>
///             <item>Implicit use of an <see cref="Executor" /> where an <see cref="ExecutorBinding" /> is expected.</item>
///             <item><c>new WorkflowBuilder(start)</c>, <see cref="WorkflowBuilder.AddEdge(ExecutorBinding, ExecutorBinding)" />,
///             the conditional <c>AddEdge&lt;T&gt;(source, target, Func&lt;T, bool&gt;)</c> overload,
///             <c>WithOutputFrom(params ExecutorBinding[])</c> and parameterless <c>Build()</c>.</item>
///             <item>The protected <c>Executor.ConfigureProtocol</c> with <c>ProtocolBuilder.YieldsOutputTypes</c>, without which
///             an <see cref="Executor{TInput}" /> may not yield a workflow output at all.</item>
///             <item>Auto-forwarding of a handler's returned value along outgoing edges, and
///             <c>IWorkflowContext.YieldOutputAsync</c> surfacing as a <see cref="WorkflowOutputEvent" />
///             readable via <c>As&lt;T&gt;()</c>.</item>
///             <item>Non-streaming <see cref="InProcessExecution.RunAsync{TInput}(Workflow, TInput, string, CancellationToken)" />
///             returning a <see cref="Run" /> whose <c>NewEvents</c> drains the completed run's events on read.</item>
///             <item><see cref="RequestPort.Create{TRequest,TResponse}(string)" />, binding a port as an executor,
///             the <see cref="RequestInfoEvent" /> a pending request raises, <c>ExternalRequest.CreateResponse</c>,
///             and <c>Run.ResumeAsync(IEnumerable&lt;ExternalResponse&gt;, CancellationToken)</c>.</item>
///         </list>
///     </para>
/// </summary>
public sealed class WorkflowBuilderProbeTests
{
    private static readonly TimeSpan RunBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     start → A → B, built with the raw builder. Proves the returned value auto-forwards down each edge (so B
    ///     sees A's transform of the workflow input) and that the executors ran in graph order.
    /// </summary>
    [Test]
    public async Task Builder_ThreeExecutorChain_TransformsInputAndRunsInOrder()
    {
        var trace = new ConcurrentQueue<string>();
        var start = new PassThroughExecutor("probe-start", trace);
        var upper = new UpperCaseExecutor("probe-upper", trace);
        var sink = new SinkExecutor("probe-sink", trace);

        var workflow = new WorkflowBuilder(start)
                       .AddEdge(start, upper)
                       .AddEdge(upper, sink)
                       .WithOutputFrom(sink)
                       .Build();

        using var cts = new CancellationTokenSource(RunBudget);
        await using var run = await InProcessExecution.RunAsync(workflow, "probe-input", "d8-chain", cts.Token);

        var outputs = StringOutputs(DrainEvents(run, "chain"));
        AssertEx.Equal(expected: 1, outputs.Count, "the chain must yield exactly one workflow output");
        AssertEx.Equal("PROBE-INPUT!", outputs[0], "B must receive A's transform of the workflow input");
        AssertEx.Equal("probe-start,probe-upper,probe-sink",
            string.Join(",", trace),
            "executors must run in graph order");
    }

    /// <summary>
    ///     A conditional <c>AddEdge&lt;T&gt;</c> pair routes one message to exactly one of two targets. Run once per
    ///     lane, because a condition stuck at <c>true</c> would fan out to both and a condition stuck at
    ///     <c>false</c> would reach neither — both are caught here, a single lane would catch only one of them.
    /// </summary>
    [Test]
    public async Task Builder_ConditionalEdge_RoutesToExactlyOneTarget()
    {
        await AssertLaneRoutes("left");
        await AssertLaneRoutes("right");
    }

    /// <summary>
    ///     A <see cref="RequestPort" /> in the graph suspends the run with a <see cref="RequestInfoEvent" />; the test
    ///     answers it and the resumed run completes with the answer carried through to the terminal output.
    /// </summary>
    [Test]
    public async Task Builder_RequestPort_SuspendsThenCompletesOnExternalResponse()
    {
        var trace = new ConcurrentQueue<string>();
        var start = new PassThroughExecutor("probe-port-start", trace);
        var port = RequestPort.Create<string, string>("probe-port");
        var portBinding = port.BindAsExecutor();
        var finish = new SinkExecutor("probe-port-finish", trace);

        var workflow = new WorkflowBuilder(start)
                       .AddEdge(start, portBinding)
                       .AddEdge(portBinding, finish)
                       .WithOutputFrom(finish)
                       .Build();

        using var cts = new CancellationTokenSource(RunBudget);
        await using var run = await InProcessExecution.RunAsync(workflow, "needs-approval", "d8-port", cts.Token);

        var suspendedEvents = DrainEvents(run, "port-suspend");
        var pending = suspendedEvents.OfType<RequestInfoEvent>().ToList();
        AssertEx.Equal(expected: 1, pending.Count, "the port must suspend the run with exactly one pending request");
        AssertEx.Empty(StringOutputs(suspendedEvents), "no workflow output may be produced while the request is unanswered");
        AssertEx.True(pending[0].Request.TryGetDataAs<string>(out var requested),
            "the pending request must carry the upstream string payload");
        AssertEx.Equal("needs-approval", requested, "the port request must carry what the upstream executor sent");

        var resumed = await run.ResumeAsync([pending[0].Request.CreateResponse("approved")], cts.Token);
        AssertEx.True(resumed, "ResumeAsync must accept the external response");

        var outputs = StringOutputs(DrainEvents(run, "port-resume"));
        AssertEx.Equal(expected: 1, outputs.Count, "the resumed run must yield exactly one workflow output");
        AssertEx.Equal("approved!", outputs[0], "the external response must flow through to the terminal executor");
    }

    private static async Task AssertLaneRoutes(string lane)
    {
        var trace = new ConcurrentQueue<string>();
        var start = new TicketPassThroughExecutor("probe-router", trace);
        var left = new TicketSinkExecutor("probe-left", trace);
        var right = new TicketSinkExecutor("probe-right", trace);

        var workflow = new WorkflowBuilder(start)
                       .AddEdge<Ticket>(start, left, ticket => ticket?.Lane == "left")
                       .AddEdge<Ticket>(start, right, ticket => ticket?.Lane == "right")
                       .WithOutputFrom(left, right)
                       .Build();

        using var cts = new CancellationTokenSource(RunBudget);
        await using var run = await InProcessExecution.RunAsync(workflow, new Ticket(lane, "payload"), $"d8-route-{lane}", cts.Token);

        var events = DrainEvents(run, $"lane-{lane}");
        var visited = trace.Where(id => id != "probe-router").ToList();
        AssertEx.Equal(expected: 1, visited.Count, $"lane '{lane}' must reach exactly one target, reached [{string.Join(",", visited)}]");
        AssertEx.Equal($"probe-{lane}", visited[0], $"lane '{lane}' must reach the matching target");

        var outputs = StringOutputs(events);
        AssertEx.Equal(expected: 1, outputs.Count, $"lane '{lane}' must yield exactly one workflow output");
        AssertEx.Equal($"probe-{lane}:payload", outputs[0], $"lane '{lane}' output must come from the matching target");
    }

    /// <summary>
    ///     Snapshots the events the run produced since they were last read (<c>Run.NewEvents</c> drains on read, so
    ///     it must be enumerated exactly once per checkpoint) and fails loudly on any executor or workflow error —
    ///     without this, a broken executor surfaces only as a missing output and reads like a routing bug.
    /// </summary>
    private static List<WorkflowEvent> DrainEvents(Run run, string context)
    {
        var events = run.NewEvents.ToList();
        var failures = events
                       .Where(evt => evt is ExecutorFailedEvent or WorkflowErrorEvent)
                       .Select(evt => evt.ToString())
                       .ToList();
        AssertEx.Empty(failures, $"{context}: the run must not raise executor/workflow errors: {string.Join(" | ", failures)}");
        return events;
    }

    private static List<string> StringOutputs(IEnumerable<WorkflowEvent> events)
    {
        return events
               .OfType<WorkflowOutputEvent>()
               .Where(evt => evt.Is<string>())
               .Select(evt => evt.As<string>()!)
               .ToList();
    }

    private sealed record Ticket(string Lane, string Payload);

    /// <summary>Returns its input unchanged; the returned value is auto-forwarded along the outgoing edges.</summary>
    private sealed class PassThroughExecutor : Executor<string, string>
    {
        private readonly ConcurrentQueue<string> _trace;

        public PassThroughExecutor(string id, ConcurrentQueue<string> trace)
            : base(id)
        {
            _trace = trace;
        }

        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _trace.Enqueue(Id);
            return ValueTask.FromResult(message);
        }
    }

    /// <summary>The middle hop: transforms the message so the sink's assertion can only pass if A actually ran.</summary>
    private sealed class UpperCaseExecutor : Executor<string, string>
    {
        private readonly ConcurrentQueue<string> _trace;

        public UpperCaseExecutor(string id, ConcurrentQueue<string> trace)
            : base(id)
        {
            _trace = trace;
        }

        public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _trace.Enqueue(Id);
            return ValueTask.FromResult(message.ToUpperInvariant());
        }
    }

    /// <summary>Terminal sink: yields the workflow output explicitly rather than relying on auto-yield.</summary>
    private sealed class SinkExecutor : Executor<string>
    {
        private readonly ConcurrentQueue<string> _trace;

        public SinkExecutor(string id, ConcurrentQueue<string> trace)
            : base(id)
        {
            _trace = trace;
        }

        // Without this the runtime rejects the yield with "Cannot output object of type String. Expecting one of []":
        // an Executor<TInput> declares no output type by itself.
        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        {
            return base.ConfigureProtocol(protocolBuilder).YieldsOutputTypes([typeof(string)]);
        }

        public override async ValueTask HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _trace.Enqueue(Id);
            await context.YieldOutputAsync(message + "!", cancellationToken);
        }
    }

    private sealed class TicketPassThroughExecutor : Executor<Ticket, Ticket>
    {
        private readonly ConcurrentQueue<string> _trace;

        public TicketPassThroughExecutor(string id, ConcurrentQueue<string> trace)
            : base(id)
        {
            _trace = trace;
        }

        public override ValueTask<Ticket> HandleAsync(Ticket message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _trace.Enqueue(Id);
            return ValueTask.FromResult(message);
        }
    }

    private sealed class TicketSinkExecutor : Executor<Ticket>
    {
        private readonly ConcurrentQueue<string> _trace;

        public TicketSinkExecutor(string id, ConcurrentQueue<string> trace)
            : base(id)
        {
            _trace = trace;
        }

        protected override ProtocolBuilder ConfigureProtocol(ProtocolBuilder protocolBuilder)
        {
            return base.ConfigureProtocol(protocolBuilder).YieldsOutputTypes([typeof(string)]);
        }

        public override async ValueTask HandleAsync(Ticket message, IWorkflowContext context, CancellationToken cancellationToken = default)
        {
            _trace.Enqueue(Id);
            await context.YieldOutputAsync($"{Id}:{message.Payload}", cancellationToken);
        }
    }
}
#pragma warning restore CA1707, CA2007, MAAIW001
