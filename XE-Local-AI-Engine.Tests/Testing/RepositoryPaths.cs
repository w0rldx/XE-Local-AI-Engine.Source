namespace XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Discovers the repository root from the test output directory by walking up until the solution file
///     (<c>XE-Local-AI-Engine.slnx</c>) is found, and resolves project-relative paths beneath it. The repository is
///     standalone — project directories live directly under the root, replacing the old monorepo-era
///     <c>Apps/XE-Local-AI-Engine/...</c> layout assumptions.
/// </summary>
internal static class RepositoryPaths
{
    private const string SolutionFileName = "XE-Local-AI-Engine.slnx";

    /// <summary>The absolute repository root (the directory that contains the solution file).</summary>
    public static string Root { get; } = DiscoverRoot();

    /// <summary>Resolves a path under the repository root.</summary>
    public static string Combine(params string[] segments)
    {
        return Path.Combine([Root, .. segments]);
    }

    /// <summary>Resolves a path under the client host project (<c>XE-Local-AI-Engine.Client</c>).</summary>
    public static string ClientProject(params string[] segments)
    {
        return Path.Combine([Root, "XE-Local-AI-Engine.Client", .. segments]);
    }

    private static string DiscoverRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new DirectoryNotFoundException($"Could not locate the repository root (containing '{SolutionFileName}') from the test output directory.");
    }
}
