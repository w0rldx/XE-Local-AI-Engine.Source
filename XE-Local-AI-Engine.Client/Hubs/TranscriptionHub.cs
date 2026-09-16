namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;
using XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>The SignalR method names a live transcription client listens on.</summary>
public static class TranscriptionHubEvents
{
    public const string SegmentCommitted = "transcriptionSegmentCommitted";

    public const string PartialUpdated = "transcriptionPartialUpdated";

    public const string SessionStatusChanged = "transcriptionSessionStatusChanged";
}

/// <summary>
///     The refusal codes this hub throws as <see cref="HubException" /> messages. Stable strings the client matches
///     on: a live capture UI has to tell "your session ended" apart from "your frame was malformed", and an exception
///     message written inline would change the contract the next time somebody reworded it.
/// </summary>
public static class TranscriptionHubErrors
{
    public const string Disabled = "transcription-disabled";

    public const string SessionRequired = "transcription-session-required";

    public const string SessionNotFound = "transcription-session-not-found";

    public const string NotTranscribing = "transcription-session-not-transcribing";

    public const string FrameTooLarge = "transcription-frame-too-large";

    public const string FrameMisaligned = "transcription-frame-misaligned";

    public const string UnknownChannel = "transcription-unknown-channel";

    public const string InvalidWatermark = "transcription-invalid-watermark";
}

/// <summary>
///     What a subscriber gets back: the session's status, the transcript rows after its watermark, the watermark it
///     may resume from, and whether the replay cap cut the page short.
/// </summary>
/// <param name="SessionId">The session subscribed to.</param>
/// <param name="Status">The session's persisted status, or <c>Transcribing</c> for a persist-free session.</param>
/// <param name="LastSeq">The last row DELIVERED, or the caller's own watermark when nothing was.</param>
/// <param name="Segments">The replayed rows, ascending by sequence.</param>
/// <param name="ReplayTruncated">Whether rows beyond this page exist; read one row past the cap, never inferred.</param>
public sealed record TranscriptionSessionSubscriptionSnapshot(
    Guid SessionId,
    string Status,
    long LastSeq,
    IReadOnlyList<TranscriptSegmentResponse> Segments,
    bool ReplayTruncated);

/// <summary>One committed transcript row, pushed as it is allocated its sequence.</summary>
public sealed record TranscriptSegmentCommittedPush(
    Guid SessionId,
    long Seq,
    long StartMs,
    long EndMs,
    string Text,
    string Channel,
    double? Confidence);

/// <summary>One lane's provisional text. Never persisted and never sequenced: it is replaced, not accumulated.</summary>
public sealed record TranscriptPartialUpdatedPush(Guid SessionId, string Channel, string Text);

/// <summary>A live session reached its terminal state, and this is what it was.</summary>
public sealed record TranscriptionSessionStatusPush(Guid SessionId, string Status);

/// <summary>
///     Operator-only live transcription: the browser's audio goes in here and committed segments come back out.
///     <para>
///         <b>A disconnect IS meaningful on this hub</b> — the opposite of <see cref="GraphWorkflowRunHub" />, whose
///         doc comment says a run outlives the tab. A live session is fed by the tab that opened it: once every
///         connection watching it is gone, nothing will ever push another frame, so losing the last connection arms
///         the abandonment grace in the registry and the session ends rather than idling forever.
///     </para>
///     <para>
///         The hub itself holds no audio state. It validates a frame and forwards it to
///         <see cref="ILiveTranscriptionSessionRegistry.PushAudioAsync" />, which an in-host capture source calls
///         directly — two callers of one method rather than two paths.
///     </para>
/// </summary>
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Policy = NodeAuthorizationPolicies.Operator)]
public sealed class TranscriptionHub(
    ITranscriptionService sessions,
    ILiveTranscriptionSessionRegistry live,
    IOptions<TranscriptionOptions> options) : Hub
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

    private readonly ILiveTranscriptionSessionRegistry _live = live ?? throw new ArgumentNullException(nameof(live));
    private readonly TranscriptionOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly ITranscriptionService _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

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

        // Join BEFORE anything is read — the status as well as the replay. Reading first leaves a window in which a
        // session that ends in between publishes its terminal status to nobody, and the snapshot this caller gets
        // says Transcribing forever. Joining first can only duplicate, and the client merges by exact Seq.
        await Groups.AddToGroupAsync(Context.ConnectionId, TranscriptionHubGroups.Session(sessionId), cancellationToken).ConfigureAwait(false);

        // The summary, not the session with its transcript: this needs a status and nothing else, and the replay
        // below is already bounded by the cap. Reading the whole session here decrypted every segment twice.
        var session = await _sessions.GetSessionSummaryAsync(sessionId, cancellationToken).ConfigureAwait(false);

        // A live session with no row is the persist-free (dictation) case and is legitimate; an id that is neither a
        // row nor a live session is not — and it leaves the group it was speculatively added to.
        if (session is null && !_live.IsLive(sessionId))
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, TranscriptionHubGroups.Session(sessionId), cancellationToken).ConfigureAwait(false);
            throw new HubException(TranscriptionHubErrors.SessionNotFound);
        }

        // Registered on SUBSCRIBE, not on the first frame: a connection that subscribes and is then denied its
        // microphone never pushes anything, and registering it on push would leave that session with no browser
        // attached, so its disconnect would arm nothing.
        Track(sessionId);
        _live.NoteBrowserAttached(sessionId, Context.ConnectionId);

        if (session is null)
        {
            // Nothing was ever stored, so a reconnect resumes the stream and recovers nothing it missed.
            return new TranscriptionSessionSubscriptionSnapshot(sessionId,
                TranscriptionSessionStatus.Transcribing.ToString(),
                afterSeq,
                [],
                ReplayTruncated: false);
        }

        // One over the cap, so "there is more" is observed rather than inferred from a full page.
        var replayLimit = _options.SegmentReplayLimit;
        var segments = await _sessions.ListSegmentsAfterAsync(sessionId, afterSeq, replayLimit + 1, cancellationToken).ConfigureAwait(false);
        var replayed = segments.Take(replayLimit).ToList();

        // The watermark is the last row the subscriber was actually HANDED. Taking the session's own maximum would
        // skip every row the cap cut off, for good: nothing replays them a second time. An empty page keeps the
        // caller's own watermark, because it has seen nothing new and has therefore moved nowhere.
        var lastSeq = replayed.Count == 0 ? afterSeq : replayed[^1].Seq;
        return new TranscriptionSessionSubscriptionSnapshot(sessionId,
            session.Status.ToString(),
            lastSeq,
            [.. replayed.Select(static segment => segment.ToResponse())],
            segments.Count > replayLimit);
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
            throw new HubException(TranscriptionHubErrors.NotTranscribing);
        }

        // A push from a connection that never subscribed is legitimate and attaches it.
        Track(sessionId);
        _live.NoteBrowserAttached(sessionId, Context.ConnectionId);

        try
        {
            await _live.PushAudioAsync(sessionId, (TranscriptChannel)channel, pcm16k, Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // A channel this session does not carry — a client sending You to a Microphone session. Defined here
            // rather than left as an unhandled argument error, so the client gets the same typed code it gets for a
            // channel no member exists for.
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
        await _live.EndAsync(sessionId, LiveEndReason.Completed, CancellationToken.None).ConfigureAwait(false);
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

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
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

/// <summary>The per-session SignalR group every push for one live session goes to.</summary>
internal static class TranscriptionHubGroups
{
    public static string Session(Guid sessionId) =>
        string.Concat("transcription-session-", sessionId.ToString("N"));
}
