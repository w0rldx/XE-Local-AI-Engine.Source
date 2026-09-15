namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     The floor for every host without WASAPI process loopback: Linux, macOS, and Windows below
///     <see cref="ProcessAudioCaptureSupport.MinimumWindowsBuild" />. Mirrors <c>NoOpSandboxProcessGroupKiller</c>.
/// </summary>
/// <remarks>
///     It fails <b>closed</b>: <see cref="CaptureAsync" /> throws a named error rather than returning quietly, so a
///     caller can never open a live session that silently receives no audio. The picker returns an empty list
///     instead of throwing, because "nothing to offer" is a normal answer for a list.
/// </remarks>
internal sealed class NotSupportedProcessAudioCaptureSource : IProcessAudioCaptureSource
{
    public bool IsSupported => false;

    public ValueTask<IReadOnlyList<ProcessAudioCaptureCandidate>> ListCandidatesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<ProcessAudioCaptureCandidate>>([]);

    public Task CaptureAsync(Guid sessionId, int processId, CancellationToken cancellationToken) =>
        throw new TranscriptionProcessCaptureNotSupportedException(
            $"Per-application audio capture is not available on this host, so transcription session {sessionId} cannot capture process {processId}. "
            + TranscriptionProcessCaptureNotSupportedException.DefaultMessage);
}
