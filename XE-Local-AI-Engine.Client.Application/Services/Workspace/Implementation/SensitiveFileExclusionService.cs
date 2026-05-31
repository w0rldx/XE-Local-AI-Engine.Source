namespace XE_Local_AI_Engine.Client.Services.Workspace.Implementation;

using System.Collections.Frozen;

/// <summary>
///     Name-based <see cref="ISensitiveFileExclusionService" />. The excluded set covers secrets,
///     the host <c>.git</c> baseline (a fresh in-sandbox baseline is created after copy), and generated/heavy output
///     directories. Pattern rules add <c>.env.*</c> and <c>*credentials.enc</c>. The sets are frozen once so lookups
///     are allocation-free.
/// </summary>
internal sealed class SensitiveFileExclusionService : ISensitiveFileExclusionService
{
    // Excluded regardless of entry type: secrets, the host .git baseline, the .ssh store, well-known credential
    // bundles, and generated/heavy output directories. Browser-profile and tooling caches are folded into this set.
    private static readonly FrozenSet<string> ExcludedNames = new[]
    {
        ".git",
        ".ssh",
        ".env",
        "secrets.json",
        "appsettings.Production.json",
        "cloud-credentials.enc",
        "worker-credentials.enc",
        "bin",
        "obj",
        "node_modules",
        "dist",
        "coverage",
        ".vs",
        ".idea"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public bool IsExcluded(string entryName, bool isDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryName);

        if (ExcludedNames.Contains(entryName))
        {
            return true;
        }

        // Pattern rules apply to files only; a directory matching a file pattern is unusual and copied through.
        return !isDirectory && IsSecretFilePattern(entryName);
    }

    private static bool IsSecretFilePattern(string fileName)
    {
        // ".env.local", ".env.production", … (the ".env" base name is already in ExcludedNames).
        if (fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // "*credentials.enc": any cloud/worker credential bundle, not just the two well-known names above.
        return fileName.EndsWith("credentials.enc", StringComparison.OrdinalIgnoreCase);
    }
}
