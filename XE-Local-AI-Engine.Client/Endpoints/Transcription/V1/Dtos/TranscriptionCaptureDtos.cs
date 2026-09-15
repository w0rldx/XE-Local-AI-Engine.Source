namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1;

/// <summary>One process the per-application capture picker can target.</summary>
public sealed class CaptureProcessResponse
{
    /// <summary>The operating-system process id, which is what the start request carries back.</summary>
    public required int Pid { get; init; }

    /// <summary>The process name, or a <c>PID n</c> placeholder when it exited between enumeration and lookup.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the process holds a render audio session right now.</summary>
    public required bool HasAudio { get; init; }
}

/// <summary>
///     What the capture picker answers. <c>supported</c> separates "this operating system cannot do it" from
///     "nothing is playing right now", which an empty list on its own would not tell the operator.
/// </summary>
public sealed class CaptureProcessListResponse
{
    /// <summary>Whether this host can capture process audio at all.</summary>
    public required bool Supported { get; init; }

    /// <summary>The processes holding an audio session. Always empty when <see cref="Supported" /> is false.</summary>
    public required IReadOnlyList<CaptureProcessResponse> Processes { get; init; }
}

/// <summary>Starts per-application capture for one live session. There is no scope field.</summary>
public sealed class StartProcessCaptureRequest
{
    /// <summary>The session, from the route. It must already be live.</summary>
    public Guid SessionId { get; init; }

    /// <summary>
    ///     The process to capture. Its <b>child processes are included</b> — that is the only mode WASAPI offers,
    ///     and the user-facing copy says so rather than offering a choice that cannot be implemented.
    /// </summary>
    public int ProcessId { get; init; }
}

/// <summary>Whether a session currently has a server-side capture attached.</summary>
public sealed class ProcessCaptureStatusResponse
{
    /// <summary>The session the capture belongs to.</summary>
    public required Guid SessionId { get; init; }

    /// <summary>Whether capture is running for it.</summary>
    public required bool Capturing { get; init; }
}

/// <summary>
///     The typed refusal both capture failures return, in the same reason-plus-message shape
///     <c>TranscriptionRuntimeBlockedResponse</c> uses, so the SPA branches on a code rather than on prose.
/// </summary>
public sealed class ProcessCaptureBlockedResponse
{
    /// <summary>The reason code the SPA matches.</summary>
    public required string Reason { get; init; }

    /// <summary>What to tell the operator.</summary>
    public required string Message { get; init; }
}
