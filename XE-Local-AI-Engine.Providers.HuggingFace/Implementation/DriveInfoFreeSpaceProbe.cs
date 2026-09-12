namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Production <see cref="IFreeSpaceProbe" /> backed by <see cref="DriveInfo" />.
///     <para>
///         Measured on the closest EXISTING ancestor of the path, which is also the directory handed to
///         <see cref="DriveInfo" />. The path ROOT is not that filesystem: on Linux every absolute path roots at
///         <c>/</c>, so measuring the root reported the root filesystem for a models directory, a cache or an
///         instance directory that lives on a mounted data volume. <see cref="DriveInfo" /> resolves the filesystem
///         actually holding a directory when it is given one, and on Windows the ancestor still names its volume.
///     </para>
/// </summary>
public sealed class DriveInfoFreeSpaceProbe : IFreeSpaceProbe
{
    /// <inheritdoc />
    public long GetAvailableFreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Resolve the closest existing ancestor so the probe works before the directory is first created.
        var probePath = Path.GetFullPath(path);
        while (!Directory.Exists(probePath))
        {
            var parent = Path.GetDirectoryName(probePath);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probePath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"No existing directory was found at or above '{path}', so the free space on its volume cannot be measured.");
            }

            probePath = parent;
        }

        return new DriveInfo(probePath).AvailableFreeSpace;
    }
}
