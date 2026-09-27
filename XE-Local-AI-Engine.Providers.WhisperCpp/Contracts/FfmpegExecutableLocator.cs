namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>Finds the <c>ffmpeg</c> executable on <c>PATH</c>, for the whisper.cpp runtime status and the engine's audio transcoder.</summary>
public static class FfmpegExecutableLocator
{
    /// <summary>
    ///     Returns the absolute path of the first <c>ffmpeg</c> (<c>ffmpeg.exe</c> on Windows) found walking
    ///     <c>PATH</c> in order, or <see langword="null" /> when none is. Uncached: every call walks <c>PATH</c> again.
    /// </summary>
    public static string? ResolveFromPath()
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
