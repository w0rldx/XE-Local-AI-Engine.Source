namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>Resolves the operator-owned cache for managed inference runtimes and source builds.</summary>
public static class RuntimeCacheDirectory
{
    public const string EnvironmentVariable = "XE_RUNTIME_DATA_DIR";

    public static string Resolve() =>
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string Resolve(string? configuredRoot)
    {
        if (configuredRoot is null)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XE-Local-AI-Engine");
        }

        if (string.IsNullOrWhiteSpace(configuredRoot) || configuredRoot.Any(char.IsControl)
                                                      || !Path.IsPathFullyQualified(configuredRoot))
        {
            throw new InvalidOperationException($"{EnvironmentVariable} must be a non-empty absolute directory path.");
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"{EnvironmentVariable} is not a valid directory path.");
        }
    }
}
