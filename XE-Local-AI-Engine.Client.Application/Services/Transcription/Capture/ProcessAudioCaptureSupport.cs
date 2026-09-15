namespace XE_Local_AI_Engine.Client.Services.Transcription.Capture;

/// <summary>
///     The one place the Windows build floor for WASAPI process loopback is decided. Pure, so the policy is testable
///     on every operating system rather than only on the one it describes.
/// </summary>
internal static class ProcessAudioCaptureSupport
{
    /// <summary>
    ///     Microsoft documents <c>AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS</c> — the struct that actually carries the
    ///     loopback mode — as Windows Server 2022 / build 20348. NAudio annotates
    ///     <c>WasapiRecorderBuilder.WithProcessLoopback</c> with <c>[SupportedOSPlatform("windows10.0.19041.0")]</c>,
    ///     but that is an ANALYZER annotation satisfying CA1416, not compatibility evidence. Gate the capability on
    ///     the documented number: being wrong this way hides a feature, being wrong the other way is a hard COM
    ///     failure the operator cannot act on.
    /// </summary>
    internal const int MinimumWindowsBuild = 20348;

    /// <summary>
    ///     Whether an operating-system version clears the documented floor. Takes the version rather than reading
    ///     <see cref="Environment.OSVersion" /> so a test drives it; comparing <c>IsSupported</c> against its own
    ///     predicate would be a tautology that cannot fail.
    /// </summary>
    /// <remarks>
    ///     The minor component is deliberately absent: every Windows 10 and 11 release reports major 10, minor 0,
    ///     and an unused parameter is an IDE0060 build error here.
    /// </remarks>
    internal static bool IsBuildSupported(int major, int build) =>
        major > 10 || (major == 10 && build >= MinimumWindowsBuild);
}
