namespace XE_Local_AI_Engine.Tests.Transcription;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Client.Services.Transcription.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Hand-written seams for the batch transcription path.
/// </summary>
/// <remarks>
///     Written by hand rather than substituted because each one has to record what it was handed — which stream, which
///     model, how many times — and because the cancellation case needs a gate the test releases instead of a sleep.
///     The shape follows <c>XE-Local-AI-Engine.Tests/Providers/LlamaServer/SupervisorTestDoubles.cs</c> and its
///     whisper sibling <c>Providers/WhisperCpp/WhisperSupervisorTestDoubles.cs</c>.
/// </remarks>
internal sealed class FakeWhisperTranscriber : IWhisperTranscriber
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What every call returns when <see cref="Failure" /> and <see cref="Gate" /> are unset.</summary>
    public WhisperTranscriptionResult Result { get; set; } =
        new("hello", [new WhisperTranscriptSegment(0.0, 1.25, "hello", 0.9)], "en", 0.99, 1.25);

    /// <summary>Thrown instead of returning, when set.</summary>
    public Exception? Failure { get; set; }

    /// <summary>When set, the call parks here until the test completes it. This is the cancellation gate.</summary>
    public TaskCompletionSource? Gate { get; set; }

    /// <summary>Completes as soon as a call has entered, so a test can cancel at a known point.</summary>
    public Task Entered => _entered.Task;

    public int CallCount { get; private set; }

    /// <summary>The media type of the last request, so a test can prove which file reached the runtime.</summary>
    public string? LastContentType { get; private set; }

    /// <summary>The first bytes of the last request's stream, so a test can prove it was the transcoded file.</summary>
    public byte[] LastAudioHead { get; private set; } = [];

    /// <summary>The stream position the last request arrived at; the provider requires a rewound stream.</summary>
    public long LastStartPosition { get; private set; } = -1;

    public string? LastModelId { get; private set; }

    public WhisperLanguageMode LastLanguageMode { get; private set; }

    public string? LastLanguageCode { get; private set; }

    public bool LastTranslate { get; private set; }

    public async Task<WhisperTranscriptionResult> TranscribeAsync(string modelId, WhisperTranscriptionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        CallCount++;
        LastModelId = modelId;
        LastContentType = request.ContentType;
        LastLanguageMode = request.LanguageMode;
        LastLanguageCode = request.LanguageCode;
        LastTranslate = request.Translate;
        LastStartPosition = request.Audio.Position;

        var head = new byte[16];
        var read = await request.Audio.ReadAsync(head.AsMemory(), ct).ConfigureAwait(false);
        LastAudioHead = head[..read];

        _entered.TrySetResult();

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        return Failure is not null ? throw Failure : Result;
    }
}

/// <summary>Records the ensure-running calls the batch path makes; never starts a process.</summary>
internal sealed class FakeWhisperServerSupervisor : IWhisperServerSupervisor
{
    private readonly List<string> _ensureRunningCalls = [];

    public IReadOnlyList<string> EnsureRunningCalls => _ensureRunningCalls;

    public Task<WhisperServerEndpoint> EnsureRunningAsync(string modelId, CancellationToken ct)
    {
        _ensureRunningCalls.Add(modelId);
        return Task.FromResult(new WhisperServerEndpoint(modelId, Generation: 1, new Uri("http://127.0.0.1:9/")));
    }

    public Task<WhisperServerEvictResult> EvictAsync(CancellationToken ct) =>
        Task.FromResult(new WhisperServerEvictResult(Evicted: true,
            new WhisperRuntimeActivitySnapshot(0, 0, 0, MutationReserved: false, EvictionReserved: false)));

    public IWhisperTranscriptionLease? TryAcquireTranscriptionLease(string modelId, long generation) =>
        null;

    public WhisperRuntimeStatusSnapshot GetStatus() =>
        new(WhisperRuntimeState.Ready, "tiny", Backend: null, BinaryVersion: null, BinarySource: null, SupportsTranscode: true);
}

/// <summary>
///     Stands in for ffmpeg. Availability is settable per test, and the conversion either writes a real minimal WAV or
///     throws after leaving a partial file behind — the case that proves the slot owns the destination path.
/// </summary>
internal sealed class FakeAudioTranscoder : IAudioTranscoder
{
    private readonly List<string> _calls = [];

    public bool IsAvailable { get; set; } = true;

    /// <summary>When set, the conversion writes these bytes and then throws.</summary>
    public bool ThrowAfterPartialWrite { get; set; }

    public IReadOnlyList<string> Calls => _calls;

    public async Task<string> ToWav16kMonoAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        _calls.Add(sourcePath);

        if (ThrowAfterPartialWrite)
        {
            await File.WriteAllBytesAsync(destinationPath, [0x52, 0x49, 0x46, 0x46], cancellationToken).ConfigureAwait(false);
            throw new AudioTranscodeException("The audio could not be converted.");
        }

        await File.WriteAllBytesAsync(destinationPath, TranscriptionAudioFixtures.TranscodedWav, cancellationToken).ConfigureAwait(false);
        return destinationPath;
    }
}

/// <summary>Resolves the node's effective model without touching settings, the catalogue or the hardware profile.</summary>
internal sealed class FakeTranscriptionRuntimeService(string effectiveModelId) : ITranscriptionRuntimeService
{
    public Task<TranscriptionRuntimeView> GetRuntimeAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<WhisperServerEvictResult> EjectAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<TranscriptionModelCatalogView> GetModelsAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<WhisperModelEntry> GetRecommendedModelAsync(CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<TranscriptionModelCatalogView> SelectModelAsync(string? modelId, CancellationToken ct) =>
        throw new NotSupportedException();

    public Task<string> ResolveEffectiveModelIdAsync(CancellationToken ct) =>
        Task.FromResult(effectiveModelId);
}

/// <summary>
///     The smallest byte sequences that are unambiguously each container, so a test can write a file the sniffer reads
///     exactly as a real upload would. Nothing here is real audio — the transcriber is faked, so only the header matters.
/// </summary>
internal static class TranscriptionAudioFixtures
{
    /// <summary>A RIFF/WAVE header. The first sixteen bytes are all the sniffer ever reads.</summary>
    public static byte[] Wav =>
    [
        0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00
    ];

    /// <summary>A distinct WAV header the transcode fake writes, so a test can tell the two files apart.</summary>
    public static byte[] TranscodedWav =>
    [
        0x52, 0x49, 0x46, 0x46, 0xFF, 0xFF, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00
    ];

    /// <summary>An Ogg page header.</summary>
    public static byte[] Ogg => [0x4F, 0x67, 0x67, 0x53, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>A FLAC stream marker.</summary>
    public static byte[] Flac => [0x66, 0x4C, 0x61, 0x43, 0x00, 0x00, 0x00, 0x22, 0x10, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00];

    /// <summary>An ID3-tagged MP3.</summary>
    public static byte[] Mp3 => [0x49, 0x44, 0x33, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFB, 0x90, 0x00, 0x00, 0x00];

    /// <summary>An ISO base-media header, which is what an m4a is.</summary>
    public static byte[] Mp4 => [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70, 0x4D, 0x34, 0x41, 0x20, 0x00, 0x00, 0x00, 0x00];

    /// <summary>An EBML header, which is what a webm is.</summary>
    public static byte[] Matroska => [0x1A, 0x45, 0xDF, 0xA3, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x23, 0x42, 0x86, 0x81, 0x01];
}

/// <summary>
///     A one-shot hold on a session read, released by the test.
/// </summary>
/// <remarks>
///     It holds AFTER the inner read has returned, which is the only placement that reproduces the interleaving it
///     exists for: the caller is left holding a snapshot of the session that another writer is free to invalidate.
///     Arming is one-shot, so the very next read is the one held and every later read — including the assertions' —
///     runs untouched.
/// </remarks>
internal sealed class TranscriptionStoreReadGate
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    /// <summary>Completes once a read has been caught by the hold.</summary>
    public Task Entered => _entered.Task;

    public void Arm() =>
        Interlocked.Exchange(ref _armed, 1);

    public void Release() =>
        _released.TrySetResult();

    internal async Task HoldIfArmedAsync()
    {
        if (Interlocked.Exchange(ref _armed, 0) == 0)
        {
            return;
        }

        _ = _entered.TrySetResult();
        await _released.Task.ConfigureAwait(false);
    }
}

/// <summary>The real store with <see cref="GetWithSegmentsAsync" /> routed through a test-controlled gate.</summary>
/// <remarks>
///     A decorator rather than a fake store: every other member, and the read itself, is the real thing on real SQLite,
///     because the session state these tests assert is what the store actually wrote. Only the MOMENT the read returns
///     to its caller is under the test's control.
/// </remarks>
internal sealed class GatedTranscriptionSessionStore(ITranscriptionSessionStore inner, TranscriptionStoreReadGate gate) : ITranscriptionSessionStore
{
    public Task CreateAsync(TranscriptionSessionCreate create, CancellationToken cancellationToken) =>
        inner.CreateAsync(create, cancellationToken);

    public async Task<TranscriptionSessionDetailView?> GetWithSegmentsAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var view = await inner.GetWithSegmentsAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await gate.HoldIfArmedAsync().ConfigureAwait(false);
        return view;
    }

    public Task<IReadOnlyList<TranscriptionSessionSummaryView>> ListAsync(int limit, int offset, CancellationToken cancellationToken) =>
        inner.ListAsync(limit, offset, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        inner.CountAsync(cancellationToken);

    public Task<bool> DeleteAsync(Guid sessionId, CancellationToken cancellationToken) =>
        inner.DeleteAsync(sessionId, cancellationToken);

    public Task<bool> SetStatusAsync(Guid sessionId, TranscriptionSessionStatus status, long updatedAtUtc, CancellationToken cancellationToken) =>
        inner.SetStatusAsync(sessionId, status, updatedAtUtc, cancellationToken);

    public Task<bool> CompleteAsync(Guid sessionId, string? detectedLanguage, long durationMs, long updatedAtUtc, CancellationToken cancellationToken) =>
        inner.CompleteAsync(sessionId, detectedLanguage, durationMs, updatedAtUtc, cancellationToken);

    public Task<bool> FailAsync(Guid sessionId, string errorCode, string errorMessage, long updatedAtUtc, CancellationToken cancellationToken) =>
        inner.FailAsync(sessionId, errorCode, errorMessage, updatedAtUtc, cancellationToken);

    public Task<bool> AppendSegmentsAsync(Guid sessionId, IReadOnlyList<TranscriptSegmentWrite> segments, long updatedAtUtc, CancellationToken cancellationToken) =>
        inner.AppendSegmentsAsync(sessionId, segments, updatedAtUtc, cancellationToken);
}

/// <summary>
///     The real <see cref="TranscriptionService" /> wired to the real
///     <see cref="XE_Local_AI_Engine.Client.Persistence.Implementation.TranscriptionSessionStore" /> over a real SQLite
///     file, with only the runtime seams faked.
/// </summary>
/// <remarks>
///     The store is not faked because it is not what is under test and because the session's persisted state — the
///     status transitions, the appended transcript, the recorded error — is most of what these tests assert. The node
///     data directory is a per-test temp root, so the temp-file assertions look at a directory nothing else writes to.
/// </remarks>
internal sealed class TranscriptionServiceHarness : IAsyncDisposable
{
    private readonly ServiceProvider _provider;

    private TranscriptionServiceHarness(ServiceProvider provider,
        string root,
        TranscriptionStoreReadGate readGate,
        FakeWhisperTranscriber transcriber,
        FakeWhisperServerSupervisor supervisor,
        FakeAudioTranscoder transcoder,
        ManualTimeProvider time)
    {
        _provider = provider;
        Root = root;
        ReadGate = readGate;
        Transcriber = transcriber;
        Supervisor = supervisor;
        Transcoder = transcoder;
        Time = time;
        Service = new TranscriptionService(provider.GetRequiredService<IServiceScopeFactory>(),
            new FakeTranscriptionRuntimeService(EffectiveModelId),
            supervisor,
            transcriber,
            transcoder,
            new FakeNodeDataDirectory(root),
            time,
            NullLogger<TranscriptionService>.Instance);
    }

    /// <summary>What the runtime facade resolves when a create request names no model.</summary>
    public const string EffectiveModelId = "ggml-tiny";

    public string Root { get; }

    /// <summary>Holds the next session read, so a test can place a caller's stale snapshot where it needs one.</summary>
    public TranscriptionStoreReadGate ReadGate { get; }

    public TranscriptionService Service { get; }

    public FakeWhisperTranscriber Transcriber { get; }

    public FakeWhisperServerSupervisor Supervisor { get; }

    public FakeAudioTranscoder Transcoder { get; }

    public ManualTimeProvider Time { get; }

    /// <summary>The engine-owned temporary directory; every assertion about leaked audio looks here.</summary>
    public string TempDirectory => Path.Combine(Root, "tmp", "transcription");

    /// <summary>The files currently sitting in the engine's temporary directory.</summary>
    public IReadOnlyList<string> TempFiles => Directory.Exists(TempDirectory) ? Directory.GetFiles(TempDirectory) : [];

    public static async Task<TranscriptionServiceHarness> CreateAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);

        var readGate = new TranscriptionStoreReadGate();
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(root, "transcription.sqlite")}"));

        // The decorator is always in place and does nothing until a test arms the gate, so every harness resolves the
        // same graph and no test can accidentally assert against a different one.
        services.AddScoped<TranscriptionSessionStore>();
        services.AddScoped<ITranscriptionSessionStore>(scopeProvider =>
            new GatedTranscriptionSessionStore(scopeProvider.GetRequiredService<TranscriptionSessionStore>(), readGate));

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        return new TranscriptionServiceHarness(provider,
            root,
            readGate,
            new FakeWhisperTranscriber(),
            new FakeWhisperServerSupervisor(),
            new FakeAudioTranscoder(),
            new ManualTimeProvider());
    }

    /// <summary>Creates a session and writes <paramref name="audio" /> into a fresh upload slot, ready to transcribe.</summary>
    public async Task<(Guid SessionId, TranscriptionUploadSlot Slot)> BeginAsync(byte[] audio,
        string extension = ".wav",
        TranscriptionSessionConfig? config = null)
    {
        var session = await Service.CreateSessionAsync(new CreateTranscriptionSessionInput
        {
            Config = config
        }, CancellationToken.None).ConfigureAwait(false);
        var slot = await Service.BeginUploadAsync(session.Id, extension, CancellationToken.None).ConfigureAwait(false);
        await File.WriteAllBytesAsync(slot.SourcePath, audio).ConfigureAwait(false);
        return (session.Id, slot);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync().ConfigureAwait(false);
        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A test that deliberately holds a file handle open leaves the temp root behind; the OS reclaims it.
        }
    }
}
