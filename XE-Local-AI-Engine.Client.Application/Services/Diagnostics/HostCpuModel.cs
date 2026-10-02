namespace XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>Reads the host CPU model name; shared by the benchmark environment facts and the node-info report.</summary>
internal static class HostCpuModel
{
    /// <summary>The CPU model name, or <see langword="null" /> when the platform does not expose one.</summary>
    internal static string? TryRead()
    {
        if (OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        }

        const string cpuInfoPath = "/proc/cpuinfo";
        if (!File.Exists(cpuInfoPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(cpuInfoPath))
        {
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 && line.StartsWith("model name", StringComparison.Ordinal))
            {
                return line[(separator + 1)..].Trim();
            }
        }

        return null;
    }
}
