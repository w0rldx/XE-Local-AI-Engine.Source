namespace XE_Local_AI_Engine.Providers.Abstractions;

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
///     The handle-side half of <see cref="PathContainment" />: where an OPENED file really is, as the kernel names the handle.
/// </summary>
/// <remarks>
///     A pre-open component walk leaves a race: a directory swapped for a link between the walk and the open is followed by the open.
///     The handle's own name cannot be redirected that way, so checking it after the open closes the race. Linux reads the name from
///     <c>/proc/self/fd</c>, Windows from <c>GetFinalPathNameByHandle</c>; other platforms have no check here and keep the caller's pre-open
///     walk. The trusted root is compared as the same mechanism spells it (<see cref="ResolveExistingOrNull" />), so a root behind a
///     symlink, a junction or an 8.3 short name still matches.
/// </remarks>
public static class OpenedHandleContainment
{
    // O_PATH (010000000) | O_CLOEXEC (02000000), the asm-generic values x64 and arm64 share: names the inode without read access,
    // works on directories, and follows links, which is what resolving a trusted root needs.
    private const int PathOnlyCloseOnExecFlags = 0x200000 | 0x80000;

    private const uint FileFlagBackupSemantics = 0x02000000;

    // VOLUME_NAME_DOS | FILE_NAME_NORMALIZED: a drive-letter (or \\?\UNC\) path with every link and short name expanded.
    private const uint FinalPathNormalizedDos = 0;

    private const int MaximumWindowsPathLength = 32_768;

    /// <summary>
    ///     Throws <see cref="UnauthorizedAccessException" /> when the file behind <paramref name="handle" /> is neither
    ///     <paramref name="trustedRoot" /> nor under it, or its name cannot be read. A no-op off Linux and Windows.
    /// </summary>
    /// <param name="trustedRoot">The root as <see cref="ResolveExistingOrNull" /> returned it.</param>
    public static void EnsureWithinRoot(SafeFileHandle handle, string trustedRoot)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(trustedRoot);

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return;
        }

        if (FinalPathOrNull(handle) is not { } openedPath
            || !PathContainment.IsRootOrUnderCanonical(Path.TrimEndingDirectorySeparator(Path.GetFullPath(openedPath)), trustedRoot))
        {
            throw new UnauthorizedAccessException("An opened file resolved outside its trusted root.");
        }
    }

    /// <summary>
    ///     The canonical path of an existing file or directory, read from a handle opened on it (links followed, trailing separator
    ///     trimmed), or <see langword="null" /> when it cannot be opened. Off Linux and Windows, the lexical full path.
    /// </summary>
    public static string? ResolveExistingOrNull(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (OperatingSystem.IsLinux())
        {
            var pathBytes = new byte[Encoding.UTF8.GetByteCount(path) + 1];
            Encoding.UTF8.GetBytes(path, pathBytes);
            var descriptor = open(pathBytes, PathOnlyCloseOnExecFlags);
            if (descriptor < 0)
            {
                return null;
            }

            using var linuxHandle = new SafeFileHandle(descriptor, ownsHandle: true);
            return TrimmedOrNull(FinalPathOrNull(linuxHandle));
        }

        if (OperatingSystem.IsWindows())
        {
            // Backup semantics is what lets CreateFile open a directory; no access right is needed to name the object.
            using var windowsHandle = CreateFile(path,
                desiredAccess: 0,
                FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero,
                FileMode.Open,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            return windowsHandle.IsInvalid ? null : TrimmedOrNull(FinalPathOrNull(windowsHandle));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>
    ///     The kernel's name for <paramref name="handle" />: on Windows without the <c>\\?\</c> prefix and with a UNC path as
    ///     <c>\\server\share</c>. <see langword="null" /> when it cannot be read, and off Linux and Windows.
    /// </summary>
    [SuppressMessage("Sonar Code Smell", "S3869:SafeHandle instances should not use DangerousGetHandle",
        Justification =
            "Linux exposes an opened file's canonical target through /proc/self/fd/{fd}; the owning SafeFileHandle remains live for this synchronous lookup and is never released or transferred here.")]
    private static string? FinalPathOrNull(SafeFileHandle handle)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var descriptorPath = string.Create(CultureInfo.InvariantCulture, $"/proc/self/fd/{handle.DangerousGetHandle().ToInt64()}");
                return new FileInfo(descriptorPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[MaximumWindowsPathLength];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, FinalPathNormalizedDos);
        if (length == 0 || length >= (uint)buffer.Length)
        {
            return null;
        }

        const string devicePrefix = @"\\?\";
        const string uncPrefix = @"\\?\UNC\";
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(@"\\", path[uncPrefix.Length..]);
        }

        return path.StartsWith(devicePrefix, StringComparison.Ordinal) ? path[devicePrefix.Length..] : path;
    }

    private static string? TrimmedOrNull(string? path) =>
        path is null ? null : Path.TrimEndingDirectorySeparator(path);

    // A single libc open(). The caller marshals the path into a null-terminated UTF-8 byte array so any filename round-trips.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFile(string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathSize,
        uint flags);
}
