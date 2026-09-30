namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Globalization;
using System.Runtime.InteropServices;

/// <summary>
///     An open <c>O_PATH</c> directory descriptor for a tree the isolated chain will bind, together with the canonical path it was opened
///     from.
/// </summary>
/// <remarks>
///     The descriptor, not the path, is what <c>bwrap</c> is given, and that is the point: a pathname is re-resolved by <c>bwrap</c>, in
///     another process, at a later moment, so anything able to rename a component in between redirects the mount, while a descriptor names
///     the already-validated inode. FD binds are MANDATORY — there is no pathname fallback, and a host where the chain cannot be
///     established reports the capability absent. The descriptor is deliberately NOT close-on-exec: it must survive <c>setsid</c> →
///     <c>systemd-run --scope</c> → <c>bwrap</c>, which was measured rather than assumed, so no <c>posix_spawn</c> shim is needed.
/// </remarks>
internal sealed class SandboxTrustedDescriptor : IDisposable
{
    private int _fileDescriptor;

    internal SandboxTrustedDescriptor(int fileDescriptor, string path)
    {
        _fileDescriptor = fileDescriptor;
        Path = path;
    }

    /// <summary>The raw descriptor number, as it must be spelled in the <c>bwrap</c> argument vector.</summary>
    public int FileDescriptor => _fileDescriptor;

    /// <summary>The canonical host path the descriptor was opened from, for logging and for the mount destination.</summary>
    public string Path { get; }

    /// <summary>The descriptor number as the argument vector spells it.</summary>
    public string Argument => _fileDescriptor.ToString(CultureInfo.InvariantCulture);

    public void Dispose()
    {
        var descriptor = Interlocked.Exchange(ref _fileDescriptor, value: -1);
        if (descriptor >= 0)
        {
            _ = close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fileDescriptor);
}
