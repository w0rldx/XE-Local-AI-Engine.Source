namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunHubTests
{
    [Test]
    public async Task Subscribe_JoinsBeforeSendingRetainedEvents()
    {
        var store = Substitute.For<IBenchmarkStore>();
        var events = Buffer();
        var runId = Guid.NewGuid();
        var retained = events.Append(runId,
            BenchmarkRunStreamEventKind.OutputDelta,
            new BenchmarkRunStreamPayload(Content: "safe output"));
        store.GetRunAsync(runId, Arg.Any<CancellationToken>()).Returns(Run(runId, lastStreamSequence: 0));
        using var fixture = CreateHub(store, events);

        await fixture.Hub.Subscribe(runId, afterSeq: 0);

        await fixture.Groups.Received(1).AddToGroupAsync("connection", BenchmarkRunHub.RunGroup(runId), Arg.Any<CancellationToken>());
        await fixture.Caller.Received(1).SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Is<object?[]>(arguments => MatchesEvent(arguments, retained)),
            Arg.Any<CancellationToken>());
        await fixture.Caller.DidNotReceive().SendCoreAsync(BenchmarkRunHubEvents.ReplayReset,
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Subscribe_WhenOnlyPersistentTerminalCursorExists_SendsReplayReset()
    {
        var store = Substitute.For<IBenchmarkStore>();
        var events = Buffer();
        var runId = Guid.NewGuid();
        store.GetRunAsync(runId, Arg.Any<CancellationToken>()).Returns(Run(runId, lastStreamSequence: 7));
        using var fixture = CreateHub(store, events);

        await fixture.Hub.Subscribe(runId, afterSeq: 3);

        await fixture.Caller.Received(1).SendCoreAsync(BenchmarkRunHubEvents.ReplayReset,
            Arg.Is<object?[]>(arguments => MatchesReset(arguments, runId, latestSequence: 7, runVersion: 4)),
            Arg.Any<CancellationToken>());
        await fixture.Caller.DidNotReceive().SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Subscribe_ReplaysReasoningAndToolPartEventKinds()
    {
        var store = Substitute.For<IBenchmarkStore>();
        var events = Buffer();
        var runId = Guid.NewGuid();
        var reasoning = events.Append(runId, BenchmarkRunStreamEventKind.ReasoningDelta, new BenchmarkRunStreamPayload(Content: "thinking..."));
        var toolCall = events.Append(runId,
            BenchmarkRunStreamEventKind.ToolCall,
            new BenchmarkRunStreamPayload(ToolCallId: "call-1", ToolName: "search", Arguments: "{}"));
        var toolResult = events.Append(runId,
            BenchmarkRunStreamEventKind.ToolResult,
            new BenchmarkRunStreamPayload(ToolCallId: "call-1", Result: "ok", IsError: false));
        store.GetRunAsync(runId, Arg.Any<CancellationToken>()).Returns(Run(runId, lastStreamSequence: 0));
        using var fixture = CreateHub(store, events);

        await fixture.Hub.Subscribe(runId, afterSeq: 0);

        await fixture.Caller.Received(1).SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Is<object?[]>(arguments => MatchesEvent(arguments, reasoning)),
            Arg.Any<CancellationToken>());
        await fixture.Caller.Received(1).SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Is<object?[]>(arguments => MatchesEvent(arguments, toolCall)),
            Arg.Any<CancellationToken>());
        await fixture.Caller.Received(1).SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Is<object?[]>(arguments => MatchesEvent(arguments, toolResult)),
            Arg.Any<CancellationToken>());
        await fixture.Caller.DidNotReceive().SendCoreAsync(BenchmarkRunHubEvents.ReplayReset,
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Subscribe_AfterTerminalPlaintextEviction_SendsReplayResetAndNoPlaintextEvents()
    {
        var store = Substitute.For<IBenchmarkStore>();
        var events = Buffer();
        var runId = Guid.NewGuid();
        var retained = events.Append(runId, BenchmarkRunStreamEventKind.OutputDelta, new BenchmarkRunStreamPayload(Content: "sensitive output"));
        events.EvictPlaintext(runId);
        store.GetRunAsync(runId, Arg.Any<CancellationToken>()).Returns(Run(runId, lastStreamSequence: retained.Sequence));
        using var fixture = CreateHub(store, events);

        await fixture.Hub.Subscribe(runId, afterSeq: 0);

        await fixture.Caller.Received(1).SendCoreAsync(BenchmarkRunHubEvents.ReplayReset,
            Arg.Is<object?[]>(arguments => MatchesReset(arguments, runId, latestSequence: retained.Sequence, runVersion: 4)),
            Arg.Any<CancellationToken>());
        await fixture.Caller.DidNotReceive().SendCoreAsync(BenchmarkRunHubEvents.Event,
            Arg.Any<object?[]>(),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public void Hub_RequiresOperatorAuthorization()
    {
        var authorize = typeof(BenchmarkRunHub).GetCustomAttribute<AuthorizeAttribute>();

        AssertEx.NotNull(authorize);
        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize!.Policy);
    }

    private static BenchmarkEventBuffer Buffer() =>
        new(Options.Create(new BenchmarkEventBufferOptions()));

    private static bool MatchesEvent(object?[] arguments, BenchmarkRunStreamEvent expected) =>
        arguments.Length == 1 && arguments[0] is BenchmarkRunStreamEvent streamEvent && streamEvent == expected;

    private static bool MatchesReset(object?[] arguments, Guid runId, long latestSequence, long runVersion) =>
        arguments.Length == 1
        && arguments[0] is BenchmarkRunReplayReset reset
        && reset.RunId == runId
        && reset.LatestSequence == latestSequence
        && reset.RunVersion == runVersion;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(IBenchmarkStore store, IBenchmarkEventBuffer events)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns("connection");
        context.ConnectionAborted.Returns(CancellationToken.None);
        var groups = Substitute.For<IGroupManager>();
        var caller = Substitute.For<ISingleClientProxy>();
        var clients = Substitute.For<IHubCallerClients>();
        clients.Caller.Returns(caller);
        var hub = new BenchmarkRunHub(store, events)
        {
            Context = context,
            Groups = groups,
            Clients = clients
        };
        return new HubFixture(hub, groups, caller);
    }

    private static BenchmarkRunRecord Run(Guid runId, long lastStreamSequence) =>
        new(runId,
            Guid.NewGuid(),
            ReadOnlyMemory<byte>.Empty,
            "model",
            PrimaryModelOrigin: null,
            "v1:fingerprint",
            "agent",
            AgentVersion: 1,
            RequestedContextTokens: 4096,
            BenchmarkPrimaryStatus.Succeeded,
            EffectiveContextTokens: 4096,
            DurationMs: 1,
            TotalTokens: 1,
            TokensPerSecond: 1,
            OutputPartsJson: null,
            lastStreamSequence,
            UserScore: null,
            PrimaryErrorMessage: null,
            Version: 4,
            CreatedAtUtc: 1,
            StartedAtUtc: 1,
            PrimaryCompletedAtUtc: 2,
            UpdatedAtUtc: 2);

    private sealed class HubFixture(BenchmarkRunHub hub, IGroupManager groups, ISingleClientProxy caller) : IDisposable
    {
        public BenchmarkRunHub Hub { get; } = hub;
        public IGroupManager Groups { get; } = groups;
        public ISingleClientProxy Caller { get; } = caller;

        public void Dispose() =>
            Hub.Dispose();
    }
}
