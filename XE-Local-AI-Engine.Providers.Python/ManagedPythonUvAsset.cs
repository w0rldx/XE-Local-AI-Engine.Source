namespace XE_Local_AI_Engine.Providers.Python;

/// <summary>One RID's uv release asset: its digest, the directory it unpacks into (empty when flat), and the executable.</summary>
public sealed record ManagedPythonUvAsset
{
    public required string Rid { get; init; }

    public required string AssetName { get; init; }

    public required string Sha256 { get; init; }

    /// <summary>The directory the archive unpacks into; empty when the executable sits at the archive's top level.</summary>
    public required string ArchiveRootDirectory { get; init; }

    public required string ExecutableName { get; init; }

    /// <summary>True for a zip (Windows), false for a gzip tarball.</summary>
    public required bool IsZip { get; init; }

    public Uri DownloadUri()
    {
        return new Uri($"https://github.com/astral-sh/uv/releases/download/{ManagedPythonPins.UvVersion}/{AssetName}");
    }

    /// <summary>Where the extracted executable lives under a toolchain store root: <c>uv/&lt;version&gt;/[root/]&lt;exe&gt;</c>.</summary>
    public string ExecutablePath(string storeRoot)
    {
        return Path.Combine(storeRoot, "uv", ManagedPythonPins.UvVersion, ArchiveRootDirectory, ExecutableName);
    }
}
