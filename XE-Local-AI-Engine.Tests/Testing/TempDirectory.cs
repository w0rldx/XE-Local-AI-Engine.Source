namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     A throwaway directory that deletes itself at the end of a test. Several test classes carry a private copy of
///     this shape; new tests share this one.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    /// <param name="prefix">Leading name segment, so a stray directory says which test left it.</param>
    /// <param name="root">
    ///     Where to create it. Defaults to the OS temp root; a test that needs the directory to sit somewhere
    ///     specific — under the user profile, say, to exercise home-path scrubbing — passes its own.
    /// </param>
    public TempDirectory(string prefix = "xe-test", string? root = null)
    {
        Path = System.IO.Path.Combine(root ?? System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>A path inside this directory. The file itself is not created.</summary>
    public string FilePath(string relativePath) => System.IO.Path.Combine(Path, relativePath);

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
