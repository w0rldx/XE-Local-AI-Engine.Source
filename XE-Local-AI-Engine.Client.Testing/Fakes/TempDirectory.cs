namespace XE_Local_AI_Engine.Client.Testing.Fakes;

/// <summary>A throwaway directory that deletes itself at the end of a test: the one temp-directory double every test project shares.</summary>
public sealed class TempDirectory : IDisposable
{
    /// <summary>0755: a directory a parent-directory security check accepts under any umask, since it is not world-writable.</summary>
    public const UnixFileMode NotWorldWritable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <param name="prefix">Leading name segment, so a stray directory says which test left it.</param>
    /// <param name="root">
    ///     Where to create it; defaults to the OS temp root. A test needing a specific spot (under the user profile,
    ///     say, for home-path scrubbing) passes its own.
    /// </param>
    /// <param name="unixMode">Mode set on the directory off Windows, for a test whose subject checks it under any umask.</param>
    public TempDirectory(string prefix = "xe-test", string? root = null, UnixFileMode? unixMode = null)
    {
        Path = System.IO.Path.Combine(root ?? System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        if (unixMode is { } mode && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, mode);
        }
    }

    public string Path { get; }

    /// <summary>A path inside this directory. The file itself is not created.</summary>
    public string FilePath(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup: a leftover directory under the OS temp root is not a test failure.
        }
    }
}
