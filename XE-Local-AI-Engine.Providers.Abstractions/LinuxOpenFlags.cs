namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Runtime.InteropServices;

/// <summary>The Linux <c>open(2)</c> flag values and the one libc <c>open</c> import every engine file open goes through.</summary>
/// <remarks>
///     <c>O_NOFOLLOW</c> and <c>O_DIRECTORY</c> differ by architecture: arm and arm64 override the asm-generic values x86-64 uses
///     (<c>arch/arm64/include/uapi/asm/fcntl.h</c>), so they follow <see cref="RuntimeInformation.ProcessArchitecture" />. The rest are
///     asm-generic. A failed open returns -1 and leaves its errno in <see cref="Marshal.GetLastPInvokeError" />.
/// </remarks>
public static partial class LinuxOpenFlags
{
    public const int ReadOnly = 0x0;
    public const int WriteOnly = 0x1;
    public const int Create = 0x40;
    public const int Truncate = 0x200;
    public const int NonBlocking = 0x800;
    public const int CloseOnExec = 0x80000;
    public const int PathOnly = 0x200000;

    // O_NOFOLLOW 0400000 and O_DIRECTORY 0200000 in asm-generic; arm and arm64 use 0100000 and 040000, where 0400000 is O_LARGEFILE
    // and 0200000 is O_DIRECT.
    private const int GenericNoFollow = 0x20000;
    private const int GenericDirectory = 0x10000;
    private const int ArmNoFollow = 0x8000;
    private const int ArmDirectory = 0x4000;

    /// <summary><c>O_NOFOLLOW</c> for this process's architecture.</summary>
    public static int NoFollow { get; } = NoFollowFor(RuntimeInformation.ProcessArchitecture);

    /// <summary><c>O_DIRECTORY</c> for this process's architecture.</summary>
    public static int Directory { get; } = DirectoryFor(RuntimeInformation.ProcessArchitecture);

    internal static int NoFollowFor(Architecture architecture) =>
        IsArm(architecture) ? ArmNoFollow : GenericNoFollow;

    internal static int DirectoryFor(Architecture architecture) =>
        IsArm(architecture) ? ArmDirectory : GenericDirectory;

    /// <summary>libc <c>open(path, flags)</c>; the path goes over as NUL-terminated UTF-8, so any file name round-trips.</summary>
    public static int Open(string path, int flags)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OpenNative(path, flags);
    }

    /// <summary>libc <c>open(path, flags, mode)</c> for a <see cref="Create" /> open; the mode applies only when the file is created.</summary>
    public static int Open(string path, int flags, int mode)
    {
        ArgumentNullException.ThrowIfNull(path);
        return OpenNative(path, flags, mode);
    }

    private static bool IsArm(Architecture architecture) =>
        architecture is Architecture.Arm64 or Architecture.Arm;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int OpenNative(string path, int flags, int mode);
}
