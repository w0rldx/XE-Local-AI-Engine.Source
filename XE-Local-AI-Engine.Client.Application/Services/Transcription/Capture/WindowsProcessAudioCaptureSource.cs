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
///     Type-level <see cref="SupportedOSPlatformAttribute" /> rather than a CA1416 suppression, matching
///     <c>WindowsImageJobObjectProcessHandle</c>; <c>AddNodeTranscription</c> registers this only behind
///     <c>OperatingSystem.IsWindows()</c>, which is what makes the attribute honest. There is one capture scope and it
///     is not a choice: <see cref="ProcessLoopbackMode" />'s other member, <c>ExcludeTargetProcessTree</c>, means
///     everything on the endpoint EXCEPT the target, so it appears nowhere in this product.
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
    ///     The in-process audio seam, not <c>ITranscriptionService</c> — a persist-free live session never touches it,
    ///     so audio hung there would make that path depend on it.
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

        // Two version numbers, two jobs — do not merge them. IsSupported gates the CAPABILITY on Microsoft's documented build
        // 20348; this guard satisfies CA1416 against NAudio's 19041 annotation, so the branch is unreachable in practice.
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            throw new TranscriptionProcessCaptureNotSupportedException($"Transcription session {sessionId} cannot capture process {processId}. "
                                                                       + TranscriptionProcessCaptureNotSupportedException.DefaultMessage);
        }

        // BuildAsync, never Build: the process-loopback activation path is asynchronous and Build() throws for it. No WithFormat
        // either — the virtual device rejects AutoConvertPcm — so take NAudio's documented 44.1 kHz stereo float and convert here.
        var recorder = await new WasapiRecorderBuilder()
                             .WithProcessLoopback(checked((uint)processId), CaptureMode)
                             .WithBufferLength(CaptureBufferMilliseconds)
                             .BuildAsync();

        await using (recorder)
        {
            var converter = new Wasapi16kMonoPcmConverter(recorder.WaveFormat);
            var rented = ArrayPool<byte>.Shared.Rent(DrainChunkBytes);
            try
            {
                // CaptureAsync initialises and starts the audio client itself and throws "Already recording" if StartRecording
                // ran first; its own finally stops and resets the client, so there is nothing left for this method to stop.
                await foreach (var buffer in recorder.CaptureAsync(cancellationToken))
                {
                    converter.Write(buffer.Data.Span);

                    for (var i = 0; i < MaxDrainIterationsPerPacket; i++)
                    {
                        var written = converter.Read(rented);
                        if (written == 0)
                        {
                            break;
                        }

                        // The registry copies the frame before queueing it, so the rented array is free the moment this
                        // returns. It also returns without waiting for inference, which keeps this loop from dropping audio.
                        await _registry.PushAudioAsync(sessionId, TranscriptChannel.Others, rented.AsMemory(0, written), cancellationToken);
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

        // Every one of these wraps a COM object; leaving one undisposed defers the release to the RCW finalizer, and the picker
        // is a poll-able route, so an undisposed collection per poll piles audio-engine releases onto a collection that may lag.
        using var enumerator = new MMDeviceEnumerator();
        using var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var device in devices)
        {
            using (device)
            {
                CollectDeviceSessions(device, sessions);
            }
        }

        // One application playing to two endpoints, or holding an idle session beside a playing one, enumerates more than once.
        // De-duplicating and OR-ing activity names no WASAPI type, so ProcessAudioCaptureCandidates is what the Linux gate runs.
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

            // AudioSessionManager.Sessions returns Active, Inactive AND Expired alike: an expired session's process has already
            // gone, so listing it offers a target that can only fail, while an inactive one is real — which is what HasAudio is for.
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
