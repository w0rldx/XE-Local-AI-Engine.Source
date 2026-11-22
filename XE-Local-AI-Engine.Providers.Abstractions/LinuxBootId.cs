namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The running kernel's boot id, <c>/proc/sys/kernel/random/boot_id</c>: a process receipt that recorded another one names a process of
///     a previous boot, which no live process can still be.
/// </summary>
/// <remarks>Only a successful read is cached; a failed one is retried on the next call. Null off Linux and while the file cannot be read.</remarks>
public static class LinuxBootId
{
    private const string BootIdPath = "/proc/sys/kernel/random/boot_id";

    private static string? _current;

    /// <summary>This boot's id, or null off Linux or when it could not be read.</summary>
    public static string? Current => _current ??= OperatingSystem.IsLinux() ? Read(BootIdPath) : null;

    /// <summary>The trimmed content of <paramref name="path" />, or null when it is missing, unreadable or blank. Never throws for a read failure.</summary>
    internal static string? Read(string path)
    {
        try
        {
            // Forced sync: read from the synchronous startup reaper and spawn paths; the source is procfs, which never blocks on a device.
#pragma warning disable MA0045 // Contract-forced: sync reaper contracts, procfs read.
            var id = File.ReadAllText(path).Trim();
#pragma warning restore MA0045
            return id.Length > 0 ? id : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
