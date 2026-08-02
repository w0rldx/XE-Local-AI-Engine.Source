namespace XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;

/// <summary>
///     The one Git step Development Mode's template materialization (S2.1) and its managed workspace (S3.4 / decision
///     D8) genuinely share: producing a <em>standalone</em> clone of a host repository — one whose <c>.git</c> is a real
///     directory with its own object store, so the source repository is not reachable from the result.
///     <para>
///         Deliberately not a "materialize a repository" abstraction. The two callers diverge immediately after this
///         step and must keep diverging: the template service discards <c>.git</c>, re-runs <c>init</c> and fabricates
///         an initial commit, whereas the managed workspace <em>keeps</em> the cloned history and detaches onto the
///         recorded base commit. A workspace with a fabricated initial commit would be unappliable by construction,
///         because <c>TrustedDevelopmentHostApplyPort</c> requires the host repository's HEAD to equal the recorded base
///         sha at apply time. What is shared is the transport, the flag set and the standalone assertion — nothing else.
///     </para>
/// </summary>
internal static class StandaloneGitClone
{
    /// <summary>
    ///     Builds the clone arguments. Run these from the destination's <em>parent</em> directory.
    ///     <para>
    ///         The <c>file://</c> transport is mandatory, not stylistic. Given a plain local path git prints
    ///         <c>warning: --depth is ignored in local clones; use file:// instead</c> and then hardlinks the entire
    ///         object store — reproducing the shared-object coupling this helper exists to prevent, while still
    ///         reporting success. Measured on git 2.53.0: the plain-path form yields the source's full history, the
    ///         <c>file://</c> form yields exactly one commit.
    ///     </para>
    /// </summary>
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
    ///     True when the clone owns its Git directory outright: <c>.git</c> is a real directory rather than the pointer
    ///     <em>file</em> a linked worktree or a submodule gets, and it carries no <c>objects/info/alternates</c> naming
    ///     an object store it does not own.
    ///     <para>
    ///         Both halves matter and neither implies the other. The pointer-file check is what a worktree fails; the
    ///         alternates check is what a <c>--shared</c> or <c>--reference</c> clone would fail. A plain-local-path
    ///         clone passes <em>both</em> while still hardlinking the whole history, which is why callers must also keep
    ///         the <c>file://</c> transport rather than treating this assertion as the guarantee.
    ///     </para>
    /// </summary>
    public static bool IsStandalone(string destination)
    {
        var gitDirectory = Path.Combine(destination, ".git");
        return Directory.Exists(gitDirectory)
               && !File.Exists(Path.Combine(gitDirectory, "objects", "info", "alternates"));
    }

    /// <summary>
    ///     Best-effort removal of a directory this code created and then failed to finish populating. A half-created
    ///     clone is worse than none — it would be treated as a preserved workspace on the next attempt and carry
    ///     whatever the failed clone left behind — and the original failure is the one worth reporting, so a cleanup
    ///     failure is swallowed rather than masking it.
    /// </summary>
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
    ///     <para>
    ///         <see cref="Directory.Delete(string, bool)" /> alone is NOT enough for a clone. Git marks the contents of
    ///         <c>.git/objects/pack</c> — the <c>*.pack</c> and <c>*.idx</c> files — read-only, because they are
    ///         immutable once written. On Unix the read-only bit is a mode on the file and deletion is governed by the
    ///         parent directory's write permission, so the plain recursive delete succeeds. On Windows
    ///         <c>FILE_ATTRIBUTE_READONLY</c> blocks the delete itself, and the recursive walk fails part-way with
    ///         <c>UnauthorizedAccessException: Access to the path 'pack-&lt;sha&gt;.idx' is denied</c> — leaving a
    ///         half-removed tree behind.
    ///     </para>
    ///     <para>
    ///         So the attribute is cleared on the way down and the delete is then an ordinary one. This is why the
    ///         helper exists rather than each caller writing <c>Directory.Delete(path, true)</c>: every caller here is
    ///         deleting something Git wrote, so every caller hits it.
    ///     </para>
    /// </summary>
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
    ///     Clears <see cref="FileAttributes.ReadOnly" /> from every file in the tree. Symbolic links are not followed:
    ///     the attribute is cleared on the link itself, never on whatever it points at, so a link planted inside the
    ///     tree cannot be used to strip the read-only bit off a file outside it.
    /// </summary>
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
