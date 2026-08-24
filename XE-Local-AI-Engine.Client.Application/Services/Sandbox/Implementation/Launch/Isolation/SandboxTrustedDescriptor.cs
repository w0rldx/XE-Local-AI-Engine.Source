namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
///     An open <c>O_PATH</c> directory descriptor for a tree the isolated chain will bind, together with the canonical
///     path it was opened from.
///     <para>
///         The descriptor — not the path — is what <c>bwrap</c> is given (<c>--bind-fd</c> / <c>--ro-bind-fd</c>), and
///         that is the entire point. A pathname handed to <c>bwrap</c> is re-resolved by <c>bwrap</c>, in a different
///         process, at a later moment; anything that can rename a component between the engine's check and that
///         resolution redirects the mount. A descriptor names the inode that was already validated, so there is no
///         second resolution to race. The plan makes fd binds MANDATORY for exactly this reason: there is no pathname
///         fallback, and a host where the descriptor chain cannot be established reports the capability as absent.
///     </para>
///     <para>
///         The descriptor is deliberately NOT close-on-exec: it has to survive
///         <c>setsid</c> → <c>systemd-run --scope</c> → <c>bwrap</c>, all three of which exec in place. That was
///         measured on this host rather than assumed, and it is why no <c>posix_spawn</c> shim is needed.
///     </para>
/// </summary>
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

/// <summary>
///     Opens <see cref="SandboxTrustedDescriptor" />s through an <c>openat2(2)</c> walk that refuses to traverse a
///     symlink or to leave the directory it started from, validating the ownership of every component as the FD it
///     just opened rather than as a path it might re-resolve.
///     <para>
///         The ownership rule is the one the plan fixes: ancestors must be root-owned and non-writable by anyone else
///         (a root-owned sticky directory such as <c>/tmp</c> counts, because entries in it can only be removed by
///         their own owner), and the target itself must belong to the engine's own user. A jail anchor must in
///         addition be <c>0700</c>. That combination accepts the three shapes this engine actually uses — a jail under
///         <c>/tmp</c>, a runtime root under <c>/home/&lt;user&gt;</c>, a data directory under either — and rejects a
///         tree anyone else on the box could have swapped.
///     </para>
/// </summary>
internal static class SandboxTrustedDescriptorOpener
{
    // openat2(2). The number is identical on x86-64 and AArch64; the syscall landed late enough to have been
    // allocated uniformly, and those two are the architectures this engine ships for.
    private const long OpenAt2SystemCall = 437;

    private const int OpenHowBytes = 24;

    // O_PATH: open the inode without opening the file. It is the right mode for a bind source — no read permission is
    // implied, nothing is held open for I/O, and both statx(AT_EMPTY_PATH) and bwrap's --bind-fd accept it.
    private const ulong OpenPath = 0x200000;
    private const ulong OpenDirectory = 0x10000;
    private const ulong OpenCloseOnExec = 0x80000;

    // RESOLVE_NO_SYMLINKS refuses the open if ANY component (the final one included) is a symlink; RESOLVE_BENEATH
    // refuses anything that would escape the starting descriptor. Together they make the walk incapable of leaving the
    // tree it was pointed at, which is what a pathname-based check can only approximate.
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;

    private const uint PrivateDirectoryMode = 0b111_000_000;

    /// <summary>
    ///     Opens <paramref name="path" /> as an inheritable <c>O_PATH</c> directory descriptor.
    ///     <paramref name="requirePrivateMode" /> additionally demands <c>0700</c>, which is what a writable jail must
    ///     be. Throws <see cref="SandboxIsolationUnavailableException" /> with a measured reason otherwise.
    /// </summary>
    public static SandboxTrustedDescriptor Open(string path, bool requirePrivateMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsLinux())
        {
            throw new SandboxIsolationUnavailableException("the filesystem boundary is Linux-only");
        }

        if (!System.IO.Path.IsPathRooted(path))
        {
            throw new SandboxIsolationUnavailableException($"'{path}' is not an absolute path");
        }

        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (components.Length == 0)
        {
            throw new SandboxIsolationUnavailableException("the filesystem root cannot be bound as a jail or a read-only tree");
        }

        var ownUserId = geteuid();
        var current = OpenRoot();
        var currentPath = string.Empty;

        try
        {
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component is "." or "..")
                {
                    throw new SandboxIsolationUnavailableException($"'{path}' contains a relative component and is rejected");
                }

                var isTarget = index == components.Length - 1;
                var next = OpenBeneath(current.Descriptor, component, currentPath, closeOnExec: !isTarget);
                current.Dispose();
                current = next;
                currentPath = string.Concat(currentPath, "/", component);

                var facts = SandboxUnixMetadata.TryReadDescriptor(current.Descriptor)
                            ?? throw new SandboxIsolationUnavailableException($"'{currentPath}' could not be stat'ed after it was opened");

                if (isTarget)
                {
                    EnsureTargetIsOwnedByTheEngine(facts, currentPath, ownUserId, requirePrivateMode);
                }
                else
                {
                    EnsureAncestorIsTrustworthy(facts, currentPath, ownUserId);
                }
            }

            var descriptor = new SandboxTrustedDescriptor(current.Release(), currentPath);
            current.Dispose();

            return descriptor;
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     The ancestor rule. Root-owned and non-writable is the ordinary case; a root-owned STICKY directory is
    ///     accepted because that is what makes a shared <c>/tmp</c> safe to sit under — an entry in a sticky directory
    ///     can only be renamed or removed by its own owner, so nobody else can substitute the jail. A directory owned
    ///     by the engine's own user is accepted when it is not writable by group or world, which covers the user's
    ///     home and the node data directory.
    /// </summary>
    private static void EnsureAncestorIsTrustworthy(SandboxUnixFileFacts facts, string path, uint ownUserId)
    {
        if (!facts.IsDirectory)
        {
            throw new SandboxIsolationUnavailableException($"'{path}' is not a directory");
        }

        if (facts.UserId == 0)
        {
            if (facts.IsStickyDirectory || !facts.IsGroupOrWorldWritable)
            {
                return;
            }

            throw new SandboxIsolationUnavailableException($"'{path}' is root-owned but world-writable without the sticky bit");
        }

        if (facts.UserId == ownUserId && !facts.IsGroupOrWorldWritable)
        {
            return;
        }

        throw new SandboxIsolationUnavailableException(facts.UserId == ownUserId
            ? $"'{path}' is writable by group or world"
            : $"'{path}' is owned by uid {facts.UserId}, which is neither root nor this engine");
    }

    private static void EnsureTargetIsOwnedByTheEngine(SandboxUnixFileFacts facts, string path, uint ownUserId, bool requirePrivateMode)
    {
        if (!facts.IsDirectory)
        {
            throw new SandboxIsolationUnavailableException($"'{path}' is not a directory");
        }

        if (facts.UserId != ownUserId)
        {
            throw new SandboxIsolationUnavailableException($"'{path}' is owned by uid {facts.UserId} rather than by this engine");
        }

        if (facts.IsGroupOrWorldWritable)
        {
            throw new SandboxIsolationUnavailableException($"'{path}' is writable by group or world");
        }

        if (requirePrivateMode && facts.PermissionBits != PrivateDirectoryMode)
        {
            // Octal, because that is how the mode is spelled everywhere else a reader will look it up. .NET has no
            // octal numeric format specifier, hence the explicit base conversion.
            throw new SandboxIsolationUnavailableException(string.Create(CultureInfo.InvariantCulture,
                $"'{path}' is mode 0{Convert.ToString(facts.PermissionBits, toBase: 8)} rather than the 0700 a sandbox jail must be"));
        }
    }

    private static OwnedDescriptor OpenRoot()
    {
        var rootBytes = NullTerminated("/");
        var descriptor = open(rootBytes, (int)(OpenPath | OpenDirectory | OpenCloseOnExec));

        return descriptor < 0
            ? throw new SandboxIsolationUnavailableException($"the filesystem root could not be opened (errno {Marshal.GetLastPInvokeError()})")
            : new OwnedDescriptor(descriptor);
    }

    private static OwnedDescriptor OpenBeneath(int directoryDescriptor, string component, string parentPath, bool closeOnExec)
    {
        var flags = OpenPath | OpenDirectory | (closeOnExec ? OpenCloseOnExec : 0);
        var how = new byte[OpenHowBytes];
        BitConverter.TryWriteBytes(how.AsSpan(start: 0), flags);
        BitConverter.TryWriteBytes(how.AsSpan(start: 8), value: 0UL);
        BitConverter.TryWriteBytes(how.AsSpan(start: 16), ResolveNoSymlinks | ResolveBeneath);

        var descriptor = (int)syscall(OpenAt2SystemCall, directoryDescriptor, NullTerminated(component), how, OpenHowBytes);
        if (descriptor >= 0)
        {
            return new OwnedDescriptor(descriptor);
        }

        var error = Marshal.GetLastPInvokeError();
        const int Eloop = 40;
        const int Enosys = 38;
        var reason = error switch
        {
            Eloop => $"'{parentPath}/{component}' is a symlink, which the isolated chain refuses to traverse",
            Enosys => "this kernel does not implement openat2(2), so no bind source can be opened without a symlink race",
            _ => $"'{parentPath}/{component}' could not be opened beneath its parent (errno {error})"
        };

        throw new SandboxIsolationUnavailableException(reason);
    }

    private static byte[] NullTerminated(string value)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);

        return bytes;
    }

    // A descriptor owned by the walk itself: closed on every exit path unless Release() hands it to the caller.
    private sealed class OwnedDescriptor : IDisposable
    {
        private int _descriptor;

        public OwnedDescriptor(int descriptor)
        {
            _descriptor = descriptor;
        }

        public int Descriptor => _descriptor;

        public int Release()
        {
            return Interlocked.Exchange(ref _descriptor, value: -1);
        }

        public void Dispose()
        {
            var descriptor = Interlocked.Exchange(ref _descriptor, value: -1);
            if (descriptor >= 0)
            {
                _ = close(descriptor);
            }
        }

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern int close(int fileDescriptor);
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    // glibc exposes no openat2 wrapper, so the raw syscall entry point is used. The variadic declaration is safe to
    // pin to this arity on both supported architectures: integer arguments are passed in registers either way.
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long syscall(long number, int directoryDescriptor, byte[] pathname, byte[] how, ulong size);

    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint geteuid();
}
