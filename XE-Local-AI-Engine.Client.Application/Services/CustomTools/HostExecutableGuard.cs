namespace XE_Local_AI_Engine.Client.Services.CustomTools;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
///     Execution-time validation of a command tool's executable (H5), run every time the tool fires — not just at
///     author time — because a path that was a regular file when the operator saved it can be swapped for a symlink
///     later. The executable must be an absolute path (never a PATH/CWD lookup — Windows searches the CWD), must not be
///     a shell/interpreter, and must be a real regular file. On Linux the regular-file check uses <c>statx</c> with
///     <c>AT_SYMLINK_NOFOLLOW</c> so a symlinked executable is rejected rather than followed — the same libc-import
///     posture as the sandbox provider's no-follow guards (a raw <c>FileOptions</c> cast for the flag throws).
/// </summary>
internal static class HostExecutableGuard
{
    // statx(2). AT_FDCWD resolves an absolute path; AT_SYMLINK_NOFOLLOW makes the stat describe the leaf link itself
    // (mode S_IFLNK) rather than its target, so a symlinked executable is detectable and rejected.
    private const int AtFileDescriptorCurrentWorkingDirectory = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxTypeMask = 0x1; // STATX_TYPE — request the file-type bits of stx_mode.
    private const int StatxBufferBytes = 256;
    private const int StatxModeOffset = 28; // byte offset of stx_mode (u16) in struct statx (fixed UAPI layout).
    private const int FileTypeMask = 0xF000; // S_IFMT
    private const int RegularFile = 0x8000; // S_IFREG

    /// <summary>
    ///     Throws <see cref="CustomToolExecutionException" /> unless <paramref name="executablePath" /> is an absolute,
    ///     non-interpreter, real regular file. Returns normally when the executable is safe to launch.
    /// </summary>
    public static void Validate(string executablePath)
    {
        if (!CustomToolValidation.IsAbsolutePath(executablePath))
        {
            throw new CustomToolExecutionException("A command tool's executable must be an absolute path.");
        }

        if (CustomToolValidation.IsInterpreterOrShell(executablePath))
        {
            throw new CustomToolExecutionException("A command tool's executable must not be a shell or interpreter.");
        }

        if (OperatingSystem.IsLinux())
        {
            ValidateRegularFileNoFollowLinux(executablePath);
            return;
        }

        // Non-Linux (Windows): reject a missing file, a directory, or a reparse point (symlink/junction).
        if (!File.Exists(executablePath))
        {
            throw new CustomToolExecutionException("A command tool's executable must be an existing regular file.");
        }

        var attributes = File.GetAttributes(executablePath);
        if (attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new CustomToolExecutionException("A command tool's executable must not be a directory or a reparse point.");
        }
    }

    private static void ValidateRegularFileNoFollowLinux(string executablePath)
    {
        var pathBytes = new byte[Encoding.UTF8.GetByteCount(executablePath) + 1];
        Encoding.UTF8.GetBytes(executablePath, pathBytes);
        var buffer = new byte[StatxBufferBytes];

        var result = statx(AtFileDescriptorCurrentWorkingDirectory, pathBytes, AtSymlinkNoFollow, StatxTypeMask, buffer);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new CustomToolExecutionException(string.Create(CultureInfo.InvariantCulture,
                $"The command tool's executable could not be stat'd (it may be missing; errno {error})."));
        }

        var mode = BitConverter.ToUInt16(buffer, StatxModeOffset);
        if ((mode & FileTypeMask) != RegularFile)
        {
            throw new CustomToolExecutionException("A command tool's executable must be a regular file (not a symlink, directory, or device).");
        }
    }

    // DllImport (not source-generated LibraryImport) keeps this project free of AllowUnsafeBlocks, matching the libc
    // open()/statx() imports in ProcessSandboxRuntimeProvider and DockerWorkspaceHostFiles.
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int statx(int directoryFileDescriptor, byte[] pathname, int flags, uint mask, byte[] buffer);
}
