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
///     <para>
///         <b>No activity lease is taken here.</b> The transcriber acquires and releases one inside each
///         <see cref="IWhisperTranscriber.TranscribeAsync" /> call; a second, nested lease would either deadlock or
///         double-count. The consequence, accepted for this version, is that a job is lease-free while the upload is
///         sniffed and transcoded — an eject landing in that window kills the daemon and the following call fails,
///         which the session records as failed rather than silently truncating.
///     </para>
///     <para>
///         <b>Nothing here deletes a file.</b> Every temporary path belongs to the <see cref="TranscriptionUploadSlot" />
///         the endpoint disposes; a second owner could only produce a double delete or a leak.
///     </para>
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

    // Keyed by session id; a running transcription owns a live source, and CancelAsync signals it through here.
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITranscriptionRuntimeService _runtimeService;
    private readonly IWhisperServerSupervisor _supervisor;
    private readonly IWhisperTranscriber _transcriber;
    private readonly IAudioTranscoder _transcoder;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TranscriptionService> _logger;

    public TranscriptionService(IServiceScopeFactory scopeFactory,
        ITranscriptionRuntimeService runtimeService,
        IWhisperServerSupervisor supervisor,
        IWhisperTranscriber transcriber,
        IAudioTranscoder transcoder,
        INodeDataDirectory dataDirectory,
        TimeProvider timeProvider,
        ILogger<TranscriptionService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
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
            ? await _runtimeService.ResolveEffectiveModelIdAsync(cancellationToken).ConfigureAwait(false)
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
        }, cancellationToken).ConfigureAwait(false);

        return await store.GetWithSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("The transcription session vanished immediately after it was created.");
    }

    public async Task<TranscriptionSessionDetailView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .GetWithSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptionSessionPage> ListSessionsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaxPageSize);
        var boundedOffset = Math.Max(offset, 0);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>();
        var items = await store.ListAsync(boundedLimit, boundedOffset, cancellationToken).ConfigureAwait(false);
        var total = await store.CountAsync(cancellationToken).ConfigureAwait(false);
        return new TranscriptionSessionPage(items, total);
    }

    public async Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        // Cancel first, then delete without waiting for the run to unwind. Cancellation is a signal, not a join, so a
        // transcription that is mid-flight can still reach AppendSegmentsAsync or CompleteAsync after the row is gone.
        // That race is benign BY CONTRACT: every store write is keyed on the session id and returns false for a
        // session that no longer exists, and AppendSegmentsAsync checks existence itself because the node connection
        // leaves PRAGMA foreign_keys off. Tracking the run task to join on it would buy nothing this relies on.
        _ = await CancelAsync(sessionId, cancellationToken).ConfigureAwait(false);

        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .DeleteAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CancelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (!_inFlight.TryGetValue(sessionId, out var source))
        {
            return false;
        }

        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The transcription finished between the lookup and the signal; there is nothing left to cancel.
            return false;
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

    public async Task<TranscribeFileResult> TranscribeFileAsync(TranscriptionUploadSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);

        // The guard is taken FIRST, before the session is even read. Reading the status first and registering after
        // leaves a window in which two uploads both see Created: the loser can be descheduled between the read and the
        // registration, and by the time it registers the winner has completed the session, unregistered, and left.
        // The loser then restarts a completed session from a stale snapshot, re-allocates sequence 1, and the unique
        // (session_id, seq) index turns the finished transcript into a Failed one.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_inFlight.TryAdd(slot.SessionId, linked))
        {
            return TranscribeFileResult.RuntimeFailed("already-transcribing",
                "This session is already being transcribed.");
        }

        try
        {
            // Read and validated UNDER the guard, so the status this run acts on cannot change beneath it.
            var session = await GetSessionAsync(slot.SessionId, cancellationToken).ConfigureAwait(false);
            if (session is null || IsTerminal(session.Status))
            {
                return TranscribeFileResult.SessionNotFound();
            }

            // Sniffed before the runtime is touched: a container this node cannot handle is an answer, not a daemon error.
            var container = await DetectContainerAsync(slot.SourcePath, cancellationToken).ConfigureAwait(false);
            var refusal = RefuseIfUnsupported(container);
            if (refusal is not null)
            {
                return refusal;
            }

            return await RunAndTerminalizeAsync(slot, session, container, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _ = _inFlight.TryRemove(slot.SessionId, out _);
        }
    }

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
            _ = await SetStatusAsync(slot.SessionId, TranscriptionSessionStatus.Transcribing, CancellationToken.None).ConfigureAwait(false);
            return await RunAsync(slot, session, container, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = await SetStatusAsync(slot.SessionId, TranscriptionSessionStatus.Cancelled, CancellationToken.None).ConfigureAwait(false);
            return TranscribeFileResult.Cancelled();
        }
        catch (AudioTranscodeException exception)
        {
            return await FailAsync(slot.SessionId, "transcode-failed", exception.Message).ConfigureAwait(false);
        }
        catch (WhisperRuntimeException exception)
        {
            // The provider's message is already sanitized for display; it is the whole error text on purpose.
            return await FailAsync(slot.SessionId, "runtime-failed", exception.Message).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The catch-all is the point, not a fallback. The row is already Transcribing by the time anything below
            // can throw, so an ArgumentException from a non-seekable stream, an IOException opening the audio, a
            // JsonException, or any store failure would otherwise strand the session in Transcribing for good — a
            // state nothing ever leaves, because the only writer has already unwound. The same reason
            // ImageJobCoordinator terminalizes an image job on a bare Exception.
            _logger.LogError(exception, "Transcription of session {SessionId} failed unexpectedly.", slot.SessionId);
            return await FailAsync(slot.SessionId, "transcription-failed", "The transcription failed.").ConfigureAwait(false);
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
            audioPath = await _transcoder.ToWav16kMonoAsync(slot.SourcePath, destination, cancellationToken).ConfigureAwait(false);
            contentType = "audio/wav";
        }

        _ = await _supervisor.EnsureRunningAsync(session.ModelId, cancellationToken).ConfigureAwait(false);

        var config = DeserializeConfig(session.ConfigJson);
        var explicitLanguage = string.Equals(config.LanguageMode, "override", StringComparison.OrdinalIgnoreCase);

        var stream = new FileStream(audioPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 64 * 1024, useAsync: true);
        await using (stream.ConfigureAwait(false))
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
                }, cancellationToken).ConfigureAwait(false);

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
                    _ = await store.AppendSegmentsAsync(slot.SessionId, segments, completedAt, CancellationToken.None).ConfigureAwait(false);
                }

                _ = await store.CompleteAsync(slot.SessionId,
                    detectedLanguage,
                    ToMilliseconds(durationSeconds),
                    completedAt,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

        var completed = await GetSessionAsync(slot.SessionId, CancellationToken.None).ConfigureAwait(false);
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

    // The operator reads one vocabulary, not two: the supported list prints extensions, so the detected container is
    // named the same way. "Matroska" and "MP4" are the container names, but nobody uploads one of those — they upload
    // a .webm and a .m4a. The DetectedContainer field keeps the enum name for the wire.
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
        await using (stream.ConfigureAwait(false))
        {
            var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
            return AudioContainerSniffer.Detect(header.AsSpan(0, read));
        }
    }

    private async Task<TranscribeFileResult> FailAsync(Guid sessionId, string errorCode, string message)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                       .FailAsync(sessionId, errorCode, message, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), CancellationToken.None)
                       .ConfigureAwait(false);
        return TranscribeFileResult.RuntimeFailed(errorCode, message);
    }

    private async Task<bool> SetStatusAsync(Guid sessionId, TranscriptionSessionStatus status, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITranscriptionSessionStore>()
                          .SetStatusAsync(sessionId, status, _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(), cancellationToken)
                          .ConfigureAwait(false);
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

        // Matched against the NAMES, not through Enum.TryParse: that also parses the underlying numbers, so a caller
        // sending "99" would store an ordinal no member has and read it back on the wire as "99". Same rule, and the
        // same reason, as DevWorkflowTokenRules.IsNamed on the endpoint side.
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

    // A file extension and nothing else: a leading dot, then one to fifteen ASCII letters or digits. BeginUploadAsync
    // is a public entry point, and its argument is derived from a name the client chose, so a separator or a dot
    // segment reaching Path.Combine would let the upload file be placed outside the engine's temporary directory.
    // Anything that does not match is dropped entirely rather than repaired; an extension is a convenience for the
    // operator reading a directory, never something the transcription path depends on.
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
