namespace XE_Local_AI_Engine.Providers.ProcessSupervision;

/// <summary>
///     Recognises a child runtime's report that it could not bind the local port it was given: the loser of a
///     probe-then-bind race, which the next attempt fixes by allocating another port.
/// </summary>
/// <remarks>
///     One marker list for the llama, image and transcription supervisors. llama.cpp and whisper.cpp print "couldn't bind";
///     sd-server's exact wording is unverified, so the common forms are matched too.
/// </remarks>
public static class PortBindFailure
{
    private static readonly string[] Markers = ["address already in use", "couldn't bind", "could not bind", "failed to bind", "bind failed"];

    /// <summary>Whether <paramref name="output" /> (a stderr tail or one output line) reports a failed bind.</summary>
    public static bool IsReportedIn(string? output) =>
        output is not null
        && Array.Exists(Markers, marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase));
}
