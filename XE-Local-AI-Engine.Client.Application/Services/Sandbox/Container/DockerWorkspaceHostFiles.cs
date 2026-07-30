namespace XE_Local_AI_Engine.Client.Services.Sandbox.Container;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
///     Host-side access to the bytes behind a container's workspace bind mount, under the same guards
///     <c>ProcessSandboxRuntimeProvider</c> applies to its jail.
///     <para>
///         Why the host and not the container: Docker refuses <c>PUT /containers/{id}/archive</c> outright against a
///         container with a read-only root filesystem (measured against Engine 29.6.1, which answers
///         <c>400 container rootfs is marked read-only</c> regardless of destination, including a writable
///         <c>tmpfs</c>), and §3.8 makes that root filesystem non-negotiable. The bind mount is the same bytes on
///         both sides, so writing them host-side is not a workaround for the restriction — it is the route that does
///         not need the archive endpoint at all.
///     </para>
///     <para>
///         The guards are not optional decoration. A command inside the container can plant a symlink in the
///         workspace, and the host — which is where these writes land — resolves it. So every component between the
///         workspace root and the leaf is probed for a symlink before the write, the parent chain is re-probed after
///         it is materialised, and on Linux the create itself is an <c>O_NOFOLLOW</c> <c>open(2)</c> so a leaf
///         swapped between the check and the write fails rather than redirecting.
///     </para>
/// </summary>
internal static class DockerWorkspaceHostFiles
{
    // O_WRONLY (0x1) | O_CREAT (0x40) | O_TRUNC (0x200) | O_NOFOLLOW (0x20000) | O_CLOEXEC (0x80000) on Linux.
    // A raw (FileOptions) cast for O_NOFOLLOW throws, so the libc open() below is required — the same flag set and
    // the same reasoning as ProcessSandboxRuntimeProvider's copy-into write.
    private const int WriteCreateNoFollowCloseOnExecFlags = 0x1 | 0x40 | 0x200 | 0x20000 | 0x80000;
    private const int DefaultCreateFileMode = 0b110_100_100;

    /// <summary>
    ///     Writes <paramref name="content" /> to the host bytes behind <paramref name="sandboxPath" />. Throws
    ///     <see cref="UnauthorizedAccessException" /> when the path escapes the workspace or traverses a symlink.
    /// </summary>
    internal static async Task WriteAsync(string workspaceRoot,
        string mountTarget,
        string sandboxPath,
        byte[] content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        var destination = DockerSandboxPaths.ResolveHostPath(canonicalRoot, mountTarget, sandboxPath);

        if (string.Equals(destination, canonicalRoot, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' names the workspace root, which is not a file.");
        }

        var parent = Path.GetDirectoryName(destination);
        if (parent is not null)
        {
            // Validate the existing prefix BEFORE Directory.CreateDirectory: that API follows an intermediate symlink,
            // so creating first could materialise directories outside the workspace before the later rejection. The
            // second pass covers every newly created component and a concurrent swap.
            EnsureNoSymlinkComponents(canonicalRoot, parent, sandboxPath);
            Directory.CreateDirectory(parent);
            EnsureNoSymlinkComponents(canonicalRoot, parent, sandboxPath);
        }

        EnsureNoSymlinkComponents(canonicalRoot, destination, sandboxPath);
        await WriteNoFollowAsync(destination, content, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Rejects a path whose leaf, or any component between the workspace root and it, is a symlink. The workspace
    ///     root itself is trusted: the engine created it. Only existing components are probed, because a not-yet-created
    ///     leaf cannot be a link — the <c>O_NOFOLLOW</c> create is what covers a leaf planted after this walk.
    /// </summary>
    internal static void EnsureNoSymlinkComponents(string canonicalRoot, string canonicalPath, string sandboxPath)
    {
        var current = canonicalPath;
        while (!string.Equals(current, canonicalRoot, StringComparison.Ordinal))
        {
            if (!DockerSandboxPaths.IsUnderHostRoot(canonicalRoot, current))
            {
                throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the workspace mount and is rejected.");
            }

            if ((File.Exists(current) || Directory.Exists(current))
                && File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
            {
                throw new UnauthorizedAccessException(
                    $"Sandbox path '{sandboxPath}' traverses or targets a symlink inside the workspace and is rejected.");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }

    private static async Task WriteNoFollowAsync(string hostPath, byte[] content, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            // Non-Linux fallback: the per-component walk above already rejected an existing leaf symlink, and the
            // engine host is Windows only in the D1 configuration where the daemon is remote anyway.
            await File.WriteAllBytesAsync(hostPath, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pathBytes = new byte[Encoding.UTF8.GetByteCount(hostPath) + 1];
        Encoding.UTF8.GetBytes(hostPath, pathBytes);
        var fileDescriptor = open(pathBytes, WriteCreateNoFollowCloseOnExecFlags, DefaultCreateFileMode);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"the workspace destination could not be created safely (it may be a symlink; errno {error})."));
        }

        using var handle = new SafeFileHandle(fileDescriptor, ownsHandle: true);
        await RandomAccess.WriteAsync(handle, content, fileOffset: 0, cancellationToken).ConfigureAwait(false);
    }

    // DllImport rather than the source-generated LibraryImport, matching ProcessSandboxRuntimeProvider: the generated
    // form requires AllowUnsafeBlocks on the whole project and buys nothing for two calls. The path is marshalled by
    // the caller into a null-terminated UTF-8 byte array so any filename round-trips correctly.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags, int mode);
}
