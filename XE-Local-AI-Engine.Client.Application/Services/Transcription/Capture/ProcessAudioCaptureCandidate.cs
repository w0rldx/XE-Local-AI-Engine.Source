namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>One row of the per-application capture picker: a process that currently holds an audio session.</summary>
/// <param name="ProcessId">The operating-system process id the capture targets.</param>
/// <param name="Name">The process name, or a placeholder when the process exited between enumeration and lookup.</param>
/// <param name="HasAudio">
///     Whether the process holds a render audio session right now. A host that cannot enumerate sessions at all
///     reports an empty list rather than rows with <see langword="false" />, so this stays a per-row fact.
/// </param>
public sealed record ProcessAudioCaptureCandidate(int ProcessId, string Name, bool HasAudio);
