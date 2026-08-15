namespace XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>
///     Where the training runtime lives on disk, and where the pinned Python scripts are found.
/// </summary>
/// <remarks>
///     The venv root is machine-global (under <c>LocalApplicationData/XE-Local-AI-Engine</c>), the same base the
///     llama.cpp binaries and source builds use, so one provision serves every node profile on the box and the existing
///     uninstaller sweep already reaches it.
/// </remarks>
internal static class TrainingRuntimeLayout
{
    public const string ProbeScriptName = "probe.py";
    public const string ProjectFileName = "pyproject.toml";
    public const string LockfileName = "uv.lock";
    public const string StateFileName = "installed-training-runtime.json";

    /// <summary>The name the shipped scripts are linked under in the publish output (see the Client csproj).</summary>
    private const string PublishedScriptsDirectoryName = "training-scripts";

    /// <summary>The repo-relative source of the same scripts, used by dev and test runs.</summary>
    private const string RepositoryScriptsRelativePath = "tools/training";

    public static string DefaultCacheRoot()
    {
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "XE-Local-AI-Engine",
            "training-runtime");
    }

    public static string VenvRoot(string cacheRoot)
    {
        return Path.Combine(cacheRoot, "venv");
    }

    public static string ActiveVenv(string cacheRoot)
    {
        return Path.Combine(VenvRoot(cacheRoot), "active");
    }

    public static string StagingVenv(string cacheRoot)
    {
        return Path.Combine(VenvRoot(cacheRoot), ".staging");
    }

    public static string BackupVenv(string cacheRoot)
    {
        return Path.Combine(VenvRoot(cacheRoot), ".backup");
    }

    public static string StatePath(string cacheRoot)
    {
        return Path.Combine(cacheRoot, StateFileName);
    }

    /// <summary>The interpreter inside an adopted venv.</summary>
    public static string InterpreterPath(string venvDirectory)
    {
        return Path.Combine(venvDirectory, ".venv", "bin", "python");
    }

    /// <summary>
    ///     Resolves the directory holding <c>probe.py</c> / <c>pyproject.toml</c> / <c>uv.lock</c>. The published app
    ///     carries them beside the executable; a dev or test run reads them straight out of the working tree, which is
    ///     why the repo path is a fallback rather than the only answer — the repo root is outside the publish glob and
    ///     does not exist in a shipped install.
    /// </summary>
    public static string ResolveScriptsDirectory()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedScriptsDirectoryName);
        if (File.Exists(Path.Combine(published, LockfileName)))
        {
            return published;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryScriptsRelativePath);
            if (File.Exists(Path.Combine(candidate, LockfileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        // Nothing found: return the published path so the prerequisite probe reports a missing lockfile against the
        // location a shipped install would actually use, rather than inventing one.
        return published;
    }
}
