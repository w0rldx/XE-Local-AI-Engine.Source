namespace XE_Local_AI_Engine.Tests.DevWorkflows;

using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     A scripted stand-in for the sandbox half of the tool lane, so the graph can be exercised without provisioning a
///     repository and running a real build.
///     <para>
///         Everything above it is the real thing: the lane's slots, the in-flight registry, the rows, the report
///         artifact and the output document all come from the production executor. What this replaces is the one part
///         that costs half a minute and needs a checkout — which is why the seam exists at all.
///     </para>
///     <para>
///         A container singleton, so on the shared class host its script and its history are shared by every test in
///         the class. Script it per NODE KEY and a sibling cannot collide with you; hold or read it globally and take
///         a private host.
///     </para>
/// </summary>
internal sealed class FakeDevWorkflowToolCommands : IDevWorkflowToolCommands
{
    private readonly Lock _gate = new();
    private readonly List<string> _ran = [];
    private readonly Dictionary<string, IReadOnlyList<DevWorkflowToolRun>> _answers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolHold> _holds = new(StringComparer.Ordinal);

    /// <summary>Every node key whose commands were asked for, in order.</summary>
    public IReadOnlyList<string> Ran
    {
        get
        {
            lock (_gate)
            {
                return [.. _ran];
            }
        }
    }

    /// <summary>
    ///     What this node key's commands answer, attempt by attempt. The last one given repeats, so one answer is a node
    ///     that always says the same thing and two are a node that fails and then passes — which is what a retry looks
    ///     like from here.
    /// </summary>
    public void Answer(string nodeKey, params DevWorkflowToolRun[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        lock (_gate)
        {
            _answers[nodeKey] = results;
        }
    }

    /// <summary>
    ///     Parks this node key's commands until the returned handle is released, so a test can look at a node run while
    ///     it is genuinely in flight — holding a lane slot, cancellable, and unsettled.
    /// </summary>
    public ToolHold Hold(string nodeKey)
    {
        lock (_gate)
        {
            var hold = new ToolHold();
            _holds[nodeKey] = hold;
            return hold;
        }
    }

    public async Task<DevWorkflowToolRun> RunAsync(DevWorkflowRunSnapshot run,
        DevWorkflowGraphNode node,
        DevWorkflowNodeRunSnapshot nodeRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nodeRun);

        ToolHold? hold;
        DevWorkflowToolRun? answer = null;
        lock (_gate)
        {
            _ran.Add(nodeRun.NodeKey);
            _ = _holds.TryGetValue(nodeRun.NodeKey, out hold);
            if (_answers.TryGetValue(nodeRun.NodeKey, out var scripted) && scripted.Count > 0)
            {
                // Keyed on the ATTEMPT rather than on how many times this has been called, so a replayed pass answers
                // what its attempt answered rather than walking the script on.
                answer = scripted[Math.Min(nodeRun.Attempt - 1, scripted.Count - 1)];
            }
        }

        if (hold is not null)
        {
            hold.Enter();

            // WaitAsync rather than a token registration: a cancelled hold has to throw the same
            // OperationCanceledException a cancelled sandbox command does, because that is what the lane reads.
            await hold.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        return answer ?? Passing();
    }

    /// <summary>A clean pass with a small report, which is what a green validation node looks like.</summary>
    public static DevWorkflowToolRun Passing(int commandsRun = 2, string report = """{"passed":true}""") =>
        new(Passed: true,
            FailureClass: null,
            FailureCode: null,
            SanitizedReason: null,
            commandsRun,
            CommandsFailed: 0,
            TestsPassed: 12,
            TestsFailed: 0,
            Encoding.UTF8.GetBytes(report),
            []);

    /// <summary>A failed verdict: the commands ran and the gate said no, which is the fix loop's fuel.</summary>
    public static DevWorkflowToolRun Failing(string failureCode = "tests_failed", int testsFailed = 3) =>
        new(Passed: false,
            DevWorkflowFailureClasses.ToolCommandFailed,
            failureCode,
            $"Command dotnet_test_release_no_build reported {testsFailed} failing of 15 executed tests.",
            CommandsRun: 4,
            CommandsFailed: 1,
            TestsPassed: 12,
            testsFailed,
            Encoding.UTF8.GetBytes($$"""{"passed":false,"failureCode":"{{failureCode}}"}"""),
            []);

    /// <summary>A pass that never got as far as a verdict, for one of the classes no retry can answer.</summary>
    public static DevWorkflowToolRun Refusing(string failureClass, string reason, params string[] secretPaths) =>
        new(Passed: false,
            failureClass,
            FailureCode: null,
            reason,
            CommandsRun: 0,
            CommandsFailed: 0,
            TestsPassed: null,
            TestsFailed: null,
            ReadOnlyMemory<byte>.Empty,
            secretPaths);

    /// <summary>One parked pass: when it started, and the handle that lets it finish.</summary>
    internal sealed class ToolHold
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the lane has actually started this node's commands.</summary>
        public Task Started => _entered.Task;

        internal Task Released => _released.Task;

        internal void Enter() =>
            _entered.TrySetResult();

        public void Release() =>
            _released.TrySetResult();
    }
}
