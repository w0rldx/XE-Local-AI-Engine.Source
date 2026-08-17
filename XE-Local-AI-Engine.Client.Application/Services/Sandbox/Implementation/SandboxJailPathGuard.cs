namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

/// <summary>
///     The process sandbox's path-safety guards, gathered so the security invariant is one file to audit: the lexical
///     jail check (<see cref="ResolveJailPath" />) and the per-component symlink check
///     (<see cref="EnsureNoSymlinkComponentsUnderJail" />) are mandatory <b>together</b> on every read/write leg —
///     canonicalization collapses <c>..</c> but does not resolve a symlink a sandboxed command planted inside the jail
///     — and every open that follows them goes through a no-follow <c>open(2)</c>.
///     <para>
///         Callers hold the jail root; this type holds no state, so a new sandbox surface reaches the whole guard pair
///         by calling into here rather than by re-deriving half of it.
///     </para>
/// </summary>
internal static class SandboxJailPathGuard
{
    // O_RDONLY (0x0) | O_NOFOLLOW (0x20000) | O_CLOEXEC (0x80000) on Linux — the same flag set the deleted container
    // provider used. A raw (FileOptions) cast for O_NOFOLLOW throws, so the libc open() DllImport below is required
    // (parity with AgentHome's host-file no-follow guard).
    private const int ReadOnlyNoFollowCloseOnExecFlags = 0x0 | 0x20000 | 0x80000;

    // O_WRONLY (0x1) | O_CREAT (0x40) | O_TRUNC (0x200) | O_NOFOLLOW (0x20000) | O_CLOEXEC (0x80000) on Linux. A
    // no-follow create fails with ELOOP if the leaf already exists as a symlink, so the copy-into write cannot be
    // redirected through a planted leaf symlink. 0o644 mode for the created file.
    private const int WriteCreateNoFollowCloseOnExecFlags = 0x1 | 0x40 | 0x200 | 0x20000 | 0x80000;
    private const int DefaultCreateFileMode = 0b110_100_100;

    /// <summary>
    ///     Canonicalizes a (possibly sandbox-absolute) path into a host path that MUST live under the jail root. Any
    ///     path that escapes the jail — via <c>..</c> traversal or an absolute path outside it — is rejected lexically
    ///     (<see cref="Path.GetFullPath(string)" /> collapses <c>..</c>). This is the load-bearing jail control. It does
    ///     NOT resolve symlinks; a path under the jail can still TRAVERSE a planted symlink (a command running with the
    ///     jail as CWD can create one). The caller must additionally pass the canonical path through
    ///     <see cref="EnsureNoSymlinkComponentsUnderJail" /> (read/write legs) before opening to close that escape.
    /// </summary>
    internal static string ResolveJailPath(string jailRoot, string sandboxPath)
    {
        // AgentHome addresses files with sandbox-absolute paths (e.g. /agent-home/workspace/...). Treat a leading
        // separator as jail-relative so an absolute sandbox path maps under the jail rather than at the host root.
        var relative = sandboxPath.TrimStart('/', '\\');
        var combined = Path.Combine(jailRoot, relative);
        var canonical = Path.GetFullPath(combined);

        if (!IsUnderJailRoot(jailRoot, canonical))
        {
            throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the jail and is rejected.");
        }

        return canonical;
    }

    private static bool IsUnderJailRoot(string jailRoot, string canonicalPath)
    {
        var jailPrefix = jailRoot.EndsWith(Path.DirectorySeparatorChar)
            ? jailRoot
            : jailRoot + Path.DirectorySeparatorChar;

        return string.Equals(canonicalPath, jailRoot, StringComparison.Ordinal)
               || canonicalPath.StartsWith(jailPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Rejects a jail-relative path whose final component, or ANY component between the jail root and the leaf, is a
    ///     symlink (reparse point). A command running with the jail as its working directory can legitimately plant a
    ///     symlink inside the jail (e.g. <c>workspace/x -&gt; /etc</c>); the lexical <see cref="ResolveJailPath" /> check
    ///     passes such a path, but opening through it would read/write OUTSIDE the jail. Walking each component with a
    ///     no-follow probe (<see cref="File.ResolveLinkTarget(string, bool)" /> returns non-null for a symlink) closes
    ///     that escape for both the read legs and the copy-into write. <paramref name="canonicalPath" /> must already be
    ///     proven under the jail by <see cref="ResolveJailPath" />. Only existing components are probed (a not-yet-created
    ///     copy-into leaf cannot be a symlink). Throws <see cref="UnauthorizedAccessException" /> on the first symlink
    ///     component — a swap/plant-after-resolve escape signal.
    /// </summary>
    internal static void EnsureNoSymlinkComponentsUnderJail(string jailRoot, string canonicalPath, string sandboxPath)
    {
        // Walk from the leaf upward; stop at the jail root (the jail root itself is trusted — it is created by this
        // provider, not by a sandboxed command).
        var jailRootFull = Path.GetFullPath(jailRoot);
        var current = canonicalPath;
        while (!string.Equals(current, jailRootFull, StringComparison.Ordinal))
        {
            // A component above the jail root means the path escaped (defense in depth; ResolveJailPath already
            // rejected escapes, but never walk past the trusted boundary).
            if (!IsUnderJailRoot(jailRootFull, current))
            {
                throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' escapes the jail and is rejected.");
            }

            // Probe only existing components. A symlink (file or directory) returns a non-null link target under a
            // no-follow resolve; a real file/dir or a not-yet-created leaf returns null.
            if ((File.Exists(current) || Directory.Exists(current))
                && File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
            {
                throw new UnauthorizedAccessException($"Sandbox path '{sandboxPath}' traverses or targets a symlink inside the jail and is rejected.");
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }

    /// <summary>
    ///     Rejects a canonical HOST path (outside any jail) that traverses a symbolic link. Used for the trusted host
    ///     workspace, whose components are not covered by the jail-relative walk above.
    /// </summary>
    internal static void EnsureNoSymbolicLinkComponents(string canonicalPath)
    {
        var root = Path.GetPathRoot(canonicalPath)
                   ?? throw new UnauthorizedAccessException("The trusted host workspace must have a rooted canonical path.");
        var current = root;
        foreach (var segment in canonicalPath[root.Length..].Split(Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.ResolveLinkTarget(current, returnFinalTarget: false) is not null)
            {
                throw new UnauthorizedAccessException("A trusted host workspace path cannot contain symbolic links.");
            }
        }
    }

    // ---- ported host-file no-follow / byte-cap guard (same pattern as the deleted LocalContainerSandboxProvider) ----

    /// <summary>
    ///     Reads the host file under the no-follow / byte-recheck guards. Throws <see cref="InvalidDataException" />
    ///     when the file exceeds the per-file cap on this re-read or grew after sizing. Throws
    ///     <see cref="UnauthorizedAccessException" /> when the final path component is a symlink or the open cannot be
    ///     performed safely — a swap-after-walk attack signal.
    /// </summary>
    internal static byte[] ReadHostFileUnderGuard(string sourcePath, long maxCopyFileBytes)
    {
        var fileHandle = OpenNoFollow(sourcePath);

        using (fileHandle)
        {
            var length = RandomAccess.GetLength(fileHandle);
            if (length > maxCopyFileBytes)
            {
                throw new InvalidDataException("The copy source exceeds the configured per-file byte limit.");
            }

            var content = new byte[length];
            var read = 0;
            while (read < content.Length)
            {
                var chunk = RandomAccess.Read(fileHandle, content.AsSpan(read), read);
                if (chunk == 0)
                {
                    // The file shrank after the length read; copy only what is actually present.
                    return content[..read];
                }

                read += chunk;
            }

            // Growth-after-sizing check: a single probe byte past the sized length means the file grew between the
            // length read and the copy. Block (null) rather than silently truncate to the stale size.
            Span<byte> probe = stackalloc byte[1];
            if (RandomAccess.Read(fileHandle, probe, length) > 0)
            {
                throw new InvalidDataException("The copy source grew while it was being read.");
            }

            return content;
        }
    }

    /// <summary>
    ///     Opens the host file refusing a symlink at the final component. On Linux this is an atomic <c>open(2)</c> with
    ///     <c>O_NOFOLLOW</c> (the kernel fails with <c>ELOOP</c> if the leaf is a symlink), closing the check-then-open
    ///     race a managed <c>lstat</c> + open would leave. A raw <c>(FileOptions)</c> cast for <c>O_NOFOLLOW</c> throws,
    ///     so the libc <c>open()</c> DllImport is required (the same guard AgentHome uses). On a
    ///     non-Linux host this provider is not the primary runtime, so it falls back to a plain open and relies on the
    ///     canonicalized jail check plus the byte re-check. Throws <see cref="UnauthorizedAccessException" /> when the
    ///     leaf is a symlink or the open otherwise fails.
    /// </summary>
    private static SafeFileHandle OpenNoFollow(string sourcePath)
    {
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                return File.OpenHandle(sourcePath);
            }
            catch (IOException exception)
            {
                throw new UnauthorizedAccessException("a selected file could not be opened safely for copy.", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                throw new UnauthorizedAccessException("a selected file could not be opened safely for copy (access denied).", exception);
            }
        }

        // Null-terminate the UTF-8 path for libc.
        var pathBytes = new byte[Encoding.UTF8.GetByteCount(sourcePath) + 1];
        Encoding.UTF8.GetBytes(sourcePath, pathBytes);
        var fileDescriptor = open(pathBytes, ReadOnlyNoFollowCloseOnExecFlags);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"a selected file could not be opened safely for copy (it may have been replaced by a link; errno {error})."));
        }

        return new SafeFileHandle(fileDescriptor, ownsHandle: true);
    }

    // A single libc open(). The path is marshalled by the caller into a null-terminated UTF-8 byte array so any
    // filename round-trips correctly; the import takes the raw bytes. DllImport (not source-generated LibraryImport)
    // keeps the project free of AllowUnsafeBlocks — the source generator buys nothing for one call.
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags);

    // The 3-arg open() used for O_CREAT (the mode is honored only when the file is created).
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int open(byte[] pathname, int flags, int mode);

    /// <summary>
    ///     Reads a jail-side file's raw bytes through a no-follow open. On Linux the open is atomic with
    ///     <c>O_NOFOLLOW</c> so a leaf swapped to a symlink after the per-component check fails the open instead of
    ///     redirecting the read. On a non-Linux host (not the primary runtime) it falls back to a plain handle and
    ///     relies on the per-component symlink check plus the jail canonicalization. Throws
    ///     <see cref="UnauthorizedAccessException" /> when the leaf is a symlink or the open otherwise fails.
    /// </summary>
    internal static async Task<byte[]> ReadJailFileBytesNoFollowAsync(string jailPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var handle = OpenNoFollow(jailPath);
        var length = RandomAccess.GetLength(handle);
        if (length > maxBytes)
        {
            throw new InvalidDataException("The sandbox file exceeds the requested read bound.");
        }

        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await RandomAccess.ReadAsync(handle, buffer.AsMemory(read), read, cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
            {
                return buffer[..read];
            }

            read += chunk;
        }

        Memory<byte> probe = new byte[1];
        if (await RandomAccess.ReadAsync(handle, probe, length, cancellationToken).ConfigureAwait(false) > 0)
        {
            throw new InvalidDataException("The sandbox file grew while it was read under the requested bound.");
        }

        return buffer;
    }

    /// <summary>
    ///     Writes bytes to a jail-side path through a no-follow create. On Linux <c>O_NOFOLLOW</c> makes the create fail
    ///     (ELOOP) if the leaf already exists as a symlink, so a planted leaf symlink cannot redirect the copy-into write
    ///     outside the jail. On a non-Linux host it falls back to a plain create (the per-component symlink check still
    ///     guards intermediate components). Throws <see cref="UnauthorizedAccessException" /> when the leaf is a symlink
    ///     or the create otherwise fails.
    /// </summary>
    internal static async Task WriteJailFileNoFollowAsync(string jailPath, byte[] content, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            // Non-Linux fallback: the per-component symlink check already ran; a final-component existing symlink would
            // have been rejected there. Plain write.
            await File.WriteAllBytesAsync(jailPath, content, cancellationToken).ConfigureAwait(false);
            return;
        }

        var pathBytes = new byte[Encoding.UTF8.GetByteCount(jailPath) + 1];
        Encoding.UTF8.GetBytes(jailPath, pathBytes);
        var fileDescriptor = open(pathBytes, WriteCreateNoFollowCloseOnExecFlags, DefaultCreateFileMode);
        if (fileDescriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new UnauthorizedAccessException(string.Create(CultureInfo.InvariantCulture,
                $"the copy-into destination could not be created safely (it may be a symlink; errno {error})."));
        }

        using var handle = new SafeFileHandle(fileDescriptor, ownsHandle: true);
        await RandomAccess.WriteAsync(handle, content, fileOffset: 0, cancellationToken).ConfigureAwait(false);
    }
}
