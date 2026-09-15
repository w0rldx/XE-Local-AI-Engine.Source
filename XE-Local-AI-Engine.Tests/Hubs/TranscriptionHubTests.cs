namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Hubs;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The live transcription hub's own contract: what a subscriber is handed, what a frame must look like to be
///     forwarded, and what a connection's departure means. Everything behind it — the registry, the service — is
///     substituted, because what is under test is the hub's refusals and its watermark arithmetic.
/// </summary>
public sealed class TranscriptionHubTests
{
    /// <summary>The replay window the hub is configured with below, read from the OPTION as the product does.</summary>
    private const int ReplayLimit = 5;

    private const string ConnectionId = "connection";

    private static readonly Guid SessionId = Guid.Parse("77777777-7777-7777-7777-777777777777");

    [Test]
    public async Task SubscribeSession_JoinsTheGroupBeforeReadingTheReplay()
    {
        var sessions = Sessions();
        using var fixture = CreateHub(sessions, Live());

        _ = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        // The other order leaves a window in which a segment committed between the read and the join reaches nobody.
        Received.InOrder(() =>
        {
            fixture.Groups.AddToGroupAsync(ConnectionId, $"transcription-session-{SessionId:N}", Arg.Any<CancellationToken>());
            sessions.ListSegmentsAfterAsync(SessionId, 0, ReplayLimit + 1, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task SubscribeSession_ReturnsOnlyTheSegmentsAfterTheWatermark()
    {
        using var fixture = CreateHub(Sessions(Segment(8), Segment(9)), Live());

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 7).ConfigureAwait(false);

        AssertEx.Equal(SessionId, snapshot.SessionId);
        AssertEx.Equal("Transcribing", snapshot.Status);
        AssertEx.Equal(expected: 2, snapshot.Segments.Count);
        AssertEx.Equal(expected: 8L, snapshot.Segments[0].Seq);
        AssertEx.Equal(expected: 9L, snapshot.LastSeq);
        AssertEx.False(snapshot.ReplayTruncated);

        // The channel spelling the replay carries is the one the live pushes carry, or a resuming client would see
        // the same lane under two names and render it as two.
        AssertEx.Equal("Mono", snapshot.Segments[0].Channel);
    }

    [Test]
    public async Task SubscribeSession_WithNothingAfterTheWatermark_KeepsTheCallersOwnWatermark()
    {
        using var fixture = CreateHub(Sessions(), Live());

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 4).ConfigureAwait(false);

        AssertEx.Empty(snapshot.Segments);
        AssertEx.Equal(expected: 4L, snapshot.LastSeq, "it has seen nothing new, so it has moved nowhere.");
    }

    [Test]
    public async Task SubscribeSession_AtTheReplayCap_IsNotTruncated()
    {
        using var fixture = CreateHub(Sessions([.. Enumerable.Range(1, ReplayLimit).Select(seq => Segment(seq))]), Live());

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayLimit, snapshot.Segments.Count);
        AssertEx.False(snapshot.ReplayTruncated);
    }

    [Test]
    public async Task SubscribeSession_OneOverTheReplayCap_TruncatesAndSaysSo()
    {
        using var fixture = CreateHub(Sessions([.. Enumerable.Range(1, ReplayLimit + 1).Select(seq => Segment(seq))]), Live());

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        AssertEx.Equal(ReplayLimit, snapshot.Segments.Count);
        AssertEx.True(snapshot.ReplayTruncated, "the cap is observed one row over it, never inferred from a full page.");
        AssertEx.Equal((long)ReplayLimit,
            snapshot.LastSeq,
            "the cursor is the last row DELIVERED: the session's own maximum would skip every row the cap cut off.");
    }

    /// <summary>
    ///     What the cursor is FOR. Nothing replays a committed segment a second time, so a watermark past the rows the
    ///     cap withheld loses them permanently.
    /// </summary>
    [Test]
    public async Task SubscribeSession_ResumedFromItsOwnCursor_DeliversExactlyTheSegmentsTheCapCutOff()
    {
        using var fixture = CreateHub(PagingSessions([.. Enumerable.Range(1, 7).Select(seq => Segment(seq))]), Live());

        var first = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);
        AssertEx.True(first.ReplayTruncated);
        AssertEx.Equal(expected: 5L, first.LastSeq);

        var second = await fixture.Hub.SubscribeSession(SessionId, first.LastSeq).ConfigureAwait(false);
        AssertEx.False(second.ReplayTruncated, "two rows are left and the cap is five.");
        AssertEx.Equal(expected: 2, second.Segments.Count, "exactly the rest, with nothing repeated.");
        AssertEx.Equal(expected: 6L, second.Segments[0].Seq, "and in order, starting one past the cursor.");
        AssertEx.Equal(expected: 7L, second.LastSeq);

        var third = await fixture.Hub.SubscribeSession(SessionId, second.LastSeq).ConfigureAwait(false);
        AssertEx.Empty(third.Segments);
        AssertEx.Equal(second.LastSeq, third.LastSeq, "a caught-up subscriber keeps the watermark it came with.");
    }

    /// <summary>
    ///     A dictation session has no row and never will: it subscribes to the live stream and recovers nothing,
    ///     because nothing was ever stored for it to recover.
    /// </summary>
    [Test]
    public async Task SubscribeSession_ForALiveSessionWithNoRow_JoinsWithAnEmptyReplay()
    {
        var sessions = Sessions();
        sessions.GetSessionSummaryAsync(SessionId, Arg.Any<CancellationToken>()).Returns((TranscriptionSessionSummaryView?)null);
        using var fixture = CreateHub(sessions, Live());

        var snapshot = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 3).ConfigureAwait(false);

        AssertEx.Empty(snapshot.Segments);
        AssertEx.False(snapshot.ReplayTruncated);
        AssertEx.Equal(expected: 3L, snapshot.LastSeq);
        AssertEx.Equal("Transcribing", snapshot.Status);
        await fixture.Groups.Received(1).AddToGroupAsync(ConnectionId, $"transcription-session-{SessionId:N}", Arg.Any<CancellationToken>());
        await sessions.DidNotReceiveWithAnyArgs().ListSegmentsAfterAsync(Guid.Empty, default, default, default);
    }

    /// <summary>
    ///     An unknown id is refused, and the connection does not stay in the group the join speculatively put it in.
    /// </summary>
    /// <remarks>
    ///     The join now happens BEFORE the session is read, so that a session ending in that window still reaches
    ///     this connection with its terminal push. The cost is that an unknown id is briefly a member of a group
    ///     nothing publishes to, so the refusal path has to leave it again — which is what this asserts. It replaces
    ///     an earlier assertion that no group was ever joined; that contract is gone on purpose.
    /// </remarks>
    [Test]
    public async Task SubscribeSession_WhenTheSessionIsNeitherStoredNorLive_LeavesTheGroupAndThrows()
    {
        var sessions = Sessions();
        sessions.GetSessionSummaryAsync(SessionId, Arg.Any<CancellationToken>()).Returns((TranscriptionSessionSummaryView?)null);
        var live = Live();
        live.IsLive(SessionId).Returns(false);
        using var fixture = CreateHub(sessions, live);

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(SessionId, afterSeq: 0)).ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.SessionNotFound, error.Message);
        Received.InOrder(() =>
        {
            _ = fixture.Groups.AddToGroupAsync(ConnectionId, $"transcription-session-{SessionId:N}", Arg.Any<CancellationToken>());
            _ = fixture.Groups.RemoveFromGroupAsync(ConnectionId, $"transcription-session-{SessionId:N}", Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task SubscribeSession_WithAnEmptySessionId_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Sessions(), Live());

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(Guid.Empty, afterSeq: 0)).ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.SessionRequired, error.Message);
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeSession_WithANegativeWatermark_ThrowsWithoutJoiningAGroup()
    {
        using var fixture = CreateHub(Sessions(), Live());

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(SessionId, afterSeq: -1)).ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.InvalidWatermark, error.Message);
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    [Test]
    public async Task SubscribeSession_WhenTranscriptionIsDisabled_ThrowsWithoutReachingTheService()
    {
        var sessions = Sessions();
        var live = Live();
        using var fixture = CreateHub(sessions, live, enabled: false);

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.SubscribeSession(SessionId, afterSeq: 0)).ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.Disabled, error.Message);
        AssertEx.Empty(sessions.ReceivedCalls());
        AssertEx.Empty(live.ReceivedCalls());
        await fixture.Groups.DidNotReceiveWithAnyArgs().AddToGroupAsync(default!, default!, default);
    }

    /// <summary>
    ///     Registered on subscribe, not on the first frame: a connection denied its microphone never pushes anything,
    ///     and a session with no browser attached arms nothing when that connection goes away.
    /// </summary>
    [Test]
    public async Task SubscribeSession_NotesTheBrowserAttachedBeforeAnyFrameArrives()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        _ = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);

        live.Received(1).NoteBrowserAttached(SessionId, ConnectionId);
    }

    [Test]
    public async Task UnsubscribeSession_LeavesTheGroupAndDropsTheAttachment()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        await fixture.Hub.UnsubscribeSession(SessionId).ConfigureAwait(false);

        await fixture.Groups.Received(1).RemoveFromGroupAsync(ConnectionId, $"transcription-session-{SessionId:N}", Arg.Any<CancellationToken>());
        live.Received(1).NoteBrowserDetached(SessionId, ConnectionId);
    }

    [Test]
    public async Task PushAudioFrame_ForwardsTheFrameToTheRegistry()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);
        var frame = new byte[64];

        await fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Others, frame).ConfigureAwait(false);

        await live.Received(1).PushAudioAsync(SessionId,
            TranscriptChannel.Others,
            Arg.Is<ReadOnlyMemory<byte>>(pcm => pcm.Length == frame.Length),
            Arg.Any<CancellationToken>());

        // A push from a connection that never subscribed is legitimate, and it attaches that connection.
        live.Received(1).NoteBrowserAttached(SessionId, ConnectionId);
    }

    [Test]
    public async Task PushAudioFrame_OnANonTranscribingSession_ThrowsTheTypedHubError()
    {
        var live = Live();
        live.IsLive(SessionId).Returns(false);
        using var fixture = CreateHub(Sessions(), live);

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Mono, new byte[32]))
                                  .ConfigureAwait(false);

        // The registry drops a frame for a session that is not live; without this refusal the client would keep
        // capturing into nothing and never be told.
        AssertEx.Equal(TranscriptionHubErrors.NotTranscribing, error.Message);
        await live.DidNotReceiveWithAnyArgs().PushAudioAsync(Guid.Empty, default, default, default);
    }

    [Test]
    public async Task PushAudioFrame_LargerThanThirtyTwoKilobytes_IsRejected()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        var error = await AssertEx
                          .ThrowsAsync<HubException>(() =>
                              fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Mono, new byte[TranscriptionHub.MaxFrameBytes + 2]))
                          .ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.FrameTooLarge, error.Message);
        await live.DidNotReceiveWithAnyArgs().PushAudioAsync(Guid.Empty, default, default, default);
    }

    [Test]
    public async Task PushAudioFrame_AtExactlyThirtyTwoKilobytes_IsAccepted()
    {
        // The control for the case above: a cap enforced one byte early would pass it while refusing a legal frame.
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        await fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Mono, new byte[TranscriptionHub.MaxFrameBytes]).ConfigureAwait(false);

        await live.ReceivedWithAnyArgs(1).PushAudioAsync(Guid.Empty, default, default, default);
    }

    [Test]
    public async Task PushAudioFrame_WithAnOddByteCount_IsRejected()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Mono, new byte[33]))
                                  .ConfigureAwait(false);

        // Half a sample shifts every later sample by one byte and turns the rest of the lane into noise.
        AssertEx.Equal(TranscriptionHubErrors.FrameMisaligned, error.Message);
        await live.DidNotReceiveWithAnyArgs().PushAudioAsync(Guid.Empty, default, default, default);
    }

    [Test]
    [Arguments(-1)]
    [Arguments(3)]
    [Arguments(99)]
    public async Task PushAudioFrame_WithAnUndefinedChannel_IsRejected(int channel)
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.PushAudioFrame(SessionId, channel, new byte[32])).ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.UnknownChannel, error.Message);
        await live.DidNotReceiveWithAnyArgs().PushAudioAsync(Guid.Empty, default, default, default);
    }

    /// <summary>
    ///     A channel that exists but that this session does not carry — <c>You</c> on a microphone-only session. The
    ///     registry answers that with an <see cref="ArgumentException" />, which is not something a browser can match on.
    /// </summary>
    [Test]
    public async Task PushAudioFrame_WithAChannelTheSessionDoesNotCarry_IsRejectedWithTheTypedError()
    {
        var live = Live();
        live.PushAudioAsync(SessionId, TranscriptChannel.You, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new ArgumentException("this session carries no You lane"));
        using var fixture = CreateHub(Sessions(), live);

        var error = await AssertEx.ThrowsAsync<HubException>(() => fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.You, new byte[32]))
                                  .ConfigureAwait(false);

        AssertEx.Equal(TranscriptionHubErrors.UnknownChannel, error.Message);
    }

    [Test]
    public async Task PushAudioFrame_WithAnEmptyOrAbsentFrame_IsANoOp()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        await fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Mono, []).ConfigureAwait(false);
        await fixture.Hub.PushAudioFrame(SessionId, (int)TranscriptChannel.Mono, null!).ConfigureAwait(false);

        // A capture callback firing before its first buffer fills is a hiccup, not something to fail a session over.
        await live.DidNotReceiveWithAnyArgs().PushAudioAsync(Guid.Empty, default, default, default);
    }

    [Test]
    public async Task EndSession_EndsTheSessionAsCompleted()
    {
        var live = Live();
        using var fixture = CreateHub(Sessions(), live);

        await fixture.Hub.EndSession(SessionId).ConfigureAwait(false);

        await live.Received(1).EndAsync(SessionId, LiveEndReason.Completed, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Unlike the graph-workflow hub, a disconnect means something here: nothing else will feed this session, so
    ///     the registry has to learn the connection is gone in order to arm the abandonment grace.
    /// </summary>
    [Test]
    public async Task OnDisconnected_NotesTheConnectionLostForEverySessionItPushedTo()
    {
        var other = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var live = Live();
        live.IsLive(other).Returns(true);
        using var fixture = CreateHub(Sessions(), live);

        _ = await fixture.Hub.SubscribeSession(SessionId, afterSeq: 0).ConfigureAwait(false);
        await fixture.Hub.PushAudioFrame(other, (int)TranscriptChannel.Mono, new byte[32]).ConfigureAwait(false);

        await fixture.Hub.OnDisconnectedAsync(exception: null).ConfigureAwait(false);

        live.Received(1).NoteBrowserDetached(SessionId, ConnectionId);
        live.Received(1).NoteBrowserDetached(other, ConnectionId);
    }

    [Test]
    public void Hub_RequiresOperatorAuthorization()
    {
        var authorize = typeof(TranscriptionHub).GetCustomAttribute<AuthorizeAttribute>();

        AssertEx.NotNull(authorize);
        AssertEx.Equal(NodeAuthorizationPolicies.Operator, authorize!.Policy);
        AssertEx.Equal(JwtBearerDefaults.AuthenticationScheme, authorize.AuthenticationSchemes);
    }

    /// <summary>
    ///     The snapshot must report the status the session actually reached, even when it reached it after this
    ///     connection joined the group.
    /// </summary>
    /// <remarks>
    ///     Joining first is what makes the terminal push reachable at all. Reading first left a window in which the
    ///     session ended, published to nobody, and handed this caller a snapshot that said <c>Transcribing</c>
    ///     forever — a live session on screen that nothing would ever finish.
    /// </remarks>
    [Test]
    public async Task SubscribeSession_WhenTheSessionEndsWhileItIsRead_ReportsTheTerminalStatus()
    {
        var sessions = Sessions();
        var joined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // The status read is held open until the test has flipped the row, standing in for an end that lands in
        // exactly that window.
        _ = sessions.GetSessionSummaryAsync(SessionId, Arg.Any<CancellationToken>())
                    .Returns(_ => joined.Task.ContinueWith(
                        static _ => (TranscriptionSessionSummaryView?)Summary(TranscriptionSessionStatus.Completed),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default));

        using var fixture = CreateHub(sessions, Live());
        var subscribing = fixture.Hub.SubscribeSession(SessionId, afterSeq: 0);

        await fixture.Groups.Received(1).AddToGroupAsync(ConnectionId, $"transcription-session-{SessionId:N}", Arg.Any<CancellationToken>());
        joined.SetResult();
        var snapshot = await subscribing.ConfigureAwait(false);

        AssertEx.Equal("Completed", snapshot.Status, "The snapshot reports where the session ended up, not where it was when the caller asked.");
    }

    private static ITranscriptionService Sessions(params TranscriptSegmentView[] segments)
    {
        var sessions = Substitute.For<ITranscriptionService>();
        sessions.GetSessionSummaryAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Summary());
        sessions.ListSegmentsAfterAsync(SessionId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(segments.Length == 0 ? [] : segments);
        return sessions;
    }

    /// <summary>
    ///     A service that actually PAGES — it honours the watermark and the limit it is called with. A substitute that
    ///     answers the same rows whatever it is asked cannot see a gap, which is the whole point of the cursor test.
    /// </summary>
    private static ITranscriptionService PagingSessions(IReadOnlyList<TranscriptSegmentView> all)
    {
        var sessions = Substitute.For<ITranscriptionService>();
        sessions.GetSessionSummaryAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Summary());
        sessions.ListSegmentsAfterAsync(SessionId, Arg.Any<long>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    IReadOnlyList<TranscriptSegmentView> page =
                        [.. all.Where(segment => segment.Seq > call.ArgAt<long>(1)).Take(call.ArgAt<int>(2))];
                    return page;
                });
        return sessions;
    }

    private static ILiveTranscriptionSessionRegistry Live()
    {
        var live = Substitute.For<ILiveTranscriptionSessionRegistry>();
        live.IsLive(Arg.Any<Guid>()).Returns(true);
        return live;
    }

    /// <summary>
    ///     What the hub actually reads: a summary, never the session with its transcript. Subscribing needs one
    ///     column, and loading every segment to get it decrypted the whole transcript twice.
    /// </summary>
    private static TranscriptionSessionSummaryView Summary(TranscriptionSessionStatus status = TranscriptionSessionStatus.Transcribing) =>
        new()
        {
            Id = SessionId,
            Title = "live",
            CreatedAtUtc = 1_700_000_000_000,
            UpdatedAtUtc = 1_700_000_000_000,
            Status = status,
            SourceKind = TranscriptionSourceKind.Microphone,
            ModelId = "ggml-tiny",
            ConfigJson = "{\"languageMode\":\"auto\"}",
            SegmentCount = 0
        };

    private static TranscriptSegmentView Segment(long seq) =>
        new()
        {
            Id = Guid.NewGuid(),
            Seq = seq,
            StartMs = seq * 1_000,
            EndMs = (seq * 1_000) + 900,
            Text = $"segment {seq}",
            Channel = TranscriptChannel.Mono,
            Confidence = 0.9
        };

    [SuppressMessage("Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "HubFixture takes ownership of the constructed hub and every test disposes the fixture.")]
    private static HubFixture CreateHub(ITranscriptionService sessions, ILiveTranscriptionSessionRegistry live, bool enabled = true)
    {
        var context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(ConnectionId);
        context.ConnectionAborted.Returns(CancellationToken.None);
        context.Items.Returns(new Dictionary<object, object?>());
        var groups = Substitute.For<IGroupManager>();
        var clients = Substitute.For<IHubCallerClients>();
        var hub = new TranscriptionHub(sessions,
            live,
            Options.Create(new TranscriptionOptions
            {
                Enabled = enabled,
                SegmentReplayLimit = ReplayLimit
            }))
        {
            Context = context,
            Groups = groups,
            Clients = clients
        };
        return new HubFixture(hub, groups);
    }

    private sealed record HubFixture(TranscriptionHub Hub, IGroupManager Groups) : IDisposable
    {
        public void Dispose() =>
            Hub.Dispose();
    }
}
