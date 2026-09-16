namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

using System.Buffers;
using System.Diagnostics;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     WASAPI process-loopback capture: the audio of one application and its child processes, converted in-process
///     and pushed straight into the live session's <c>Others</c> lane.
/// </summary>
/// <remarks>
///     <para>
///         Type-level <see cref="SupportedOSPlatformAttribute" /> rather than a CA1416 suppression, matching
///         <c>WindowsImageJobObjectProcessHandle</c>. <c>AddNodeTranscription</c> registers this only behind
///         <c>OperatingSystem.IsWindows()</c>, which is what makes the attribute honest.
///     </para>
///     <para>
///         <b>There is one capture scope, and it is not a choice.</b> <see cref="ProcessLoopbackMode" /> has exactly
///         two members, and <c>ExcludeTargetProcessTree</c> means "everything on the endpoint <i>except</i> the
///         target" — offering it as "this application only" would record every other application on the box while
///         the user interface claimed the opposite. It appears nowhere in this product.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsProcessAudioCaptureSource : IProcessAudioCaptureSource
{
    /// <summary>
    ///     The only loopback mode this product uses: the target process and its descendants. Exposed so a test can
    ///     assert it without reading source, because the alternative member is a privacy inversion rather than an
    ///     option (see the class remarks).
    /// </summary>
    internal const ProcessLoopbackMode CaptureMode = ProcessLoopbackMode.IncludeTargetProcessTree;

    // NAudio's own default. Smaller values cut latency the transcriber cannot use and raise the wake-up rate.
    private const int CaptureBufferMilliseconds = 100;

    // One second of 16 kHz mono int16 output. Chosen for allocation comfort: the 32 KB frame ceiling is
    // TranscriptionHub.PushAudioFrame's check on an untrusted client payload, not a property of the lane.
    private const int DrainChunkBytes = 32000;

    // Belt-and-braces only. ReadFully = false already terminates the drain; one WASAPI packet can never produce
    // more 16 kHz mono output than it carried input, so tripping this cap means a bug in the conversion chain.
    private const int MaxDrainIterationsPerPacket = 64;

    private readonly ILiveTranscriptionSessionRegistry _registry;
    private readonly ILogger<WindowsProcessAudioCaptureSource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="registry">
    ///     The in-process audio seam. Deliberately not <c>ITranscriptionService</c>: a persist-free live session
    ///     never touches that service, so hanging audio there would make the persist-free path depend on it.
    /// </param>
    /// <param name="logger">Enumeration failures are reported here; they never fail the picker.</param>
    public WindowsProcessAudioCaptureSource(ILiveTranscriptionSessionRegistry registry,
        ILogger<WindowsProcessAudioCaptureSource> logger)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsSupported =>
        OperatingSystem.IsWindows()
        && ProcessAudioCaptureSupport.IsBuildSupported(Environment.OSVersion.Version.Major, Environment.OSVersion.Version.Build);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<ProcessAudioCaptureCandidate>> ListCandidatesAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            return ValueTask.FromResult<IReadOnlyList<ProcessAudioCaptureCandidate>>([]);
        }

        // The CoreAudio calls are blocking COM; the picker is a request path, so they go off the request thread.
        return new ValueTask<IReadOnlyList<ProcessAudioCaptureCandidate>>(Task.Run(ListCandidatesCore, cancellationToken));
    }

    /// <inheritdoc />
    public async Task CaptureAsync(Guid sessionId, int processId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        if (!IsSupported)
        {
            throw new TranscriptionProcessCaptureNotSupportedException($"Transcription session {sessionId} cannot capture process {processId}. "
                                                                       + TranscriptionProcessCaptureNotSupportedException.DefaultMessage);
        }

        // Two version numbers, two jobs — do not merge them. IsSupported gates the CAPABILITY on Microsoft's
        // documented build 20348 for AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS; this guard is what satisfies CA1416
        // against NAudio's [SupportedOSPlatform("windows10.0.19041.0")] annotation on WithProcessLoopback. 20348 is
        // the higher floor, so this branch is unreachable in practice — the analyzer cannot know that.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new TranscriptionProcessCaptureNotSupportedException($"Transcription session {sessionId} cannot capture process {processId}. "
                                                                       + TranscriptionProcessCaptureNotSupportedException.DefaultMessage);
        }

        // BuildAsync, never Build: the process-loopback activation path is asynchronous and Build() throws for it.
        // No WithFormat either — the process-loopback virtual device rejects AutoConvertPcm, so a format it does
        // not accept is an IAudioClient::Initialize failure rather than a silent resample. Taking NAudio's
        // documented 44.1 kHz stereo float fallback and converting in managed code is one path that always
        // initialises, at the cost of a resample the target hardware will not notice.
        var recorder = await new WasapiRecorderBuilder()
                             .WithProcessLoopback(checked((uint)processId), CaptureMode)
                             .WithBufferLength(CaptureBufferMilliseconds)
                             .BuildAsync()
                             .ConfigureAwait(false);

        await using (recorder.ConfigureAwait(false))
        {
            var converter = new Wasapi16kMonoPcmConverter(recorder.WaveFormat);
            var rented = ArrayPool<byte>.Shared.Rent(DrainChunkBytes);
            try
            {
                // CaptureAsync initialises and starts the audio client itself and throws "Already recording" if
                // StartRecording ran first; its own finally stops and resets the client when the enumeration ends
                // or the token is cancelled, so there is nothing left for this method to stop.
                await foreach (var buffer in recorder.CaptureAsync(cancellationToken).ConfigureAwait(false))
                {
                    converter.Write(buffer.Data.Span);

                    for (var i = 0; i < MaxDrainIterationsPerPacket; i++)
                    {
                        var written = converter.Read(rented);
                        if (written == 0)
                        {
                            break;
                        }

                        // The registry copies the frame before queueing it, so the rented array is free to be
                        // reused the moment this returns. It also returns without waiting for inference, which is
                        // what keeps this loop from dropping audio at the source.
                        await _registry.PushAudioAsync(sessionId, TranscriptChannel.Others, rented.AsMemory(0, written), cancellationToken)
                                       .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private IReadOnlyList<ProcessAudioCaptureCandidate> ListCandidatesCore()
    {
        var sessions = new List<(int ProcessId, bool Active)>();

        // Every one of these wraps a COM object. Leaving one undisposed defers the underlying release to the RCW
        // finalizer, and the picker is a poll-able route, so an undisposed collection per poll piles audio-engine
        // releases onto a garbage collection that may not come soon.
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                CollectDeviceSessions(device, sessions);
            }
        }

        // One application playing to two endpoints, or holding an idle session beside a playing one, enumerates
        // more than once. De-duplicating and OR-ing activity is pure, names no WASAPI type, and is therefore the
        // one part of the picker the Linux gate can run: see ProcessAudioCaptureCandidates.
        return ProcessAudioCaptureCandidates.Aggregate(sessions, ResolveProcessName);
    }

    private static void CollectDeviceSessions(MMDevice device, List<(int ProcessId, bool Active)> sessions)
    {
        using var manager = device.AudioSessionManager;
        using var collection = manager.Sessions;
        for (var index = 0; index < collection.Count; index++)
        {
            using var session = collection[index];
            if (session.IsSystemSoundsSession)
            {
                continue;
            }

            // AudioSessionManager.Sessions returns Active, Inactive AND Expired alike. An expired session belongs
            // to a process that has already gone, so listing it offers the operator a target that can only fail;
            // an inactive one is a real process that simply is not playing right now, which is what HasAudio is
            // for. This loop cannot be exercised on the Linux gate — it is Windows-only COM — which is why it now
            // does nothing but collect pairs and hands every decision to a helper that can be.
            var state = session.State;
            if (state == AudioSessionState.AudioSessionStateExpired)
            {
                continue;
            }

            sessions.Add(((int)session.GetProcessID, state == AudioSessionState.AudioSessionStateActive));
        }
    }

    private string ResolveProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch (ArgumentException exception)
        {
            // The process exited between enumeration and lookup. Normal, not an error: the row still names a pid
            // the operator may recognise, and starting capture against it will fail with its own message.
            _logger.LogDebug(exception, "Process {ProcessId} holds an audio session but could not be resolved.", processId);
            return $"PID {processId}";
        }
        catch (InvalidOperationException exception)
        {
            _logger.LogDebug(exception, "Process {ProcessId} holds an audio session but could not be resolved.", processId);
            return $"PID {processId}";
        }
    }
}
