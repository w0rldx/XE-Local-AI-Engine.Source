namespace XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;

/// <summary>
///     Answers one question once per process: is <c>ffmpeg</c> on <c>PATH</c>?
/// </summary>
/// <remarks>
///     <para>
///         This feeds exactly one thing — the <c>supportsTranscode</c> field on the runtime status — and it never
///         gates a launch flag. The name is deliberate: it advertises that the ENGINE can transcode the containers
///         whisper-server cannot decode natively, into the engine's own owned temporary directory. It does not mean
///         the daemon will convert anything; the daemon is never launched with its convert flag, because that would
///         write every request's audio to disk.
///     </para>
///     <para>
///         The answer is cached for the process lifetime. An operator who installs ffmpeg while the node is running
///         gets the capability at the next restart, which is the same granularity every other PATH-resolved tool in
///         this repository has.
///     </para>
/// </remarks>
internal static class WhisperFfmpegProbe
{
    private static readonly Lazy<string?> Resolved = new(ResolveFromPath, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when an <c>ffmpeg</c> executable was found on <c>PATH</c>.</summary>
    internal static bool IsAvailable => Resolved.Value is not null;

    /// <summary>Resolves the absolute <c>ffmpeg</c> path found on <c>PATH</c>, if any.</summary>
    internal static bool TryResolve(out string path)
    {
        path = Resolved.Value ?? string.Empty;
        return Resolved.Value is not null;
    }

    private static string? ResolveFromPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return null;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(directory, fileName);
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry cannot hold an executable; skip it rather than failing the whole probe.
                continue;
            }

            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
