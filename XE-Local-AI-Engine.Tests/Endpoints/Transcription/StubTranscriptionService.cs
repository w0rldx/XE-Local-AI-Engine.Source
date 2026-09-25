namespace XE_Local_AI_Engine.Tests.Endpoints.Transcription;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Transcription;

/// <summary>
///     An in-memory <see cref="ITranscriptionService" /> for the endpoint tests: it records what the handlers asked
///     for and answers with whatever the test set up.
/// </summary>
/// <remarks>
///     Hand-written rather than a <c>Substitute.For</c> because it is stateful — a created session has to read back
///     through <see cref="GetSessionAsync" />, and the streaming tests need <see cref="TranscribeFileAsync" /> to park
///     on a gate the test controls. It is the same posture the S1 endpoint tests take with their runtime-service stub.
///     <see cref="BeginUploadAsync" /> hands out a REAL <see cref="TranscriptionUploadSlot" />, because the slot's
///     disposal is exactly what the streaming tests are there to prove.
/// </remarks>
internal sealed class StubTranscriptionService : ITranscriptionService, IDisposable
{
    private static readonly JsonSerializerOptions ConfigJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<Guid, TranscriptionSessionDetailView> _sessions = [];
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The engine-owned upload directory this stub hands its slots out of, isolated per instance.</summary>
    public string UploadDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"xe-local-ai-engine-tests-transcription-{Guid.NewGuid():N}");

    /// <summary>Held to keep a transcription in flight while the test inspects the disk; null returns immediately.</summary>
    public TaskCompletionSource? Gate { get; init; }

    /// <summary>Completes once <see cref="TranscribeFileAsync" /> has been reached — the gate the tests wait on.</summary>
    public Task Entered => _entered.Task;

    /// <summary>Thrown from <see cref="TranscribeFileAsync" /> after the gate, to drive the handler-throws case.</summary>
    public Exception? TranscribeThrows { get; init; }

    /// <summary>What <see cref="TranscribeFileAsync" /> answers when it does not throw.</summary>
    public TranscribeFileResult? TranscribeResult { get; init; }

    public bool GetReturnsNull { get; init; }

    public bool DeleteResult { get; init; }

    public bool CancelResult { get; init; }

    public int TotalCount { get; init; } = 1;

    public int CreateCallCount { get; private set; }

    public int CancelCallCount { get; private set; }

    public int LastLimit { get; private set; } = -1;

    public int LastOffset { get; private set; } = -1;

    /// <summary>How many times the endpoint reached for an upload slot — zero proves the body was never read.</summary>
    public int BeginUploadCallCount { get; private set; }

    /// <summary>What <see cref="StartLiveAsync" /> answers; null derives it from whether the session was seeded.</summary>
    public StartLiveResult? StartLiveResult { get; init; }

    /// <summary>Thrown from <see cref="StartLiveAsync" />, to drive the unsupported-source-kind case.</summary>
    public Exception? StartLiveThrows { get; init; }

    public int StartLiveCallCount { get; private set; }

    public Guid LastStartLiveSessionId { get; private set; }

    /// <summary>Inserts a session directly, for the tests that need one to exist without going through the API.</summary>
    public Guid SeedSession()
    {
        var session = new TranscriptionSessionDetailView
        {
            Id = Guid.NewGuid(),
            Title = "seeded",
            CreatedAtUtc = 1_700_000_000_000,
            UpdatedAtUtc = 1_700_000_000_000,
            Status = TranscriptionSessionStatus.Created,
            SourceKind = TranscriptionSourceKind.File,
            ModelId = "ggml-tiny",
            ConfigJson = "{\"languageMode\":\"auto\"}",
            SegmentCount = 0,
            Segments = []
        };

        _sessions[session.Id] = session;
        return session.Id;
    }

    public Task<TranscriptionSessionDetailView> CreateSessionAsync(CreateTranscriptionSessionInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        CreateCallCount++;

        var session = new TranscriptionSessionDetailView
        {
            Id = Guid.NewGuid(),
            Title = input.Title,
            CreatedAtUtc = 1_700_000_000_000,
            UpdatedAtUtc = 1_700_000_000_000,
            Status = TranscriptionSessionStatus.Created,
            SourceKind = Enum.Parse<TranscriptionSourceKind>(string.IsNullOrWhiteSpace(input.SourceKind) ? "File" : input.SourceKind, ignoreCase: true),
            ModelId = input.ModelId ?? "ggml-tiny",
            ConfigJson = JsonSerializer.Serialize(input.Config ?? new TranscriptionSessionConfig
            {
                LanguageMode = "auto"
            }, ConfigJsonOptions),
            SegmentCount = 0,
            Segments = []
        };

        _sessions[session.Id] = session;
        return Task.FromResult(session);
    }

    public Task<TranscriptionSessionDetailView?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetReturnsNull)
        {
            return Task.FromResult<TranscriptionSessionDetailView?>(result: null);
        }

        return Task.FromResult(_sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    public Task<TranscriptionSessionSummaryView?> GetSessionSummaryAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        if (GetReturnsNull || !_sessions.TryGetValue(sessionId, out var session))
        {
            return Task.FromResult<TranscriptionSessionSummaryView?>(result: null);
        }

        return Task.FromResult<TranscriptionSessionSummaryView?>(new TranscriptionSessionSummaryView
        {
            Id = session.Id,
            Title = session.Title,
            CreatedAtUtc = session.CreatedAtUtc,
            UpdatedAtUtc = session.UpdatedAtUtc,
            Status = session.Status,
            SourceKind = session.SourceKind,
            ModelId = session.ModelId,
            ConfigJson = session.ConfigJson,
            DetectedLanguage = session.DetectedLanguage,
            DurationMs = session.DurationMs,
            SegmentCount = session.SegmentCount
        });
    }

    public Task<TranscriptionSessionPage> ListSessionsAsync(int limit, int offset, CancellationToken cancellationToken)
    {
        LastLimit = limit;
        LastOffset = offset;

        return Task.FromResult(new TranscriptionSessionPage
        {
            Items =
            [
                new TranscriptionSessionSummaryView
                {
                    Id = Guid.NewGuid(),
                    Title = "one",
                    CreatedAtUtc = 1_700_000_000_000,
                    UpdatedAtUtc = 1_700_000_000_000,
                    Status = TranscriptionSessionStatus.Completed,
                    SourceKind = TranscriptionSourceKind.File,
                    ModelId = "ggml-tiny",
                    ConfigJson = "{\"languageMode\":\"auto\"}",
                    SegmentCount = 3
                }
            ],
            TotalCount = TotalCount
        });
    }

    public Task<bool> DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(DeleteResult);

    public Task<bool> CancelAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        CancelCallCount++;
        return Task.FromResult(CancelResult);
    }

    public Task<TranscriptionUploadSlot> BeginUploadAsync(Guid sessionId, string extension, CancellationToken cancellationToken)
    {
        BeginUploadCallCount++;
        _ = Directory.CreateDirectory(UploadDirectory);
        return Task.FromResult(new TranscriptionUploadSlot(sessionId, UploadDirectory, extension, NullLogger.Instance));
    }

    public async Task<TranscribeFileResult> TranscribeFileAsync(TranscriptionUploadSlot slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slot);
        _ = _entered.TrySetResult();

        if (Gate is not null)
        {
            // WaitAsync, not a bare await: a client that disconnects must unwind the handler, which is what the
            // cancellation case is there to prove.
            await Gate.Task.WaitAsync(cancellationToken);
        }

        if (TranscribeThrows is not null)
        {
            throw TranscribeThrows;
        }

        if (TranscribeResult is not null)
        {
            return TranscribeResult;
        }

        var session = await GetSessionAsync(slot.SessionId, cancellationToken)
                      ?? new TranscriptionSessionDetailView
                      {
                          Id = slot.SessionId,
                          Title = "stub",
                          CreatedAtUtc = 1_700_000_000_000,
                          UpdatedAtUtc = 1_700_000_000_000,
                          Status = TranscriptionSessionStatus.Completed,
                          SourceKind = TranscriptionSourceKind.File,
                          ModelId = "ggml-tiny",
                          ConfigJson = "{\"languageMode\":\"auto\"}",
                          SegmentCount = 0,
                          Segments = []
                      };

        return TranscribeFileResult.Succeeded(session);
    }

    /// <summary>Records the start call and answers whatever the test set up; it registers nothing.</summary>
    public Task<StartLiveResult> StartLiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        StartLiveCallCount++;
        LastStartLiveSessionId = sessionId;

        if (StartLiveThrows is not null)
        {
            throw StartLiveThrows;
        }

        return Task.FromResult(StartLiveResult ?? new StartLiveResult
        {
            Outcome = _sessions.ContainsKey(sessionId) ? StartLiveOutcome.Started : StartLiveOutcome.SessionNotFound,
            Status = TranscriptionSessionStatus.Transcribing
        });
    }

    public Task AppendLiveSegmentAsync(Guid sessionId,
        long seq,
        TranscriptChannel channel,
        long startMs,
        long endMs,
        string text,
        double? confidence,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task CompleteLiveAsync(Guid sessionId,
        TranscriptionSessionStatus finalStatus,
        long durationMs,
        string? detectedLanguage,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<TranscriptSegmentView>> ListSegmentsAfterAsync(Guid sessionId,
        long afterSeq,
        int limit,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TranscriptSegmentView>>([]);

    /// <summary>What <see cref="UpdateSegmentTextAsync" /> answers; null means the session is unknown.</summary>
    public UpdateTranscriptSegmentResult? UpdateSegmentResult { get; init; }

    public int UpdateSegmentCallCount { get; private set; }

    /// <summary>The text the endpoint handed over — trimmed, if the endpoint did its job.</summary>
    public string? LastUpdatedText { get; private set; }

    public long LastUpdatedSeq { get; private set; }

    public Task<UpdateTranscriptSegmentResult> UpdateSegmentTextAsync(Guid sessionId, long seq, string text, CancellationToken cancellationToken)
    {
        UpdateSegmentCallCount++;
        LastUpdatedSeq = seq;
        LastUpdatedText = text;
        return Task.FromResult(UpdateSegmentResult ?? new UpdateTranscriptSegmentResult
        {
            Outcome = UpdateTranscriptSegmentOutcome.SessionNotFound
        });
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(UploadDirectory))
            {
                Directory.Delete(UploadDirectory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A scratch directory that survives a test run is litter, never a failure.
        }
    }
}
