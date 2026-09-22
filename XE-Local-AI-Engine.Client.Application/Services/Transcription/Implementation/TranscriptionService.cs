namespace XE_Local_AI_Engine.Client.Services.Transcription.Implementation;

using System.Collections.Concurrent;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Default <see cref="ITranscriptionService" />. Singleton: the in-flight cancellation registry outlives the
///     request that started a transcription, and every store call runs in its own dependency-injection scope.
/// </summary>
/// <remarks>
///     No activity lease is taken here: the transcriber acquires one inside each
///     <see cref="IWhisperTranscriber.TranscribeAsync" /> call, and a nested second would deadlock or double-count. A
///     job is therefore lease-free while the upload is sniffed and transcoded — an eject in that window kills the
///     daemon and the next call fails, which the session records as failed, never as truncated. Nothing here deletes
///     a file: every temporary path belongs to the <see cref="TranscriptionUploadSlot" /> the endpoint disposes.
/// </remarks>
public sealed class TranscriptionService : ITranscriptionService
{
    // One options instance for both directions, so a reader and a writer cannot disagree about casing. Default
    // JsonSerializerOptions against a web-cased document silently produces a zeroed record.
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    private const int MaxTitleChars = 200;
    private const int MaxPageSize = 200;
    private const int MinConfigWindowSeconds = 2;
    private const int MaxConfigWindowSeconds = 10;

    // A file is one channel. The array exists so the transcription loop below is the same shape the live capture
    // slices reuse when they feed it two.
    private static readonly TranscriptChannel[] SingleChannel = [TranscriptChannel.Mono];

    // The lane sets a live source kind implies. Shared instances: a session's channels are read, never mutated.
    private static readonly TranscriptChannel[] MonoLanes = [TranscriptChannel.Mono];
    private static readonly TranscriptChannel[] OthersLanes = [TranscriptChannel.Others];
    private static readonly TranscriptChannel[] TwoLanes = [TranscriptChannel.You, TranscriptChannel.Others];

    // Keyed by session id; a running transcription owns a live source, and CancelAsync signals it through here.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILiveTranscriptionSessionRegistry _live;
    private readonly ITranscriptionRuntimeService _runtimeService;
    private readonly IWhisperServerSupervisor _supervisor;
    private readonly IWhisperTranscriber _transcriber;
    private readonly IAudioTranscoder _transcoder;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TranscriptionService> _logger;

    public TranscriptionService(IServiceScopeFactory scopeFactory,
        ILiveTranscriptionSessionRegistry live,
        ITranscriptionRuntimeService runtimeService,
        IWhisperServerSupervisor supervisor,
        IWhisperTranscriber transcriber,
        IAudioTranscoder transcoder,
        INodeDataDirectory dataDirectory,
        TimeProvider timeProvider,
        ILogger<TranscriptionService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _live = live ?? throw new ArgumentNullException(nameof(live));
        _runtimeService = runtimeService ?? throw new ArgumentNullException(nameof(runtimeService));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _transcoder = transcoder ?? throw new ArgumentNullException(nameof(transcoder));
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The engine-owned directory every uploaded file lives in for the length of one transcription.</summary>
    private string TempDirectory => Path.Combine(_dataDirectory.Root, "tmp", "transcription");

    public async Task<TranscriptionSessionDetailView> CreateSessionAsync(CreateTranscriptionSessionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var modelId = string.IsNullOrWhiteSpace(input.ModelId)
            ? await _runtimeService.ResolveEffectiveModelIdAsync(cancellationToken)
            : input.ModelId.Trim();
        var createdAt = _timeProvider.GetUtcNow();
        var sessionId = Guid.NewGuid();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();
        await store.CreateAsync(new TranscriptionSessionCreate
        {
            Id = sessionId,
            Title = ResolveTitle(input.Title, createdAt),
            SourceKind = ParseSourceKind(input.SourceKind),
            ModelId = modelId,
            ConfigJson = JsonSerializer.Serialize(Normalize(input.Config), ConfigJsonOptions),
            CreatedAtUtc = createdAt.ToUnixTimeMilliseconds()
        }, cancellationToken);

        return await store.GetWithSegmentsAsync(sessionId, cancellationToken)
               ?? throw new InvalidOperationException("The transcription session vanished immediately after it was created.");
    }

    public async Task<TranscriptionSessionDetailView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .GetWithSegmentsAsync(sessionId, cancellationToken);
    }

    public async Task<TranscriptionSessionSummaryView?> GetSessionSummaryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .GetSummaryAsync(sessionId, cancellationToken);
    }

    public async Task<TranscriptionSessionPage> ListSessionsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaxPageSize);
        var boundedOffset = Math.Max(offset, 0);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();
        var items = await store.ListAsync(boundedLimit, boundedOffset, cancellationToken);
        var total = await store.CountAsync(cancellationToken);
        return new TranscriptionSessionPage { Items = items, TotalCount = total };
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Cancels first, then deletes without waiting for the run to unwind: cancellation is a signal, not a join, so
    ///     a mid-flight transcription can still reach <c>AppendSegmentsAsync</c> or <c>CompleteAsync</c> after the row
    ///     is gone. That race is benign BY CONTRACT — every store write is keyed on the session id and returns false
    ///     for a session that no longer exists, and <c>AppendSegmentsAsync</c> checks existence itself so a late
    ///     append answers false rather than throwing. Tracking the run task to join on it would buy nothing this relies on.
    /// </remarks>
    public async Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        _ = await CancelAsync(sessionId, cancellationToken);

        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .DeleteAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     A live session ends through the registry's single termination path, never by signalling a batch source it
    ///     does not own, and delete reaches this method first so the lanes stop before the row is removed.
    ///     <c>EndAsync</c> is called unconditionally, not only when the session reads as live: <c>IsLive</c> flips
    ///     false as the FIRST step of ending, so a delete arriving mid-teardown would otherwise remove the row while
    ///     lanes still ran. It no-ops for an unknown session and returns the in-flight end's own task, joining it.
    /// </remarks>
    public async Task<bool> CancelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var wasLive = _live.IsRegistered(sessionId);
        await _live.EndAsync(sessionId, LiveEndReason.Cancelled, cancellationToken);

        if (!_inFlight.TryGetValue(sessionId, out var source))
        {
            return wasLive;
        }

        try
        {
            await source.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            // The transcription finished between the lookup and the signal; there is nothing left to cancel.
            return wasLive;
        }

        return true;
    }

    public Task<TranscriptionUploadSlot> BeginUploadAsync(Guid sessionId, string extension, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var directory = TempDirectory;
        _ = Directory.CreateDirectory(directory);
        return Task.FromResult(new TranscriptionUploadSlot(sessionId, directory, NormalizeExtension(extension), _logger));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The in-flight guard is taken FIRST, before the session is even read. Reading the status first and
    ///     registering after leaves a window in which two uploads both see <c>Created</c>: the loser can be
    ///     descheduled between the read and the registration, and by the time it registers the winner has completed
    ///     the session, unregistered and left. The loser then restarts a completed session from a stale snapshot,
    ///     re-allocates sequence 1, and the unique <c>(session_id, seq)</c> index turns the transcript into a failure.
    /// </remarks>
    public async Task<TranscribeFileResult> TranscribeFileAsync(TranscriptionUploadSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_inFlight.TryAdd(slot.SessionId, linked))
        {
            return TranscribeFileResult.RuntimeFailed("already-transcribing",
                "This session is already being transcribed.");
        }

        try
        {
            // Read and validated UNDER the guard, so the status this run acts on cannot change beneath it.
            var session = await GetSessionAsync(slot.SessionId, cancellationToken);
            if (session is null || IsTerminal(session.Status))
            {
                return TranscribeFileResult.SessionNotFound();
            }

            // Sniffed before the runtime is touched: a container this node cannot handle is an answer, not a daemon error.
            var container = await DetectContainerAsync(slot.SourcePath, cancellationToken);
            var refusal = RefuseIfUnsupported(container);
            if (refusal is not null)
            {
                return refusal;
            }

            return await RunAndTerminalizeAsync(slot, session, container, linked.Token);
        }
        finally
        {
            _ = _inFlight.TryRemove(slot.SessionId, out _);
        }
    }

    public async Task<StartLiveResult> StartLiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // The summary, not the transcript: this method needs a status, a model, a config and a source kind, and
        // decrypting every segment of a long session to reach them is work nobody asked for.
        var session = await GetSessionSummaryAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return new StartLiveResult
            {
                Outcome = StartLiveOutcome.SessionNotFound
            };
        }

        // Before the status checks: a file session has no live path at any status, so answering "already finished"
        // would send the caller off to retry something that can never work.
        var channels = LiveChannelsFor(session.SourceKind);

        // The MAX sequence, never the segment count: a transcript with a gap in it would otherwise re-allocate a
        // sequence the unique (session_id, seq) index already holds.
        var lastSeq = await GetLastSeqAsync(sessionId, cancellationToken);

        if (IsTerminal(session.Status))
        {
            return new StartLiveResult
            {
                Outcome = StartLiveOutcome.SessionAlreadyFinished,
                Status = session.Status,
                LastSeq = lastSeq
            };
        }

        if (session.Status == TranscriptionSessionStatus.Transcribing && _live.IsLive(sessionId))
        {
            // A double-click, a retried fetch or a reconnect that re-issues the start must not produce two sets of lanes.
            return new StartLiveResult
            {
                Outcome = StartLiveOutcome.AlreadyLive,
                Status = session.Status,
                LastSeq = lastSeq
            };
        }

        var config = DeserializeConfig(session.ConfigJson);
        var options = new LiveSessionOptions
        {
            ModelId = session.ModelId,
            Language = string.Equals(config.LanguageMode, "override", StringComparison.OrdinalIgnoreCase)
                ? config.LanguageOverride
                : null,
            Translate = config.Translate,
            Settings = LiveSegmenterSettings.FromSessionConfig(config.MaxWindowSeconds),
            Channels = channels,
            StartingSeq = lastSeq,
            SourceKind = session.SourceKind,
            Persist = true
        };

        // Compare-and-set, not a blind write from the status read above. A start racing a graceful end would
        // otherwise overwrite the terminal status that end had just written and resurrect a finished session.
        var previousStatus = session.Status;
        if (!await TryTransitionAsync(sessionId, previousStatus, TranscriptionSessionStatus.Transcribing, cancellationToken))
        {
            return await DescribeMovedRowAsync(sessionId, cancellationToken);
        }

        try
        {
            await _live.StartLiveSessionAsync(sessionId, options, cancellationToken);
        }
        catch (LiveSessionAlreadyRegisteredException)
        {
            // Two starts read Created and both moved the row; this one lost the registration race. Rolling back would reset the WINNER's row, so the loser touches nothing and
            // answers as a sequential second call would. Deliberately NOT conditioned on IsLive: the winner may already be ending, and a rollback would put a finished session back into Created.
            return new StartLiveResult
            {
                Outcome = StartLiveOutcome.AlreadyLive,
                Status = TranscriptionSessionStatus.Transcribing,
                LastSeq = lastSeq
            };
        }
        catch
        {
            // A row reading Transcribing with no registry entry accepts no audio and never ends — worse than a start the caller can see failed. Compare-and-set
            // again: a false result means the row moved on while this start was failing, and whoever moved it owns it now.
            if (!await TryTransitionAsync(sessionId, TranscriptionSessionStatus.Transcribing, previousStatus, CancellationToken.None))
            {
                _logger.LogDebug("Rolling transcription session {SessionId} back to {Status} found the row already moved on.", sessionId, previousStatus);
            }

            throw;
        }

        return new StartLiveResult
        {
            Outcome = StartLiveOutcome.Started,
            Status = TranscriptionSessionStatus.Transcribing,
            LastSeq = lastSeq,
            Options = options
        };
    }

    public async Task AppendLiveSegmentAsync(Guid sessionId,
        long seq,
        TranscriptChannel channel,
        long startMs,
        long endMs,
        string text,
        double? confidence,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                       .AppendSegmentsAsync(sessionId,
                           [
                               new TranscriptSegmentWrite
                               {
                                   Seq = seq,
                                   StartMs = startMs,
                                   EndMs = endMs,
                                   Text = text,
                                   Channel = channel,
                                   Confidence = confidence
                               }
                           ],
                           _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                           cancellationToken);
    }

    public async Task CompleteLiveAsync(Guid sessionId,
        TranscriptionSessionStatus finalStatus,
        long durationMs,
        string? detectedLanguage,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        var updatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();

        switch (finalStatus)
        {
            case TranscriptionSessionStatus.Completed:
                _ = await store.CompleteAsync(sessionId, detectedLanguage, durationMs, updatedAtUtc, cancellationToken);
                break;

            // The error pair is what makes a failure readable; a failed end with neither still records the status
            // rather than throwing on the store's own argument guards.
            case TranscriptionSessionStatus.Failed when !string.IsNullOrWhiteSpace(errorCode) && !string.IsNullOrWhiteSpace(errorMessage):
                _ = await store.FailAsync(sessionId, errorCode, errorMessage, updatedAtUtc, cancellationToken);
                break;

            default:
                _ = await store.SetStatusAsync(sessionId, finalStatus, updatedAtUtc, cancellationToken);
                break;
        }
    }

    public async Task<IReadOnlyList<TranscriptSegmentView>> ListSegmentsAfterAsync(Guid sessionId,
        long afterSeq,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .ListSegmentsAfterAsync(sessionId, afterSeq, limit, cancellationToken);
    }

    /// <summary>
    ///     What to tell a caller whose status transition lost: the row moved under it between the read and the write.
    /// </summary>
    private async Task<StartLiveResult> DescribeMovedRowAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var current = await GetSessionSummaryAsync(sessionId, cancellationToken);
        if (current is null)
        {
            return new StartLiveResult
            {
                Outcome = StartLiveOutcome.SessionNotFound
            };
        }

        return new StartLiveResult
        {
            // A session that is live belongs to whoever registered it; anything else has moved to a state this
            // start cannot begin from, and the caller is told so rather than being handed a resurrected row.
            Outcome = _live.IsLive(sessionId) ? StartLiveOutcome.AlreadyLive : StartLiveOutcome.SessionAlreadyFinished,
            Status = current.Status,
            LastSeq = await GetLastSeqAsync(sessionId, cancellationToken)
        };
    }

    private async Task<bool> TryTransitionAsync(Guid sessionId,
        TranscriptionSessionStatus expected,
        TranscriptionSessionStatus desired,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .TryTransitionStatusAsync(sessionId, expected, desired, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
    }

    private async Task<long> GetLastSeqAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .GetLastSeqAsync(sessionId, cancellationToken);
    }

    // Which lanes a source kind implies. A file has none, and that is a malformed request rather than a refused
    // state, so it leaves as an exception where every other answer here is data.
    private static IReadOnlyList<TranscriptChannel> LiveChannelsFor(TranscriptionSourceKind sourceKind) =>
        sourceKind switch
        {
            TranscriptionSourceKind.Microphone or TranscriptionSourceKind.Dictation => MonoLanes,
            TranscriptionSourceKind.SystemAudio or TranscriptionSourceKind.ApplicationProcess => OthersLanes,
            TranscriptionSourceKind.MicrophoneAndSystem => TwoLanes,
            _ => throw new LiveTranscriptionSourceKindException(sourceKind)
        };

    /// <summary>
    ///     Moves the session to <see cref="TranscriptionSessionStatus.Transcribing" /> and runs it, mapping every way
    ///     the run can end onto a terminal row.
    /// </summary>
    /// <remarks>
    ///     Separate from the admission steps in <see cref="TranscribeFileAsync" /> on purpose: the catch arms below all
    ///     WRITE the session, and they are only correct once the row is this call's to own. A failure while the session
    ///     is still <see cref="TranscriptionSessionStatus.Created" /> — the store read, the container sniff — is a
    ///     refused upload the operator can retry with another file, never a failed transcription.
    /// </remarks>
    private async Task<TranscribeFileResult> RunAndTerminalizeAsync(TranscriptionUploadSlot slot,
        TranscriptionSessionDetailView session,
        AudioContainer container,
        CancellationToken cancellationToken)
    {
        try
        {
            // The status moves and the cancellation source is registered BEFORE the transcode, so a cancel arriving
            // during conversion reaches the converter's child process instead of being refused as "not running".
            _ = await SetStatusAsync(slot.SessionId, TranscriptionSessionStatus.Transcribing, CancellationToken.None);
            return await RunAsync(slot, session, container, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _ = await SetStatusAsync(slot.SessionId, TranscriptionSessionStatus.Cancelled, CancellationToken.None);
            return TranscribeFileResult.Cancelled();
        }
        catch (AudioTranscodeException exception)
        {
            return await FailAsync(slot.SessionId, "transcode-failed", exception.Message);
        }
        catch (WhisperRuntimeException exception)
        {
            // The provider's message is already sanitized for display; it is the whole error text on purpose.
            return await FailAsync(slot.SessionId, "runtime-failed", exception.Message);
        }
        catch (Exception exception)
        {
            // The catch-all is the point, not a fallback: the row is already Transcribing before anything below can throw, so an ArgumentException from a non-seekable
            // stream, an IOException, a JsonException or a store failure would strand it there for good, the only writer having unwound. ImageJobCoordinator does the same.
            _logger.LogError(exception, "Transcription of session {SessionId} failed unexpectedly.", slot.SessionId);
            return await FailAsync(slot.SessionId, "transcription-failed", "The transcription failed.");
        }
    }

    private async Task<TranscribeFileResult> RunAsync(TranscriptionUploadSlot slot,
        TranscriptionSessionDetailView session,
        AudioContainer container,
        CancellationToken cancellationToken)
    {
        var audioPath = slot.SourcePath;
        var contentType = AudioContainerSniffer.MediaTypeFor(container);

        if (AudioContainerSniffer.RequiresFfmpeg(container))
        {
            // Named through the slot BEFORE the converter runs: a converter killed halfway still leaves a partial
            // file, and that file is audio.
            var destination = slot.AddOwnedPath(".wav");
            audioPath = await _transcoder.ToWav16kMonoAsync(slot.SourcePath, destination, cancellationToken);
            contentType = "audio/wav";
        }

        _ = await _supervisor.EnsureRunningAsync(session.ModelId, cancellationToken);

        var config = DeserializeConfig(session.ConfigJson);
        var explicitLanguage = string.Equals(config.LanguageMode, "override", StringComparison.OrdinalIgnoreCase);

        var stream = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        await using (stream)
        {
            var pending = new List<(TranscriptChannel Channel, WhisperTranscriptSegment Segment)>();
            string? detectedLanguage = null;
            double durationSeconds = 0;

            foreach (var channel in SingleChannel)
            {
                // Rewound per call: the provider requires a seekable stream and reads it from wherever it is left.
                _ = stream.Seek(0, SeekOrigin.Begin);
                var transcribed = await _transcriber.TranscribeAsync(session.ModelId, new WhisperTranscriptionRequest
                {
                    Audio = stream,
                    ContentType = contentType,
                    LanguageMode = explicitLanguage ? WhisperLanguageMode.Explicit : WhisperLanguageMode.Auto,
                    LanguageCode = explicitLanguage ? config.LanguageOverride : null,
                    Translate = config.Translate
                }, cancellationToken);

                detectedLanguage ??= transcribed.DetectedLanguageCode;
                durationSeconds = Math.Max(durationSeconds, transcribed.DurationSeconds);
                pending.AddRange(transcribed.Segments.Select(segment => (channel, segment)));
            }

            var segments = pending
                           .OrderBy(entry => entry.Segment.StartSeconds)
                           .ThenBy(entry => (int)entry.Channel)
                           .Select((entry, index) => new TranscriptSegmentWrite
                           {
                               // Sequence numbers start at 1, never 0, so a subscriber cannot confuse "no segments
                               // yet" with "segment zero".
                               Seq = index + 1,
                               StartMs = ToMilliseconds(entry.Segment.StartSeconds),
                               EndMs = ToMilliseconds(entry.Segment.EndSeconds),
                               Text = entry.Segment.Text,
                               Channel = entry.Channel,
                               Confidence = entry.Segment.Confidence
                           })
                           .ToList();

            await using (var scope = _scopeFactory.CreateAsyncScope())
            {
                var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();
                var completedAt = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
                if (segments.Count > 0)
                {
                    _ = await store.AppendSegmentsAsync(slot.SessionId, segments, completedAt, CancellationToken.None);
                }

                _ = await store.CompleteAsync(slot.SessionId,
                    detectedLanguage,
                    ToMilliseconds(durationSeconds),
                    completedAt,
                    CancellationToken.None);
            }
        }

        var completed = await GetSessionAsync(slot.SessionId, CancellationToken.None);
        return completed is null
            ? TranscribeFileResult.SessionNotFound()
            : TranscribeFileResult.Succeeded(completed);
    }

    private TranscribeFileResult? RefuseIfUnsupported(AudioContainer container)
    {
        if (AudioContainerSniffer.IsNativelySupported(container))
        {
            return null;
        }

        var needsFfmpeg = AudioContainerSniffer.RequiresFfmpeg(container);
        if (needsFfmpeg && _transcoder.IsAvailable)
        {
            return null;
        }

        var supported = _transcoder.IsAvailable
            ? AudioContainerSniffer.NativeExtensions.Concat(AudioContainerSniffer.TranscodeExtensions).ToArray()
            : AudioContainerSniffer.NativeExtensions.ToArray();
        var detected = DescribeContainer(container);
        var message = needsFfmpeg
            ? $"{detected} audio needs ffmpeg, which is not installed on this node. Supported here: {string.Join(", ", supported)}."
            : $"This node cannot transcribe {detected}. Supported here: {string.Join(", ", supported)}.";

        return TranscribeFileResult.UnsupportedContainer(container, supported, needsFfmpeg, message);
    }

    // The operator reads one vocabulary, not two: the supported list prints extensions, so the detected container is named the same way — nobody uploads a
    // "Matroska" or an "MP4", they upload a .webm and a .m4a. The DetectedContainer field keeps the enum name for the wire.
    private static string DescribeContainer(AudioContainer container) =>
        container switch
        {
            AudioContainer.Wav => "wav",
            AudioContainer.Mp3 => "mp3",
            AudioContainer.Flac => "flac",
            AudioContainer.Ogg => "ogg",
            AudioContainer.Mp4 => "m4a",
            AudioContainer.Matroska => "webm",
            _ => "that file"
        };

    private static async Task<AudioContainer> DetectContainerAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[AudioContainerSniffer.HeaderBytes];
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: AudioContainerSniffer.HeaderBytes, useAsync: true);
        await using (stream)
        {
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
            return AudioContainerSniffer.Detect(header.AsSpan(0, read));
        }
    }

    private async Task<TranscribeFileResult> FailAsync(Guid sessionId, string errorCode, string message)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                       .FailAsync(sessionId, errorCode, message, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None);
        return TranscribeFileResult.RuntimeFailed(errorCode, message);
    }

    private async Task<bool> SetStatusAsync(Guid sessionId, TranscriptionSessionStatus status, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .SetStatusAsync(sessionId, status, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken);
    }

    private static bool IsTerminal(TranscriptionSessionStatus status) =>
        status is TranscriptionSessionStatus.Completed or TranscriptionSessionStatus.Failed or TranscriptionSessionStatus.Cancelled;

    // The provider speaks fractional seconds; the entity speaks whole milliseconds.
    private static long ToMilliseconds(double seconds) =>
        (long)Math.Round(seconds * 1000);

    private static TranscriptionSessionConfig DeserializeConfig(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return Normalize(config: null);
        }

        try
        {
            return Normalize(JsonSerializer.Deserialize<TranscriptionSessionConfig>(configJson, ConfigJsonOptions));
        }
        catch (JsonException)
        {
            // A session whose stored options cannot be read still transcribes, with the defaults.
            return Normalize(config: null);
        }
    }

    private static TranscriptionSessionConfig Normalize(TranscriptionSessionConfig? config)
    {
        if (config is null)
        {
            return new TranscriptionSessionConfig
            {
                LanguageMode = "auto"
            };
        }

        var isOverride = string.Equals(config.LanguageMode, "override", StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(config.LanguageOverride);
        return config with
        {
            LanguageMode = isOverride ? "override" : "auto",
            LanguageOverride = isOverride ? config.LanguageOverride!.Trim() : null,
            MaxWindowSeconds = Math.Clamp(config.MaxWindowSeconds, MinConfigWindowSeconds, MaxConfigWindowSeconds)
        };
    }

    private static TranscriptionSourceKind ParseSourceKind(string? sourceKind)
    {
        if (string.IsNullOrWhiteSpace(sourceKind))
        {
            return TranscriptionSourceKind.File;
        }

        // Matched against the NAMES, not through Enum.TryParse: that also parses the underlying numbers, so a caller sending "99" would store an ordinal no
        // member has and read it back on the wire as "99". Same rule, and the same reason, as DevWorkflowTokenRules.IsNamed on the endpoint side.
        var trimmed = sourceKind.Trim();
        return Enum.GetNames<TranscriptionSourceKind>().Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? Enum.Parse<TranscriptionSourceKind>(trimmed, ignoreCase: true)
            : throw new ArgumentException($"'{sourceKind}' is not a known transcription source kind.", nameof(sourceKind));
    }

    // The client's file name reaches persistence only as a title, never as a path: the upload file is server-named.
    private static string ResolveTitle(string? title, DateTimeOffset createdAt)
    {
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return $"Transcription {createdAt.UtcDateTime:yyyy-MM-dd HH:mm}";
        }

        return trimmed.Length > MaxTitleChars ? trimmed[..MaxTitleChars] : trimmed;
    }

    // A leading dot then one to fifteen ASCII letters or digits, and nothing else: BeginUploadAsync is a public entry point whose argument derives from a
    // client-chosen name, so a separator or dot reaching Path.Combine would escape the engine's temp directory. Non-matches are dropped, not repaired: an extension is only a convenience.
    private static string NormalizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return string.Empty;
        }

        var trimmed = extension.Trim();
        var candidate = trimmed.StartsWith('.') ? trimmed : string.Concat(".", trimmed);
        if (candidate.Length is < 2 or > 16)
        {
            return string.Empty;
        }

        foreach (var character in candidate.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(character))
            {
                return string.Empty;
            }
        }

        return candidate;
    }
}
