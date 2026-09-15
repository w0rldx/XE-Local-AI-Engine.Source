namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     Captures the audio of one running application server-side and feeds it into a live transcription session. The
///     PCM never crosses SignalR: the browser only picks the process and starts the session.
/// </summary>
/// <remarks>
///     Two implementations, chosen by operating system in <c>AddNodeTranscription</c>:
///     <see cref="WindowsProcessAudioCaptureSource" /> and <see cref="NotSupportedProcessAudioCaptureSource" />.
///     There is no factory and no options object — the only variable is the host.
/// </remarks>
public interface IProcessAudioCaptureSource
{
    /// <summary>Whether this host can capture process audio at all. The runtime-status response publishes it.</summary>
    bool IsSupported { get; }

    /// <summary>
    ///     The processes that currently hold a render audio session, i.e. the ones capture can actually target.
    ///     Empty — never an error — on a host without process loopback and on one where nothing is playing.
    /// </summary>
    ValueTask<IReadOnlyList<ProcessAudioCaptureCandidate>> ListCandidatesAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Captures the target process <b>and its descendants</b> — the only mode WASAPI offers — converting to the
    ///     16 kHz mono int16 PCM the segmenter accepts and pushing it into the session's <c>Others</c> lane. Runs
    ///     until <paramref name="cancellationToken" /> is cancelled.
    /// </summary>
    /// <exception cref="TranscriptionProcessCaptureNotSupportedException">This host cannot capture process audio.</exception>
    Task CaptureAsync(Guid sessionId, int processId, CancellationToken cancellationToken);
}
