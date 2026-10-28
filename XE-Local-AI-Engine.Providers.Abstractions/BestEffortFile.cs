namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Cleanup deletes that never throw for a file or directory that is locked, read-only or already gone.</summary>
/// <remarks>
///     Only <see cref="IOException" /> and <see cref="UnauthorizedAccessException" /> are swallowed: a leftover costs
///     disk, never correctness, and must not mask the failure the caller is already handling. A null or malformed path
///     reads as absent and is a no-op. Any exception other than the two above still surfaces.
/// </remarks>
public static class BestEffortFile
{
    public static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>Deletes <paramref name="path" /> and everything beneath it.</summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }
}
