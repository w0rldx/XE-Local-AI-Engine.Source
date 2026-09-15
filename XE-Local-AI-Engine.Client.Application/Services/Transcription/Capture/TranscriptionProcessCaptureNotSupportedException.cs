namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     Raised when per-application audio capture is asked for on a host that cannot do it: any non-Windows host, or
///     a Windows build below <see cref="ProcessAudioCaptureSupport.MinimumWindowsBuild" />.
/// </summary>
/// <remarks>
///     A named type rather than a bare <see cref="NotSupportedException" /> because the endpoint turns it into a
///     typed 400 the SPA can render. The alternative — returning quietly and starting nothing — would open a live
///     session that never receives a byte and looks to the operator like a broken microphone.
/// </remarks>
public sealed class TranscriptionProcessCaptureNotSupportedException : Exception
{
    /// <summary>Creates the exception with the default explanation.</summary>
    public TranscriptionProcessCaptureNotSupportedException()
        : base(DefaultMessage)
    {
    }

    /// <summary>Creates the exception with an explicit message.</summary>
    public TranscriptionProcessCaptureNotSupportedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with an explicit message and the underlying cause.</summary>
    public TranscriptionProcessCaptureNotSupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>What the endpoint reports when nothing more specific is known.</summary>
    public const string DefaultMessage =
        "Per-application audio capture needs Windows Server 2022 / Windows 10 build 20348 or later; this host cannot capture process audio.";
}
