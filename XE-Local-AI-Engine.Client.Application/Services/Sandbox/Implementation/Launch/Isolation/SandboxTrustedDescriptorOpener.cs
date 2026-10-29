namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
///     Opens <see cref="SandboxTrustedDescriptor" />s through an <c>openat2(2)</c> walk that refuses to traverse a symlink or leave the
///     directory it started from, validating every component as the FD it just opened rather than a path it might re-resolve.
/// </summary>
/// <remarks>
///     Ancestors must be root-owned and non-writable by anyone else, a root-owned sticky directory such as <c>/tmp</c> counting because
///     entries in it can only be removed by their own owner; the target itself must belong to the engine's own user, and a jail anchor
///     must also be <c>0700</c>. The source of a read-only bind may instead be root-owned, under the same not-group-or-world-writable rule. That accepts the three shapes this engine uses — a jail under <c>/tmp</c>, a runtime root under the
///     user's home, a data directory under either — and rejects a tree anyone else on the host could have swapped.
/// </remarks>
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

    // RESOLVE_NO_SYMLINKS refuses the open if ANY component, the final one included, is a symlink; RESOLVE_BENEATH refuses anything that
    // would escape the starting descriptor. Together the walk cannot leave the tree it was pointed at.
    private const ulong ResolveNoSymlinks = 0x04;
    private const ulong ResolveBeneath = 0x08;

    private const uint PrivateDirectoryMode = 0b111_000_000;

    /// <summary>
    ///     Opens <paramref name="path" /> as an inheritable <c>O_PATH</c> directory descriptor.
    ///     <paramref name="requirePrivateMode" /> additionally demands <c>0700</c>, which is what a writable jail must
    ///     be. Throws <see cref="SandboxIsolationUnavailableException" /> with a measured reason otherwise.
    /// </summary>
    public static SandboxTrustedDescriptor Open(string path, bool requirePrivateMode) =>
        Open(path, requirePrivateMode, expectFile: false, readOnly: false);

    /// <summary>
    ///     Opens the source of a bind or read-only tree, a directory or (<paramref name="expectFile" />) a REGULAR FILE such as a read-only
    ///     <c>.git/config</c> or an empty credential shadow, under the same ancestor rule.
    /// </summary>
    /// <remarks>
    ///     A <paramref name="readOnly" /> source may also be root-owned: a toolchain outside <c>/usr</c> (<c>/opt/dotnet</c>,
    ///     <c>/snap/bin</c>, a system conda) that nobody else can write is as trustworthy as the <c>/usr</c> the chain already binds. A
    ///     writable source stays engine-owned.
    /// </remarks>
    public static SandboxTrustedDescriptor OpenBindSource(string path, bool expectFile, bool readOnly) =>
        Open(path, requirePrivateMode: false, expectFile, readOnly);

    private static SandboxTrustedDescriptor Open(string path, bool requirePrivateMode, bool expectFile, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsLinux())
        {
            throw new SandboxIsolationUnavailableException("the filesystem boundary is Linux-only");
        }

        if (!Path.IsPathRooted(path))
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
                var next = OpenBeneath(current.Descriptor, component, currentPath, closeOnExec: !isTarget, directory: !(isTarget && expectFile));
                current.Dispose();
                current = next;
                currentPath = string.Concat(currentPath, "/", component);

                var facts = SandboxUnixMetadata.ReadDescriptorOrNull(current.Descriptor)
                            ?? throw new SandboxIsolationUnavailableException($"'{currentPath}' could not be stat'ed after it was opened");

                if (isTarget)
                {
                    EnsureTargetIsTrustworthy(facts, currentPath, ownUserId, requirePrivateMode, expectFile, readOnly);
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

    /// <summary>The ancestor rule.</summary>
    /// <remarks>
    ///     Root-owned and non-writable is the ordinary case; a root-owned STICKY directory is accepted because that is what makes a shared
    ///     <c>/tmp</c> safe to sit under, an entry in one being renameable or removable only by its own owner. A directory owned by the
    ///     engine's own user is accepted when not group- or world-writable, which covers the user's home and the node data directory.
    /// </remarks>
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

    private static void EnsureTargetIsTrustworthy(SandboxUnixFileFacts facts, string path, uint ownUserId, bool requirePrivateMode, bool expectFile, bool readOnly)
    {
        if (expectFile ? !facts.IsRegularFile : !facts.IsDirectory)
        {
            throw new SandboxIsolationUnavailableException(expectFile ? $"'{path}' is not a regular file" : $"'{path}' is not a directory");
        }

        if (facts.UserId != ownUserId && !(readOnly && facts.UserId == 0))
        {
            throw new SandboxIsolationUnavailableException(readOnly
                ? $"'{path}' is owned by uid {facts.UserId}, which is neither root nor this engine"
                : $"'{path}' is owned by uid {facts.UserId} rather than by this engine");
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

    private static OwnedDescriptor OpenBeneath(int directoryDescriptor, string component, string parentPath, bool closeOnExec, bool directory)
    {
        var flags = OpenPath | (directory ? OpenDirectory : 0) | (closeOnExec ? OpenCloseOnExec : 0);
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
