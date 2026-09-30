namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     The one Git step Development Mode's template materialization and its managed workspace share: a
///     <em>standalone</em> clone, whose <c>.git</c> is a real directory with its own object store, so the source
///     repository is not reachable from the result.
/// </summary>
/// <remarks>
///     Deliberately not a "materialize a repository" abstraction. The two callers diverge immediately after this step and must keep
///     diverging: the template service discards <c>.git</c>, re-runs <c>init</c> and fabricates an initial commit, while the managed
///     workspace KEEPS the cloned history and detaches onto the recorded base commit. A fabricated initial commit would make a
///     workspace unappliable, because <c>TrustedDevelopmentHostApplyPort</c> requires the host repository's HEAD to equal the recorded
///     base sha at apply time. Shared here: the transport, the flag set and the standalone assertion, nothing else.
/// </remarks>
internal static class StandaloneGitClone
{
    /// <summary>
    ///     Builds the clone arguments. Run these from the destination's <em>parent</em> directory.
    /// </summary>
    /// <remarks>
    ///     The <c>file://</c> transport is mandatory, not stylistic: given a plain local path git ignores <c>--depth</c>, warns, and
    ///     hardlinks the entire object store — reproducing the shared-object coupling this helper exists to prevent, while still
    ///     reporting success. Measured: the plain-path form yields the source's full history, the <c>file://</c> form exactly one commit.
    /// </remarks>
    /// <param name="sourceRoot">Canonical absolute path of the repository being cloned.</param>
    /// <param name="destination">Absolute path the clone is created at. Must not already exist.</param>
    /// <param name="branch">
    ///     Branch to clone. When null the source's default branch is used, which is what template materialization wants
    ///     because it is about to discard the history anyway.
    /// </param>
    public static IReadOnlyList<string> Arguments(string sourceRoot, string destination, string? branch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        List<string> arguments = ["clone", "--depth", "1", "--no-recurse-submodules", "--no-tags"];
        if (branch is not null)
        {
            arguments.Add("--branch");
            arguments.Add(branch);
        }

        arguments.Add(new Uri(sourceRoot).AbsoluteUri);
        arguments.Add(destination);
        return arguments;
    }

    /// <summary>
    ///     True when the clone owns its Git directory outright: <c>.git</c> is a real directory rather than the
    ///     pointer <em>file</em> a worktree or submodule gets, and carries no <c>objects/info/alternates</c>.
    /// </summary>
    /// <remarks>
    ///     Both halves matter and neither implies the other: the pointer-file check is what a worktree fails, the alternates check
    ///     what a <c>--shared</c> or <c>--reference</c> clone would. A plain-local-path clone passes BOTH while still hardlinking the
    ///     whole history, so callers must keep the <c>file://</c> transport rather than treat this assertion as the guarantee.
    /// </remarks>
    public static bool IsStandalone(string destination)
    {
        var gitDirectory = Path.Combine(destination, ".git");
        return Directory.Exists(gitDirectory)
               && !File.Exists(Path.Combine(gitDirectory, "objects", "info", "alternates"));
    }

    /// <summary>
    ///     Best-effort removal of a directory this code created and then failed to finish populating.
    /// </summary>
    /// <remarks>
    ///     A half-created clone is worse than none: the next attempt would treat it as a preserved workspace and carry
    ///     whatever the failed clone left behind. The original failure is the one worth reporting, so a cleanup
    ///     failure is swallowed rather than masking it.
    /// </remarks>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    ///     Removes a directory tree Git produced, including the read-only files it leaves behind.
    /// </summary>
    /// <remarks>
    ///     <see cref="Directory.Delete(string, bool)" /> alone is NOT enough for a clone: Git marks <c>.git/objects/pack</c>'s
    ///     contents read-only because they are immutable once written. On Unix that is a mode and the parent directory's write
    ///     permission governs deletion, so a plain recursive delete succeeds; on Windows <c>FILE_ATTRIBUTE_READONLY</c> blocks the
    ///     delete and the walk fails part-way with an access denial, leaving a half-removed tree. So the attribute is cleared on the
    ///     way down. Hence the helper: every caller here deletes something Git wrote, so every caller hits it.
    /// </remarks>
    public static void Delete(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!Directory.Exists(path))
        {
            return;
        }

        ClearReadOnlyAttributes(new DirectoryInfo(path));
        Directory.Delete(path, recursive: true);
    }

    /// <summary>
    ///     Clears <see cref="FileAttributes.ReadOnly" /> from every file in the tree.
    /// </summary>
    /// <remarks>
    ///     Symbolic links are not followed: the attribute is cleared on the link itself, never on whatever it points
    ///     at, so a link planted inside the tree cannot strip the read-only bit off a file outside it.
    /// </remarks>
    private static void ClearReadOnlyAttributes(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                TryClearReadOnly(entry);
                continue;
            }

            if (entry is DirectoryInfo subdirectory)
            {
                ClearReadOnlyAttributes(subdirectory);
                continue;
            }

            TryClearReadOnly(entry);
        }
    }

    private static void TryClearReadOnly(FileSystemInfo entry)
    {
        try
        {
            if ((entry.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly)
            {
                entry.Attributes &= ~FileAttributes.ReadOnly;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The delete below reports the real failure; a clear that could not be applied is not itself the error.
        }
    }
}
