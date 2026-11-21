namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Detects a WSL guest from cheap host-local signals: the distro environment variable, then the kernel release and version strings.</summary>
public static class WslHost
{
    private const string WslDistroNameEnvironmentVariable = "WSL_DISTRO_NAME";

    /// <summary>Whether this process runs inside WSL.</summary>
    /// <exception cref="IOException">A kernel signal file exists but could not be read; each caller decides what unknown means.</exception>
    public static bool Detect()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(WslDistroNameEnvironmentVariable)))
        {
            return true;
        }

        return FileContainsWslSignature("/proc/sys/kernel/osrelease") || FileContainsWslSignature("/proc/version");
    }

    private static bool FileContainsWslSignature(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        // Forced sync: callers are synchronous probe seams and constructors; the source is procfs, which never blocks on a device.
#pragma warning disable MA0045 // Contract-forced: sync probe seam, procfs read.
        var contents = File.ReadAllText(path);
#pragma warning restore MA0045
        return contents.Contains("microsoft", StringComparison.OrdinalIgnoreCase)
               || contents.Contains("WSL", StringComparison.OrdinalIgnoreCase);
    }
}
