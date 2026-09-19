namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>One row of the per-application capture picker: a process that currently holds an audio session.</summary>
public sealed class ProcessAudioCaptureCandidate
{
    /// <summary>The operating-system process id the capture targets.</summary>
    public required int ProcessId { get; init; }

    /// <summary>The process name, or a placeholder when the process exited between enumeration and lookup.</summary>
    public required string Name { get; init; }

    /// <summary>
    ///     Whether the process holds a render audio session right now. A host that cannot enumerate sessions at all
    ///     reports an empty list rather than rows with <see langword="false" />, so this stays a per-row fact.
    /// </summary>
    public required bool HasAudio { get; init; }
}
