namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

using XE_Local_AI_Engine.Providers.HuggingFace.Contracts;

/// <summary>Production <see cref="IFreeSpaceProbe" /> backed by <see cref="DriveInfo" />.</summary>
public sealed class DriveInfoFreeSpaceProbe : IFreeSpaceProbe
{
    /// <inheritdoc />
    public long GetAvailableFreeBytes(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        // Resolve the closest existing ancestor so the probe works before the model directory is first created.
        var probePath = Path.GetFullPath(path);
        while (!Directory.Exists(probePath))
        {
            var parent = Path.GetDirectoryName(probePath);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, probePath, StringComparison.Ordinal))
            {
                break;
            }

            probePath = parent;
        }

        var root = Path.GetPathRoot(probePath);
        if (string.IsNullOrEmpty(root))
        {
            throw new InvalidOperationException("Unable to determine the volume root for the model directory.");
        }

        return new DriveInfo(root).AvailableFreeSpace;
    }
}
