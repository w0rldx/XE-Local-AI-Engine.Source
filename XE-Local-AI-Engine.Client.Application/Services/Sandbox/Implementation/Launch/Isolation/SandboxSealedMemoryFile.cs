namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

/// <summary>
///     An anonymous in-memory file holding one synthetic <c>/etc</c> entry, sealed against modification and handed to
///     <c>bwrap</c> as a descriptor for <c>--ro-bind-data</c>.
///     <para>
///         Why not a real file: the four entries the jail needs (<c>passwd</c>, <c>group</c>, <c>nsswitch.conf</c>,
///         <c>hosts</c>) must contain what THIS layer decided they contain, and a file on disk is a place where
///         something else can intervene between writing it and <c>bwrap</c> reading it. A <c>memfd</c> has no name in
///         any filesystem, so nothing can open it; sealing it with <c>F_SEAL_WRITE</c> and friends means the engine
///         itself cannot change it afterwards either, so the bytes <c>bwrap</c> reads are provably the bytes this
///         layer wrote.
///     </para>
///     <para>
///         Like the bind descriptors it is deliberately NOT close-on-exec: <c>bwrap</c> is three execs away.
///     </para>
/// </summary>
internal sealed class SandboxSealedMemoryFile : IDisposable
{
    // memfd_create(2) / fcntl(2) sealing.
    private const uint AllowSealing = 0x0002;
    private const int AddSeals = 1033;
    private const int SealSeal = 0x0001;
    private const int SealShrink = 0x0002;
    private const int SealGrow = 0x0004;
    private const int SealWrite = 0x0008;

    private int _fileDescriptor;

    private SandboxSealedMemoryFile(int fileDescriptor)
    {
        _fileDescriptor = fileDescriptor;
    }

    /// <summary>The raw descriptor number, as the <c>--ro-bind-data</c> argument spells it.</summary>
    public int FileDescriptor => _fileDescriptor;

    /// <summary>
    ///     Creates a sealed memory file carrying <paramref name="content" />. Throws
    ///     <see cref="SandboxIsolationUnavailableException" /> when the host cannot provide one — which, like every
    ///     other ingredient, means the capability is absent rather than that a weaker jail is built.
    /// </summary>
    public static SandboxSealedMemoryFile Create(string name, byte[] content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(content);

        if (!OperatingSystem.IsLinux())
        {
            throw new SandboxIsolationUnavailableException("the filesystem boundary is Linux-only");
        }

        // Without MFD_CLOEXEC on purpose: the descriptor has to survive setsid → systemd-run → bwrap.
        var descriptor = memfd_create(name, AllowSealing);
        if (descriptor < 0)
        {
            throw new SandboxIsolationUnavailableException($"a sealed memory file for /etc/{name} could not be created (errno {Marshal.GetLastPInvokeError()})");
        }

        var file = new SandboxSealedMemoryFile(descriptor);
        try
        {
            // ownsHandle:false — the descriptor's lifetime belongs to this object, not to the handle wrapper. Writing
            // at an explicit offset leaves the file position at zero, which is where bwrap will start reading.
            using (var handle = new SafeFileHandle((nint)descriptor, ownsHandle: false))
            {
                RandomAccess.Write(handle, content, fileOffset: 0);
            }

            // Sealed AFTER the write and BEFORE anything else can see the descriptor. F_SEAL_SEAL closes the door on
            // adding or removing seals later, so this is final.
            if (fcntl(descriptor, AddSeals, SealWrite | SealShrink | SealGrow | SealSeal) != 0)
            {
                throw new SandboxIsolationUnavailableException($"the synthetic /etc/{name} could not be sealed against modification (errno {Marshal.GetLastPInvokeError()})");
            }

            return file;
        }
        catch (Exception exception) when (exception is not SandboxIsolationUnavailableException)
        {
            file.Dispose();
            throw new SandboxIsolationUnavailableException($"the synthetic /etc/{name} could not be written", exception);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Returns the file position to zero. <c>bwrap</c> reads the descriptor to EOF and the position is shared with
    ///     every process that inherited it, so a descriptor that has already been read once would otherwise deliver an
    ///     empty file. Each launch builds its own memory files, so this is belt-and-braces rather than load-bearing —
    ///     but an empty <c>/etc/passwd</c> is a silent, confusing failure and it costs one syscall to make impossible.
    /// </summary>
    public void RewindForLaunch()
    {
        if (_fileDescriptor >= 0)
        {
            _ = lseek(_fileDescriptor, offset: 0, whence: 0);
        }
    }

    public void Dispose()
    {
        var descriptor = Interlocked.Exchange(ref _fileDescriptor, value: -1);
        if (descriptor >= 0)
        {
            _ = close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "memfd_create", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int memfd_create(string name, uint flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int fcntl(int fileDescriptor, int command, int argument);

    [DllImport("libc", EntryPoint = "lseek", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern long lseek(int fileDescriptor, long offset, int whence);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fileDescriptor);
}
