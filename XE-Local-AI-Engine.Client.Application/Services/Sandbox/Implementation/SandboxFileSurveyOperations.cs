namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;

using System.Text;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Workspace;

/// <summary>
///     The sanctioned file surface of the process jail — read, list, search, copy in and out, reset — each entered with a jail root the
///     caller resolved from a live sandbox handle.
/// </summary>
/// <remarks>
///     Every leg runs the same guard pair, <see cref="SandboxJailPathGuard.ResolveJailPath" /> plus
///     <see cref="SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail" />, before it opens anything, which is why the surveys are
///     provider operations rather than composed <c>find</c> and <c>grep</c> argument vectors: a new surface here cannot skip the
///     confinement, and none of it is POSIX-only. Secret exclusion is deliberately NOT this type's job — callers pass their own suppression
///     predicate and re-apply it, because which entries a feature may see is its policy and the jail's job is containment.
/// </remarks>
internal static class SandboxFileSurveyOperations
{
    public static async Task CopyIntoAsync(string jailRoot,
        SandboxCopyRequest request,
        long maxCopyFileBytes,
        CancellationToken cancellationToken)
    {
        var destination = SandboxJailPathGuard.ResolveJailPath(jailRoot, request.DestinationPath);

        // SECURITY (hard reject): a sandboxed command can plant a symlink inside the jail, so the destination's parent chain, and the
        // leaf if it exists, must hold none. Parent dirs are created first so they exist and are re-checked before the no-follow create.
        var parent = Path.GetDirectoryName(destination);
        if (parent is not null)
        {
            // Validate the existing prefix BEFORE Directory.CreateDirectory, which follows an intermediate symlink and could mutate an
            // outside directory before the later rejection. Re-check after creation for newly materialized components and a concurrent swap.
            SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, parent, request.DestinationPath);
            Directory.CreateDirectory(parent);
            SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, parent, request.DestinationPath);
        }

        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, destination, request.DestinationPath);

        // Re-open the host source under the no-follow and byte-cap-on-re-read guard: never trust a path string sized by an earlier walk.
        // A swap-to-symlink, over-cap file or growth-after-sizing throws, so preparation cannot report a snapshot of bytes never copied.
        var content = SandboxJailPathGuard.ReadHostFileUnderGuard(request.SourcePath, maxCopyFileBytes);

        // No-follow create on Linux: if the leaf was swapped for a symlink between the component check and the write,
        // O_NOFOLLOW makes the create fail rather than write through the link.
        await SandboxJailPathGuard.WriteJailFileNoFollowAsync(destination, content, cancellationToken);
    }

    public static void ResetDirectory(string jailRoot, string sandboxPath)
    {
        var resolved = SandboxJailPathGuard.ResolveJailPath(jailRoot, sandboxPath);
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, resolved, sandboxPath);

        if (Directory.Exists(resolved))
        {
            StandaloneGitClone.Delete(resolved);
        }
        else if (File.Exists(resolved))
        {
            throw new UnauthorizedAccessException("The requested sandbox directory is occupied by a file.");
        }

        Directory.CreateDirectory(resolved);
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, resolved, sandboxPath);
        if (Directory.EnumerateFileSystemEntries(resolved).Any())
        {
            throw new IOException("The sandbox directory could not be proven empty after reset.");
        }
    }

    public static async Task<string> ReadFileAsync(string jailRoot,
        string sandboxPath,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var resolved = SandboxJailPathGuard.ResolveJailPath(jailRoot, sandboxPath);

        // SECURITY: reject any symlink component (a sandboxed command can plant one), then read through a no-follow
        // open so a leaf swapped to a symlink after the component check cannot redirect the read outside the jail.
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, resolved, sandboxPath);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException($"Sandbox path '{sandboxPath}' was not found.", sandboxPath);
        }

        var bytes = await SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(resolved, maxBytes, cancellationToken);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Lists the jail's regular files.</summary>
    /// <remarks>
    ///     Resolves the directory through the SAME two controls a read goes through — <c>ResolveJailPath</c> for the lexical escape and
    ///     <c>EnsureNoSymlinkComponentsUnderJail</c> for a planted link — then walks it with <see cref="WorkspaceFileScanner" />, which
    ///     follows no link on the way down either. Doing it here rather than through a <c>find</c> shell-out makes the operation exist on a
    ///     host with no findutils, and moves the confinement from an argument vector the caller had to get right into the jail's owner.
    /// </remarks>
    public static IReadOnlyList<string> ListFiles(string jailRoot, SandboxListFilesRequest request, CancellationToken cancellationToken)
    {
        var root = ResolveSurveyDirectory(jailRoot, request.DirectoryPath);

        return WorkspaceFileScanner.ListFiles(root,
            request.MaxEntries,
            request.IsPathSuppressed ?? (static _ => false),
            request.NameGlob,
            cancellationToken);
    }

    public static IReadOnlyList<string> SearchText(string jailRoot, SandboxSearchTextRequest request, CancellationToken cancellationToken)
    {
        var root = ResolveSurveyDirectory(jailRoot, request.DirectoryPath);

        return WorkspaceFileScanner.SearchText(root,
            request.Pattern,
            request.IsRegex,
            request.MaxMatches,
            request.MaxOutputBytes,
            request.IsPathSuppressed ?? (static _ => false),
            cancellationToken);
    }

    public static async Task CopyOutAsync(string jailRoot, SandboxCopyRequest request, CancellationToken cancellationToken)
    {
        var source = SandboxJailPathGuard.ResolveJailPath(jailRoot, request.SourcePath);

        // SECURITY: reject any symlink component on the jail-side source, then read through a no-follow open so an
        // escaping symlink cannot copy a host file outside the jail out to the caller's destination.
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, source, request.SourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Sandbox path '{request.SourcePath}' was not found.", request.SourcePath);
        }

        // Read the raw bytes from inside the jail and write them to the host destination so a binary artifact survives
        // the round trip unchanged (parity with the container provider's copy-out).
        var content = await SandboxJailPathGuard.ReadJailFileBytesNoFollowAsync(source, int.MaxValue, cancellationToken);
        await File.WriteAllBytesAsync(request.DestinationPath, content, cancellationToken);
    }

    /// <summary>The survey's own confinement, identical to the read leg's.</summary>
    /// <remarks>
    ///     Kept in one place so the two surveys cannot drift from each other or from <see cref="ReadFileAsync" />. The handle-side checks —
    ///     live sandbox, non-blank path, cancellation — run in the provider before the jail root reaches here.
    /// </remarks>
    private static string ResolveSurveyDirectory(string jailRoot, string directoryPath)
    {
        var resolved = SandboxJailPathGuard.ResolveJailPath(jailRoot, directoryPath);
        SandboxJailPathGuard.EnsureNoSymlinkComponentsUnderJail(jailRoot, resolved, directoryPath);
        if (!Directory.Exists(resolved))
        {
            throw new DirectoryNotFoundException($"Sandbox path '{directoryPath}' was not found.");
        }

        return resolved;
    }
}
