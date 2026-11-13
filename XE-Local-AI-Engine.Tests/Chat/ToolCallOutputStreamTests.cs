namespace XE_Local_AI_Engine.Tests.Chat;

using System.Globalization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>A tool call's live sandbox command output over the real dispatcher, forwarder, stream sink and resume registry.</summary>
/// <remarks>
///     Sequenced and redacted on the turn's stream, kept out of the tool timeline, bounded under a flood, replayed on
///     resume as a replacement, and folded into the completed tool part.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class ToolCallOutputStreamTests
{
    private const string CallId = "call-1";
    private const string WorkspaceRoot = "/srv/jails/abc";
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Test]
    public async Task CommandOutput_ReachesTheTurnsStream_AsSequencedRedactedEvents_OnItsOwnCard()
    {
        await using var turn = await TurnHarness.StartAsync();
        using (turn.OpenScope())
        using (AmbientToolCall.Enter(CallId, "run_python"))
        using (ToolCommandOutputScope.BindCurrentCall())
        {
            using var output = turn.StartCommand($"python {WorkspaceRoot}/job.py --password=hunter2");
            output.Observer.OnLine(SandboxOutputSource.StandardOutput, $"\u001b[32mreading {WorkspaceRoot}/data.csv\u001b[0m");
            output.Observer.OnLine(SandboxOutputSource.StandardError, "api_key=sk-123 rejected");
            turn.Clock.Advance(Interval);
            output.Complete(Result(exitCode: 3));
        }

        var events = await turn.DrainAsync();

        var sequences = events.Select(static streamEvent => streamEvent.Sequence).ToList();
        AssertEx.True(sequences.SequenceEqual(sequences.Order()) && sequences.Distinct().Count() == sequences.Count, "every frame carries its own ascending number");
        AssertEx.Equal(ChatStreamEventTypes.ToolCallRequested, events[0].Type);

        var outputs = events.Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallOutput).ToList();
        AssertEx.Equal(expected: 3, outputs.Count);
        AssertEx.True(outputs.All(static streamEvent => streamEvent.ToolCallId == CallId));
        AssertEx.True(outputs.All(static streamEvent => streamEvent.CommandLabel == "python <workspace>/job.py --password[redacted]"),
            "the label leaves the process with the workspace root and the credential redacted");

        var start = outputs[0];
        AssertEx.True(start is { CommandRunning: true, OutputReplace: true, CommandExitCode: null });
        AssertEx.Empty(start.OutputLines);

        var batch = outputs[1];
        AssertEx.True(batch is { CommandRunning: true, OutputReplace: false });
        var lines = AssertEx.NotNull(batch.OutputLines);
        AssertEx.Equal(expected: 2, lines.Count);
        AssertEx.Equal(NodeChatToolOutputStreams.Stdout, lines[0].Stream);
        AssertEx.Equal("reading <workspace>/data.csv", lines[0].Text);
        AssertEx.Equal(NodeChatToolOutputStreams.Stderr, lines[1].Stream);
        AssertEx.Equal("api_key[redacted] rejected", lines[1].Text);

        var end = outputs[2];
        AssertEx.True(end is { CommandRunning: false, CommandExitCode: 3, OutputReplace: false, OutputSkippedLines: 0 });
        AssertEx.Empty(end.OutputLines);
    }

    [Test]
    public async Task AnAgentHomeCommandLabel_CarryingAnAuthorizationHeader_LeavesWithoutTheToken()
    {
        await using var turn = await TurnHarness.StartAsync();
        using (turn.OpenScope())
        using (AmbientToolCall.Enter(CallId, "run_in_agent_home"))
        using (ToolCommandOutputScope.BindCurrentCall())
        {
            using var output = turn.StartCommand("curl -H \"Authorization: Bearer abc123\" https://api.example/v1");
            output.Complete(Result(exitCode: 0));
        }

        var labels = (await turn.DrainAsync()).Where(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallOutput)
                                              .Select(static streamEvent => streamEvent.CommandLabel)
                                              .Distinct()
                                              .ToList();
        AssertEx.Equal("curl -H \"Authorization[redacted]\" https://api.example/v1", labels.Single());
    }

    [Test]
    public async Task Resume_ReplaysTheCallsTail_AsAReplacement_AndKeepsOutputOutOfTheToolTimeline()
    {
        await using var turn = await TurnHarness.StartAsync();
        using var scope = turn.OpenScope();
        using var call = AmbientToolCall.Enter(CallId, "run_python");
        using var bound = ToolCommandOutputScope.BindCurrentCall();
        using var output = turn.StartCommand("make test");
        output.Observer.OnLine(SandboxOutputSource.StandardOutput, "one");
        turn.Clock.Advance(Interval);
        output.Observer.OnLine(SandboxOutputSource.StandardError, "two");
        turn.Clock.Advance(Interval);

        var replay = await turn.ResumeOpeningAsync();

        AssertEx.Equal(expected: 1, replay.Count(static streamEvent => streamEvent.Type is ChatStreamEventTypes.ToolCallRequested or ChatStreamEventTypes.ToolCallCompleted),
            "live output never enters the tool lifecycle history");
        var tail = replay.Single(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallOutput);
        AssertEx.True(tail is { ToolCallId: CallId, OutputReplace: true, CommandRunning: true, CommandLabel: "make test" });
        AssertEx.Equal("one|two", string.Join('|', AssertEx.NotNull(tail.OutputLines).Select(static line => line.Text)));
        AssertEx.True(replay.FindIndex(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested)
                      < replay.FindIndex(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallOutput),
            "the tail follows the card it belongs to");
    }

    [Test]
    public async Task AFlood_NeitherForcesAReconcile_NorTouchesTheToolTimeline_AndEndsOnTheRealTail()
    {
        await using var turn = await TurnHarness.StartAsync();
        using (turn.OpenScope())
        using (AmbientToolCall.Enter(CallId, "run_python"))
        using (ToolCommandOutputScope.BindCurrentCall())
        {
            using var output = turn.StartCommand(label: null);
            for (var index = 0; index < 100_000; index++)
            {
                output.Observer.OnLine(SandboxOutputSource.StandardOutput, string.Create(CultureInfo.InvariantCulture, $"line {index} ") + new string('x', 80));
                if (index % 500 == 499)
                {
                    turn.Clock.Advance(Interval);
                }
            }

            output.Complete(Result(exitCode: 0));
        }

        // The sink is drained only now, so everything the flood produced sat in its queue at once: still under budget.
        var events = await turn.DrainAsync();

        AssertEx.False(events.Any(static streamEvent => streamEvent.Type == ChatStreamEventTypes.AssistantReconcile), "a flood must not overflow the turn's queue");
        var end = events.Last(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallOutput);
        AssertEx.True(end is { CommandRunning: false, CommandExitCode: 0, OutputReplace: true });
        AssertEx.True(end.OutputSkippedLines > 0);
        var tail = AssertEx.NotNull(end.OutputLines);
        AssertEx.True(tail[^1].Text.StartsWith("line 99999 ", StringComparison.Ordinal), "the final tail is the command's real end");
        AssertEx.True(tail.Sum(static line => line.Text.Length + 1) <= 16 * 1024);

        var replay = await turn.ResumeOpeningAsync();
        AssertEx.Equal(expected: 1, replay.Count(static streamEvent => streamEvent.Type == ChatStreamEventTypes.ToolCallRequested));
    }

    [Test]
    public async Task WithoutATurn_OrWithoutACallId_NoLiveOutputStarts()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
        var handle = await CreateHandleAsync(provider);

        using (AmbientToolCall.Enter(CallId, "run_python"))
        using (ToolCommandOutputScope.BindCurrentCall())
        {
            AssertEx.Null(ToolCommandOutputScope.StartCommand(provider, handle, "exec-1", WorkspaceRoot, "ls"), "no turn scope: unobserved");
        }

        using (ToolCommandOutputScope.Open(Guid.NewGuid(), Guid.NewGuid(), dispatcher, new ToolCommandStopRegistry(), TimeProvider.System, NullLogger.Instance))
        using (AmbientToolCall.Enter(string.Empty, "run_python"))
        using (ToolCommandOutputScope.BindCurrentCall())
        {
            AssertEx.Null(ToolCommandOutputScope.StartCommand(provider, handle, "exec-1", WorkspaceRoot, "ls"), "an id-less call has no card to stream into");
        }

        dispatcher.DidNotReceiveWithAnyArgs().ReportToolCallOutput(default!);
    }

    [Test]
    public async Task TheCompletedToolPart_CarriesTheLastCommandsTail_Once()
    {
        await using var turn = await TurnHarness.StartAsync();
        using var scope = turn.OpenScope();
        using (AmbientToolCall.Enter(CallId, "run_in_agent_home"))
        using (ToolCommandOutputScope.BindCurrentCall())
        {
            using (var first = turn.StartCommand("git status"))
            {
                first.Observer.OnLine(SandboxOutputSource.StandardOutput, "clean");
                first.Complete(Result(exitCode: 0));
            }

            using var second = turn.StartCommand("dotnet test");
            second.Observer.OnLine(SandboxOutputSource.StandardError, "1 failed");
            second.Complete(Result(exitCode: 1));
        }

        var completed = ToolCommandOutputScope.WithCommandTail(Lifecycle(turn.InvocationId, ToolCallLifecyclePhase.Completed));
        await turn.Dispatcher.ReportToolCallLifecycleAsync(completed);

        var part = turn.Parts.Snapshot().Single(static part => part.ToolCallId == CallId);
        AssertEx.Equal("dotnet test", part.LastCommandLabel, "a call that ran several commands keeps the last one's tail");
        AssertEx.Equal(expected: 0, part.OutputSkippedLines);
        var line = AssertEx.NotNull(part.OutputTail).Single();
        AssertEx.Equal(NodeChatToolOutputStreams.Stderr, line.Stream);
        AssertEx.Equal("1 failed", line.Text);
        AssertEx.Null(ToolCommandOutputScope.WithCommandTail(Lifecycle(turn.InvocationId, ToolCallLifecyclePhase.Completed)).OutputTail,
            "the tail is released once the completion read it");
    }

    private static SandboxCommandResult Result(int exitCode) =>
        new()
        {
            ExecutionId = "exec-1",
            ExitCode = exitCode,
            Completed = true
        };

    private static ToolCallLifecyclePayload Lifecycle(Guid invocationId, ToolCallLifecyclePhase phase) =>
        new()
        {
            InvocationId = invocationId,
            ToolCallId = CallId,
            ToolName = "run_python",
            Phase = phase,
            Arguments = phase == ToolCallLifecyclePhase.Requested ? "{}" : null,
            Result = phase == ToolCallLifecyclePhase.Completed ? "exit_code: 0" : null
        };

    private static Task<SandboxHandle> CreateHandleAsync(FakeSandboxRuntimeProvider provider) =>
        provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = "owner",
                NodeId = "node",
                ProviderName = provider.ProviderName,
                RuntimeProfile = "live-output-test",
                ManifestVersion = 1
            },
            RuntimeProfile = "live-output-test"
        });

    /// <summary>One running chat turn wired the way the stream service wires it, with a requested call on the card.</summary>
    private sealed class TurnHarness : IAsyncDisposable
    {
        private readonly ChatStreamEventForwarder _forwarder;
        private readonly IAsyncDisposable _lease;
        private readonly ChatStreamEventSink _sink;

        private TurnHarness(WorkerEventDispatcher dispatcher,
            InvocationResumeRegistry registry,
            ChatStreamEventSink sink,
            NodeChatPartAccumulator parts,
            ChatStreamEventForwarder forwarder,
            IAsyncDisposable lease,
            Guid invocationId,
            FakeSandboxRuntimeProvider provider,
            SandboxHandle handle)
        {
            Dispatcher = dispatcher;
            Registry = registry;
            _sink = sink;
            Parts = parts;
            _forwarder = forwarder;
            _lease = lease;
            InvocationId = invocationId;
            Provider = provider;
            Handle = handle;
        }

        public WorkerEventDispatcher Dispatcher { get; }

        public InvocationResumeRegistry Registry { get; }

        public NodeChatPartAccumulator Parts { get; }

        public Guid InvocationId { get; }

        public Guid MessageId { get; } = Guid.NewGuid();

        public ManualTimeProvider Clock { get; } = new();

        public FakeSandboxRuntimeProvider Provider { get; }

        public SandboxHandle Handle { get; }

        public static async Task<TurnHarness> StartAsync()
        {
            var dispatcher = new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(),
                Substitute.For<IInvocationHistory>(),
                NullLogger<WorkerEventDispatcher>.Instance,
                TimeProvider.System);
            var registry = new InvocationResumeRegistry(dispatcher, TimeProvider.System, NullLogger<InvocationResumeRegistry>.Instance);
            var invocationId = Guid.NewGuid();
            var conversationId = Guid.NewGuid();
            var correlation = new NodeChatMessageCorrelation
            {
                ConversationId = conversationId,
                MessageId = invocationId,
                RequestId = invocationId
            };
            var sequence = new NodeChatStreamSequence();
            var sink = new ChatStreamEventSink(correlation, sequence, new ChatStreamBudgetOptions(), TimeProvider.System);
            var parts = new NodeChatPartAccumulator();
            var forwarder = new ChatStreamEventForwarder(dispatcher,
                correlation,
                invocationId,
                Channel.CreateUnbounded<InvocationState>().Writer,
                sink,
                sequence,
                parts,
                TimeProvider.System);
            var lease = await dispatcher.ReportInvocationAssignedAsync(RuntimePackageBuilder.Valid().WithInvocationId(invocationId).WithConversationId(conversationId).Build());
            await dispatcher.ReportToolCallLifecycleAsync(Lifecycle(invocationId, ToolCallLifecyclePhase.Requested));
            var provider = new FakeSandboxRuntimeProvider(TimeProvider.System);
            var handle = await CreateHandleAsync(provider);
            return new TurnHarness(dispatcher, registry, sink, parts, forwarder, lease, invocationId, provider, handle);
        }

        // Synchronous on purpose: an AsyncLocal set here must reach the calling test.
        public IDisposable OpenScope() =>
            ToolCommandOutputScope.Open(InvocationId, MessageId, Dispatcher, new ToolCommandStopRegistry(), Clock, NullLogger.Instance);

        public ToolCommandOutput StartCommand(string? label) =>
            ToolCommandOutputScope.StartCommand(Provider, Handle, "exec-1", WorkspaceRoot, label)
            ?? throw new InvalidOperationException("No live output started: the turn scope or the bound call is missing.");

        public async Task<List<ChatStreamEvent>> DrainAsync()
        {
            _sink.Complete();
            var events = new List<ChatStreamEvent>();
            await foreach (var streamEvent in _sink.ReadAllAsync())
            {
                events.Add(streamEvent);
            }

            return events;
        }

        /// <summary>The replayed frames up to and including the snapshot, which the registry emits before it waits on live items.</summary>
        public async Task<List<ChatStreamEvent>> ResumeOpeningAsync()
        {
            var events = new List<ChatStreamEvent>();
            await foreach (var streamEvent in Registry.ResumeAsync(InvocationId))
            {
                events.Add(streamEvent);
                if (streamEvent.Type == ChatStreamEventTypes.AssistantSnapshot)
                {
                    break;
                }
            }

            return events;
        }

        public async ValueTask DisposeAsync()
        {
            _forwarder.Dispose();
            await _lease.DisposeAsync();
        }
    }
}
