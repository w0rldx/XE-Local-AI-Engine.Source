namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
///     The owner, group and mode of one filesystem object, read through <c>statx(2)</c>. Everything the isolation
///     layer decides about trust — is this binary root-owned, is this directory writable by anyone but us, is this
///     component a symlink — is decided from these three numbers, so they are read once and passed around as a value
///     rather than re-statted per question.
/// </summary>
[StructLayout(LayoutKind.Auto)]
internal readonly record struct SandboxUnixFileFacts(uint UserId, uint GroupId, uint Mode)
{
    // S_IFMT and the file-type constants. Spelled out rather than taken from a framework enum because
    // UnixFileMode carries only the permission bits and cannot answer "is this a symlink".
    private const uint FileTypeMask = 0xF000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;
    private const uint SymbolicLinkType = 0xA000;

    private const uint StickyBit = 0x200;
    private const uint GroupAndWorldWrite = 0b000_010_010;
    private const uint AnyExecute = 0b001_001_001;

    public bool IsDirectory => (Mode & FileTypeMask) == DirectoryType;

    public bool IsRegularFile => (Mode & FileTypeMask) == RegularFileType;

    public bool IsSymbolicLink => (Mode & FileTypeMask) == SymbolicLinkType;

    /// <summary>
    ///     <see langword="true" /> for a directory carrying the sticky bit — the property that makes a world-writable
    ///     shared directory such as <c>/tmp</c> safe to have as an ancestor: entries in it can only be renamed or
    ///     removed by their own owner.
    /// </summary>
    public bool IsStickyDirectory => IsDirectory && (Mode & StickyBit) != 0;

    public bool IsGroupOrWorldWritable => (Mode & GroupAndWorldWrite) != 0;

    public bool HasAnyExecuteBit => (Mode & AnyExecute) != 0;

    /// <summary>The permission bits alone (the low twelve, so setuid/setgid/sticky are included).</summary>
    public uint PermissionBits => Mode & 0xFFF;
}

/// <summary>
///     Reads <see cref="SandboxUnixFileFacts" /> for a path or for an already-open file descriptor.
///     <para>
///         <c>statx(2)</c> rather than <c>stat(2)</c> deliberately, for the reason recorded in
///         <c>DockerWorkspaceHostFiles</c>: <c>struct statx</c> is a kernel UAPI structure with a byte layout that is
///         identical on every architecture and every glibc vintage, so the fields can be read out of a raw buffer at
///         fixed offsets. A <c>struct stat</c> binding would have to know both.
///     </para>
///     <para>
///         The descriptor overload is what makes the trust checks TOCTOU-safe: the isolation layer opens a path
///         component and then asks about the object it actually opened, so a component swapped between the check and
///         the use cannot change the answer.
///     </para>
/// </summary>
internal static class SandboxUnixMetadata
{
    // AT_FDCWD; every path passed here is absolute, so it only ever means "no directory descriptor".
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;

    // AT_EMPTY_PATH: with an empty pathname, statx describes the object the descriptor itself refers to. This works on
    // an O_PATH descriptor, which is what the isolation layer holds.
    private const int AtEmptyPath = 0x1000;

    // STATX_TYPE | STATX_MODE | STATX_UID | STATX_GID.
    private const uint OwnerAndModeMask = 0x1 | 0x2 | 0x8 | 0x10;

    // Byte offsets into `struct statx`: stx_uid at 20, stx_gid at 24, stx_mode (a __u16) at 28.
    private const int UserIdOffset = 20;
    private const int GroupIdOffset = 24;
    private const int ModeOffset = 28;
    private const int BufferBytes = 256;

    private static readonly byte[] EmptyPath = [0];

    /// <summary>
    ///     Reads the facts for <paramref name="path" />, or <see langword="null" /> when the object does not exist or
    ///     the platform cannot answer. <paramref name="followSymbolicLinks" /> is <see langword="false" /> by default
    ///     because every caller here is asking about the component itself, not about what it points at.
    /// </summary>
    public static SandboxUnixFileFacts? TryRead(string path, bool followSymbolicLinks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var pathBytes = new byte[Encoding.UTF8.GetByteCount(path) + 1];
        Encoding.UTF8.GetBytes(path, pathBytes);
        var flags = followSymbolicLinks ? 0 : AtSymlinkNoFollow;

        return Read(AtFileDescriptorCurrentWorkingDirectory, pathBytes, flags);
    }

    /// <summary>
    ///     Reads the facts for the object an open descriptor refers to. The descriptor may be <c>O_PATH</c>.
    /// </summary>
    public static SandboxUnixFileFacts? TryReadDescriptor(int fileDescriptor)
    {
        if (!OperatingSystem.IsLinux() || fileDescriptor < 0)
        {
            return null;
        }

        return Read(fileDescriptor, EmptyPath, AtEmptyPath | AtSymlinkNoFollow);
    }

    private static SandboxUnixFileFacts? Read(int directoryFileDescriptor, byte[] pathBytes, int flags)
    {
        var buffer = new byte[BufferBytes];
        if (statx(directoryFileDescriptor, pathBytes, flags, OwnerAndModeMask, buffer) != 0)
        {
            return null;
        }

        return new SandboxUnixFileFacts(BitConverter.ToUInt32(buffer, UserIdOffset),
            BitConverter.ToUInt32(buffer, GroupIdOffset),
            BitConverter.ToUInt16(buffer, ModeOffset));
    }

    // DllImport rather than the source-generated LibraryImport, matching every other libc binding in this provider:
    // the generated form needs AllowUnsafeBlocks on the whole project and buys nothing for one call.
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int statx(int directoryFileDescriptor, byte[] pathname, int flags, uint mask, byte[] buffer);
}
