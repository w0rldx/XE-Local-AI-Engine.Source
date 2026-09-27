namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Common;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     Operator-only live transcription: the browser's audio goes in here and committed segments come back out.
/// </summary>
/// <remarks>
///     <b>A disconnect IS meaningful on this hub</b>, the opposite of <see cref="GraphWorkflowRunHub" />: a live
///     session is fed by the tab that opened it, so once every connection watching it is gone nothing will ever push
///     another frame, and losing the last one arms the abandonment grace in the registry rather than idling forever.
///     The hub itself holds no audio state — it validates a frame and forwards it to
///     <see cref="ILiveTranscriptionSessionRegistry.PushAudioAsync" />, which an in-host capture source calls directly.
/// </remarks>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class TranscriptionHub : Hub
{
    /// <summary>
    ///     The largest frame one <see cref="PushAudioFrame" /> may carry: 32 KiB, one second of 16 kHz mono int16.
    /// </summary>
    /// <remarks>
    ///     Enforced here, in the application, and NOT by the transport: this node raises SignalR's receive ceiling to
    ///     512 KB for other hubs, so the default 32 KB message limit is not the thing refusing an oversized frame.
    /// </remarks>
    public const int MaxFrameBytes = 32 * 1024;

    /// <summary>
    ///     Where this connection's subscribed session ids live, so a disconnect can detach from each of them.
    /// </summary>
    /// <remarks>
    ///     A plain <see cref="HashSet{T}" /> is enough: SignalR dispatches at most one invocation per connection at a
    ///     time by default, so nothing here races with itself.
    /// </remarks>
    private const string SubscribedSessionsKey = "transcription.subscribed-sessions";

    private readonly ILiveTranscriptionSessionRegistry _live;
    private readonly TranscriptionOptions _options;
    private readonly ITranscriptionService _sessions;

    public TranscriptionHub(ITranscriptionService sessions,
        ILiveTranscriptionSessionRegistry live,
        IOptions<TranscriptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(live);
        _live = live;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(sessions);
        _sessions = sessions;
    }

    /// <summary>
    ///     Joins the session's group and replays the committed rows after <paramref name="afterSeq" />.
    /// </summary>
    public async Task<TranscriptionSessionSubscriptionSnapshot> SubscribeSession(Guid sessionId, long afterSeq)
    {
        if (!_options.Enabled)
        {
            throw new HubException(TranscriptionHubErrors.Disabled);
        }

        if (sessionId == Guid.Empty)
        {
            throw new HubException(TranscriptionHubErrors.SessionRequired);
        }

        if (afterSeq < 0)
        {
            throw new HubException(TranscriptionHubErrors.InvalidWatermark);
        }

        var cancellationToken = Context.ConnectionAborted;

        // Join BEFORE anything is read, status as well as replay: reading first leaves a window in which a session that ends
        // in between tells nobody, and the snapshot says Transcribing forever. Joining first can only duplicate, and the client merges by exact Seq.
        await Groups.AddToGroupAsync(Context.ConnectionId, TranscriptionHubGroups.Session(sessionId), cancellationToken);

        // The summary, not the session with its transcript: this needs a status and nothing else, and the replay
        // below is already bounded by the cap. Reading the whole session here decrypted every segment twice.
        var session = await _sessions.GetSessionSummaryAsync(sessionId, cancellationToken);

        // A live session with no row is the persist-free (dictation) case and is legitimate; an id that is neither a
        // row nor a live session is not — and it leaves the group it was speculatively added to.
        if (session is null && !_live.IsLive(sessionId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, TranscriptionHubGroups.Session(sessionId), cancellationToken);
            throw new HubException(TranscriptionHubErrors.SessionNotFound);
        }

        // Registered on SUBSCRIBE, not on the first frame: a connection that subscribes and is then denied its microphone
        // never pushes anything, and registering on push would leave that session with no browser attached, so its disconnect would arm nothing.
        Track(sessionId);
        _live.NoteBrowserAttached(sessionId, Context.ConnectionId);

        if (session is null)
        {
            // Nothing was ever stored, so a reconnect resumes the stream and recovers nothing it missed.
            return new TranscriptionSessionSubscriptionSnapshot
            {
                SessionId = sessionId,
                Status = TranscriptionSessionStatus.Transcribing.ToString(),
                LastSeq = afterSeq,
                Segments = [],
                ReplayTruncated = false
            };
        }

        var (replayed, truncated) = await ReplayWindow.ReadAsync(_options.SegmentReplayLimit,
            limit => _sessions.ListSegmentsAfterAsync(sessionId, afterSeq, limit, cancellationToken));

        // The watermark is the last row the subscriber was actually HANDED: the session's own maximum would skip every
        // row the cap cut off, for good — nothing replays them a second time. An empty page keeps the caller's own watermark: it has seen nothing new and has therefore moved nowhere.
        var lastSeq = replayed.Count == 0 ? afterSeq : replayed[^1].Seq;
        return new TranscriptionSessionSubscriptionSnapshot
        {
            SessionId = sessionId,
            Status = session.Status.ToString(),
            LastSeq = lastSeq,
            Segments = [.. replayed.Select(static segment => segment.ToResponse())],
            ReplayTruncated = truncated
        };
    }

    /// <summary>
    ///     Leaves the session's group and drops this connection's attachment, which arms the abandonment grace when
    ///     it was the last one — the same thing a disconnect does, because it means the same thing.
    /// </summary>
    public Task UnsubscribeSession(Guid sessionId)
    {
        Untrack(sessionId);
        _live.NoteBrowserDetached(sessionId, Context.ConnectionId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, TranscriptionHubGroups.Session(sessionId), Context.ConnectionAborted);
    }

    /// <summary>
    ///     Hands one frame of 16 kHz mono little-endian int16 PCM to the session's lane for
    ///     <paramref name="channel" />.
    /// </summary>
    /// <remarks>
    ///     Validation and a forward, nothing else: no buffering, no reshaping, no interpretation of the audio. A frame
    ///     that cannot be accepted is refused with a typed error rather than dropped, because a client that is told
    ///     nothing keeps capturing into a session that stopped listening.
    /// </remarks>
    public async Task PushAudioFrame(Guid sessionId, int channel, byte[] pcm16k)
    {
        if (!_options.Enabled)
        {
            throw new HubException(TranscriptionHubErrors.Disabled);
        }

        if (sessionId == Guid.Empty)
        {
            throw new HubException(TranscriptionHubErrors.SessionRequired);
        }

        if (pcm16k is null or { Length: 0 })
        {
            // A capture callback that fires before the first buffer fills is a hiccup, not an error.
            return;
        }

        if (pcm16k.Length > MaxFrameBytes)
        {
            throw new HubException(TranscriptionHubErrors.FrameTooLarge);
        }

        if (pcm16k.Length % 2 != 0)
        {
            // Half a sample shifts every later sample by one byte and turns the rest of the lane into noise.
            throw new HubException(TranscriptionHubErrors.FrameMisaligned);
        }

        if (!Enum.IsDefined((TranscriptChannel)channel))
        {
            throw new HubException(TranscriptionHubErrors.UnknownChannel);
        }

        // The registry drops a frame for an unknown or already-ending session by design, so the refusal has to be
        // made here or "never silently dropped" would not hold.
        if (!_live.IsLive(sessionId))
        {
            if (_live.IsRegistered(sessionId))
            {
                // Draining after a Stop or the buffered-audio cap: a refusal makes the client abort, which cancels the drain.
                return;
            }

            throw new HubException(TranscriptionHubErrors.NotTranscribing);
        }

        // A push from a connection that never subscribed is legitimate and attaches it.
        Track(sessionId);
        _live.NoteBrowserAttached(sessionId, Context.ConnectionId);

        try
        {
            await _live.PushAudioAsync(sessionId, (TranscriptChannel)channel, pcm16k, Context.ConnectionAborted);
        }
        catch (ArgumentException)
        {
            // A channel this session does not carry — a client sending You to a Microphone session. Defined here rather
            // than left as an unhandled argument error, so the client gets the same typed code as for an unknown channel.
            throw new HubException(TranscriptionHubErrors.UnknownChannel);
        }
    }

    /// <summary>
    ///     Ends the session: the lanes flush, the last commits persist, the status push goes out and the entry is
    ///     disposed.
    /// </summary>
    public async Task EndSession(Guid sessionId)
    {
        if (!_options.Enabled)
        {
            throw new HubException(TranscriptionHubErrors.Disabled);
        }

        if (sessionId == Guid.Empty)
        {
            throw new HubException(TranscriptionHubErrors.SessionRequired);
        }

        // Not the connection's token: an end that stopped halfway because the caller navigated away would leave the
        // lanes running and the row in Transcribing.
        await _live.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None);
    }

    /// <summary>
    ///     Detaches this connection from every session it subscribed to or pushed into. The session whose last
    ///     connection this was arms its abandonment grace.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue(SubscribedSessionsKey, out var tracked) && tracked is HashSet<Guid> subscribed)
        {
            foreach (var sessionId in subscribed)
            {
                _live.NoteBrowserDetached(sessionId, Context.ConnectionId);
            }
        }

        await base.OnDisconnectedAsync(exception);
    }

    private void Track(Guid sessionId)
    {
        if (Context.Items.TryGetValue(SubscribedSessionsKey, out var tracked) && tracked is HashSet<Guid> subscribed)
        {
            _ = subscribed.Add(sessionId);
            return;
        }

        Context.Items[SubscribedSessionsKey] = new HashSet<Guid>
        {
            sessionId
        };
    }

    private void Untrack(Guid sessionId)
    {
        if (Context.Items.TryGetValue(SubscribedSessionsKey, out var tracked) && tracked is HashSet<Guid> subscribed)
        {
            _ = subscribed.Remove(sessionId);
        }
    }
}
