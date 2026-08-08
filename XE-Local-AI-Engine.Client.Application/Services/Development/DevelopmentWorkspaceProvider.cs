namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Providers.Abstractions;

internal sealed class DevelopmentWorkspaceProvider : IDevelopmentWorkspaceProvider
{
    private const string RuntimeProfile = "development-local";
    private const int WorkspaceManifestVersion = 2;

    /// <summary>
    ///     The in-sandbox root the per-task runtime directories are requested at. A <em>requested</em> path: the process
    ///     provider identity-maps and reports the host path instead, and only a provider with a mount layer places
    ///     anything here. Chosen to sit outside the workspace and the scratch tmpfs so it cannot shadow either.
    /// </summary>
    private const string RuntimeMountRoot = "/xe-runtime";

    /// <summary>
    ///     <c>.git/config</c> named in the sandbox-path namespace, whose root IS the workspace. A provider with a mount
    ///     layer derives the real target from the host path (it is inside the trusted workspace, so the engine must not
    ///     have to know what that workspace is called inside the sandbox); this is the neutral spelling of the same
    ///     place.
    /// </summary>
    private const string GitConfigSandboxPath = "/.git/config";

    /// <summary>Caps the git stderr excerpt carried in a failure message. See <c>RedactGitError</c>.</summary>
    private const int GitErrorExcerptLimit = 500;

    /// <summary>
    ///     The per-task runtime subdirectories a build needs, and the reason the control-manifest exclusion is satisfied at zero cost.
    ///     <para>
    ///         <c>workspace.json</c> — the workspace CONTROL MANIFEST — sits directly in <c>RuntimePath</c>, and it
    ///         must be unreachable from inside any sandbox. Mounting these four named subdirectories rather
    ///         than their parent is what keeps it out. Nothing inside a sandbox needs it: every accessor is host-side in
    ///         <see cref="PrepareAsync" /> and runs before the sandbox exists.
    ///     </para>
    /// </summary>
    private static readonly string[] RuntimeDirectoryNames = ["home", "tmp", "nuget", "dotnet"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The MSBuild and NuGet files whose discovery walks <em>up</em> from a project directory until the first hit,
    ///     written empty one level above every managed workspace so that walk stops at the workspace instead of
    ///     escaping into whatever happens to be above the node's data directory.
    ///     <para>
    ///         Reproduced live on 2026-07-31 with the process sandbox provider, which is the shipped default. Running
    ///         from a source checkout puts the node's data root inside this repository, so a registered repository's
    ///         <c>dotnet restore</c> inherited <em>this</em> repository's <c>Directory.Packages.props</c> and failed
    ///         <c>NU1008</c> — Central Package Management demanded a <c>PackageVersion</c> item for a package the
    ///         target repository declares perfectly legally inline. Validation was measuring the host's build
    ///         configuration, not the repository under test. The container provider never had this: its mount root
    ///         <em>is</em> the workspace, so the walk already terminated there.
    ///     </para>
    ///     <para>
    ///         A repository that brings its own copy of one of these files is unaffected — MSBuild and NuGet stop at
    ///         the first file found, which is the repository's own, one level below this barrier. The barrier is only
    ///         ever read for repositories that declare nothing, and for those "no central package management, no
    ///         inherited props or targets" is the correct answer rather than an imposed one.
    ///     </para>
    ///     <para>
    ///         It lives one level ABOVE the workspace on purpose. Written inside it, every file here would appear in
    ///         <c>git status</c> as an untracked change and land in the attempt's changed-file manifest — the evidence
    ///         the whole feature is built on.
    ///     </para>
    /// </summary>
    private static readonly (string FileName, string Content)[] BuildConfigurationBarrier =
    [
        ("Directory.Build.props", "<Project>\n  <!-- Bounds MSBuild's upward search to the managed Development workspace below. -->\n</Project>\n"),
        ("Directory.Build.targets", "<Project>\n  <!-- Bounds MSBuild's upward search to the managed Development workspace below. -->\n</Project>\n"),
        ("Directory.Packages.props",
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n"),
        ("Directory.Solution.props", "<Project>\n  <!-- Bounds MSBuild's upward search to the managed Development workspace below. -->\n</Project>\n")
    ];

    private readonly INodeDataDirectory _dataDirectory;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly TimeProvider _timeProvider;

    public DevelopmentWorkspaceProvider(INodeDataDirectory dataDirectory,
        IDevelopmentSandboxRuntimeProvider sandbox,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<DevelopmentWorkspaceSession> PrepareAsync(DevelopmentExecutionSnapshot snapshot,
        DevelopmentRepositoryBinding repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(repository);
        DevelopmentTrustPolicy.EnsureCurrent(snapshot, _timeProvider);
        if ((_sandbox.Capabilities & SandboxProviderCapabilities.SupportsTrustedHostWorkspace) == SandboxProviderCapabilities.None)
        {
            throw new SandboxCapabilityNotSupportedException($"The '{_sandbox.ProviderName}' provider cannot bind a preserved trusted host workspace.");
        }

        var canonicalRepositoryRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository.RepositoryRoot);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonicalRepositoryRoot);
        if (repository.ProjectId != snapshot.ProjectId
            || repository.SelectedFolderId != snapshot.SelectedFolderId
            || !string.Equals(identity, repository.RepositoryIdentityHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(identity, snapshot.RepositoryIdentityHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentWorkspaceSecurityException("The supplied repository does not match the persisted trusted repository identity.");
        }

        ValidateBaseBranch(snapshot.BaseBranch);
        var worktreePath = Path.Combine(_dataDirectory.Root,
            "development",
            "workspaces",
            snapshot.ProjectId.ToString("N"),
            snapshot.TaskId.ToString("N"));
        var runtimePath = Path.Combine(_dataDirectory.Root,
            "development",
            "runtime",
            snapshot.ProjectId.ToString("N"),
            snapshot.TaskId.ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        Directory.CreateDirectory(runtimePath);
        EnsureBuildConfigurationBarrier(Path.GetDirectoryName(worktreePath)!);

        // Created HERE and not only in DevelopmentWorkspaceTools, which runs after this method returns. A provider with
        // a mount layer binds these directories at create time, and a bind source the daemon has to invent is created
        // with the DAEMON's ownership — under a rootful daemon that is root, and the container then cannot write its
        // own HOME. The tools' own EnsureRuntimeDirectories stays: it is what keeps a directly constructed tools
        // instance working, and creating an existing directory costs nothing.
        foreach (var name in RuntimeDirectoryNames)
        {
            Directory.CreateDirectory(Path.Combine(runtimePath, name));
        }

        var workspaceManifestPath = Path.Combine(runtimePath, "workspace.json");

        var git = new HostGitRunner(_options.MaxAttemptDurationSeconds);
        var trustedCommonGitDirectory = await ResolveGitPathAsync(git,
            canonicalRepositoryRoot,
            "--git-common-dir",
            "The trusted repository Git directory could not be resolved.",
            cancellationToken).ConfigureAwait(false);
        string baseCommit;
        if (!Directory.Exists(worktreePath))
        {
            var resolve = await git.RunAsync(canonicalRepositoryRoot,
                AgentHomeGit.Arguments("rev-parse", "--verify", $"refs/heads/{snapshot.BaseBranch}^{{commit}}"),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(resolve, "The configured base branch could not be resolved.");
            baseCommit = resolve.StandardOutput.Trim();

            await CreateStandaloneWorkspaceAsync(git,
                canonicalRepositoryRoot,
                worktreePath,
                snapshot.BaseBranch,
                baseCommit,
                cancellationToken).ConfigureAwait(false);
            await WriteWorkspaceManifestAsync(workspaceManifestPath,
                new WorkspaceManifest(WorkspaceManifestVersion,
                    identity,
                    repository.SelectedFolderId,
                    baseCommit),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var manifest = await ReadWorkspaceManifestAsync(workspaceManifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest.Version is not (1 or WorkspaceManifestVersion)
                || !string.Equals(manifest.RepositoryIdentityHash, identity, StringComparison.OrdinalIgnoreCase)
                || manifest.SelectedFolderId is { } manifestFolderId && manifestFolderId != repository.SelectedFolderId)
            {
                throw new DevelopmentWorkspaceSecurityException("The preserved Development worktree does not match its trusted workspace manifest.");
            }

            baseCommit = manifest.BaseCommit;
        }

        // BEFORE the first host-side Git command touches a workspace a previous attempt could have written to. The
        // validation below runs `rev-parse` and `symbolic-ref` on the HOST with this workspace as the working
        // directory, so a repository-local exec-bearing key would be executing here, not in the sandbox. Also covers
        // the freshly cloned case, where it is a cheap no-op — the clone's own config already contains nothing but the
        // preserved keys once `origin` has been removed.
        DevelopmentWorkspaceGitConfig.RestoreMinimal(worktreePath);

        await ValidatePreservedWorktreeAsync(git,
            worktreePath,
            trustedCommonGitDirectory,
            baseCommit,
            cancellationToken).ConfigureAwait(false);

        // Derived from the index and rewritten every preparation, immediately after the config rewrite above and before
        // any command of the validation gate runs. Without it, `git diff --check` — the FIRST command of every .NET
        // profile — reports trailing whitespace on every changed line of a repository that legitimately stores CRLF,
        // and the gate fails at command one on a correct change.
        await DevelopmentWorkspaceWhitespacePolicy.ApplyAsync(git, worktreePath, cancellationToken).ConfigureAwait(false);

        var persistedManifest = await ReadWorkspaceManifestAsync(workspaceManifestPath, cancellationToken).ConfigureAwait(false);
        if (persistedManifest.Version != WorkspaceManifestVersion || persistedManifest.SelectedFolderId is null)
        {
            await WriteWorkspaceManifestAsync(workspaceManifestPath,
                new WorkspaceManifest(WorkspaceManifestVersion, identity, repository.SelectedFolderId, baseCommit),
                cancellationToken).ConfigureAwait(false);
        }

        var branch = await git.RunAsync(worktreePath,
            AgentHomeGit.Arguments("symbolic-ref", "--quiet", "--short", "HEAD"),
            cancellationToken).ConfigureAwait(false);
        if (branch.ExitCode == 0)
        {
            throw new DevelopmentWorkspaceSecurityException("The managed Development worktree must remain detached from protected branches.");
        }

        var attachKey = new SandboxAttachKey
        {
            OwnerUserId = snapshot.ProjectId.ToString("N"),
            NodeId = snapshot.TaskId.ToString("N"),
            ProviderName = _sandbox.ProviderName,
            RuntimeProfile = RuntimeProfile,
            ManifestVersion = WorkspaceManifestVersion
        };
        var handle = await _sandbox.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = attachKey,
            RuntimeProfile = RuntimeProfile,
            // DELIBERATELY Unrestricted — a recorded deferral, not an oversight, and not an inconsistency with
            // AgentHome. AgentHome now requests default-deny egress because everything it
            // runs in the sandbox is local. Development Mode is different: the dotnet-slnx / dotnet-csproj profiles run
            // `dotnet restore` into a per-task NUGET_PACKAGES root that starts COLD, so denying egress here today would
            // not harden Development Mode — it would break it outright, along with the validation gate that depends on
            // a real restore/build/test run.
            //
            // Turning this off is future work, and it is only safe once dependency-manifest-rejection machinery exists: restore limited to
            // the base commit's manifests, plus a dependency-manifest change failing validation with its specific
            // reason. Until both halves land, "network off" here is an outage rather than a hardening win.
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = worktreePath
            },
            Mounts = BuildMounts(runtimePath, worktreePath)
        }, cancellationToken).ConfigureAwait(false);

        return new DevelopmentWorkspaceSession(snapshot.ProjectId,
            snapshot.TaskId,
            snapshot.AttemptId,
            baseCommit,
            identity,
            worktreePath,
            runtimePath,
            handle);
    }

    /// <summary>
    ///     The engine-generated mounts this feature needs beyond the workspace itself.
    ///     <para>
    ///         The four runtime subdirectories are named individually rather than by mounting their parent, and that is
    ///         the whole of the control-state exclusion: <c>workspace.json</c> lives in the parent and stays outside
    ///         every sandbox because the parent is never mounted.
    ///     </para>
    ///     <para>
    ///         <c>.git/config</c> is requested READ-ONLY, and only from a provider that advertises
    ///         <see cref="SandboxProviderCapabilities.SupportsReadOnlyMounts" />. It is a nested file mount layered over
    ///         the read-write workspace: the work tree stays writable so the agent can edit, <c>.git/index</c> stays
    ///         writable so <c>git apply --index</c> still works, and <c>.git/config</c> becomes both unwritable and
    ///         unremovable — a filter driver that cannot be DEFINED cannot run, whatever an in-tree
    ///         <c>.gitattributes</c> selects.
    ///     </para>
    ///     <para>
    ///         The capability gate is not defensive coding. A provider with no mount layer fails a read-only request
    ///         closed rather than serving it writable, so requesting it unconditionally would kill Development Mode
    ///         outright on the process provider it runs on today — which is exactly why the engine-side rewrite in
    ///         <see cref="DevelopmentWorkspaceGitConfig" /> exists as the provider-independent half rather than as a
    ///         belt-and-braces extra.
    ///     </para>
    /// </summary>
    private IReadOnlyList<SandboxMount> BuildMounts(string runtimePath, string worktreePath)
    {
        var mounts = RuntimeDirectoryNames
                     .Select(name => new SandboxMount
                     {
                         HostPath = Path.Combine(runtimePath, name),
                         SandboxPath = RuntimeMountRoot + "/" + name,
                         ReadOnly = false
                     })
                     .ToList();

        var gitConfigPath = Path.Combine(worktreePath, ".git", "config");
        if ((_sandbox.Capabilities & SandboxProviderCapabilities.SupportsReadOnlyMounts) != SandboxProviderCapabilities.None
            && File.Exists(gitConfigPath))
        {
            mounts.Add(new SandboxMount
            {
                HostPath = gitConfigPath,
                SandboxPath = GitConfigSandboxPath,
                ReadOnly = true
            });
        }

        return mounts;
    }

    /// <summary>
    ///     Writes <see cref="BuildConfigurationBarrier" /> into the workspace's parent directory, which is engine-owned
    ///     and outside every sandbox mount.
    ///     <para>
    ///         Rewritten on every prepare rather than only on creation: an operator (or a stray build) can delete these,
    ///         and a silently missing barrier reopens the defect with no symptom until a restore fails confusingly. A
    ///         file that already holds the expected content is left alone.
    ///     </para>
    /// </summary>
    private static void EnsureBuildConfigurationBarrier(string workspaceParentPath)
    {
        foreach (var (fileName, content) in BuildConfigurationBarrier)
        {
            var path = Path.Combine(workspaceParentPath, fileName);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            {
                continue;
            }

            File.WriteAllText(path, content);
        }
    }

    private static void ValidateBaseBranch(string baseBranch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseBranch);
        if (baseBranch[0] == '-'
            || baseBranch.Contains("..", StringComparison.Ordinal)
            || baseBranch.Contains("@{", StringComparison.Ordinal)
            || baseBranch.Any(char.IsControl))
        {
            throw new DevelopmentWorkspaceSecurityException("The configured base branch is not a safe Git branch name.");
        }
    }

    private static void EnsureGitSuccess(HostGitResult result, string message)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException($"{message} (git exited {result.ExitCode}: {RedactGitError(result.StandardError)})");
    }

    /// <summary>
    ///     git's own stderr is the only thing that says <em>why</em> a clone or checkout failed. Dropping it left
    ///     <see cref="EnsureGitSuccess" /> throwing a bare sentence with the cause unrecoverable — which is exactly what
    ///     ten Development tests hit on a Windows host, reporting "could not be cloned" and nothing actionable. An
    ///     operator seeing this in production got the same dead end.
    ///     <para>
    ///         Mirrors <c>NodePatchApplyService.Redact</c>: strip the temporary-directory prefix so a workspace path does
    ///         not ride into an operator-visible message, and bound the length so a runaway git diagnostic cannot become
    ///         the message.
    ///     </para>
    /// </summary>
    private static string RedactGitError(string standardError)
    {
        if (string.IsNullOrWhiteSpace(standardError))
        {
            return "git produced no diagnostic output";
        }

        var redacted = standardError.Replace(Path.GetTempPath(), "<tmp>/", StringComparison.Ordinal).Trim();
        return redacted.Length > GitErrorExcerptLimit
            ? string.Concat(redacted.AsSpan(0, GitErrorExcerptLimit), "…")
            : redacted;
    }

    /// <summary>
    ///     Creates the managed workspace as an engine-owned standalone clone, replacing
    ///     <c>git worktree add --detach</c>.
    ///     <para>
    ///         A linked worktree's <c>.git</c> is a pointer <em>file</em> into the trusted source repository, so binding
    ///         the workspace into a container either breaks git outright or hands the container the user's real
    ///         repository — refs, config, objects and <c>hooks</c>, which is host-side arbitrary code execution. A clone
    ///         owns its own <c>.git</c>, so the workspace is self-contained and the trusted source repository is not
    ///         reachable from it.
    ///     </para>
    ///     <para>
    ///         Three steps here are not optional, and each one fails an assertion that would otherwise pass silently:
    ///         a clone leaves HEAD <em>attached</em> to the cloned branch, so it must be detached or both this provider's
    ///         own <c>symbolic-ref</c> check and <c>DevelopmentWorkspaceTools.EnsureWorkspaceInvariantAsync</c> reject
    ///         it after the first catalog command; a clone <em>inherits</em> <c>origin</c> pointing at the trusted source
    ///         repository, which is a live named path straight back to the thing that is supposed to be unreachable
    ///         (discarding <c>.git</c> gets this for free, and this path deliberately cannot); and the result
    ///         must still be standing on the base commit that was resolved before the clone.
    ///     </para>
    /// </summary>
    private static async Task CreateStandaloneWorkspaceAsync(HostGitRunner git,
        string canonicalRepositoryRoot,
        string worktreePath,
        string baseBranch,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        var parent = Path.GetDirectoryName(worktreePath)
                     ?? throw new DevelopmentWorkspaceSecurityException("The managed Development workspace path has no parent directory.");
        try
        {
            var clone = await git.RunAsync(parent,
                AgentHomeGit.Arguments([.. StandaloneGitClone.Arguments(canonicalRepositoryRoot, worktreePath, baseBranch)]),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(clone, "The managed Development workspace could not be cloned.");

            if (!StandaloneGitClone.IsStandalone(worktreePath))
            {
                throw new DevelopmentWorkspaceSecurityException("The managed Development workspace clone does not own a standalone Git directory.");
            }

            // Detaching onto the pre-resolved base commit rather than the clone's own tip is also what closes the
            // window between resolving the base branch and cloning it. If the source branch moved in between, the
            // shallow clone does not contain the recorded commit at all and this fails outright instead of silently
            // producing a workspace standing on a different base than the one persisted in the manifest.
            var detach = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("checkout", "--detach", baseCommit),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(detach, "The managed Development workspace could not be detached onto its base commit.");

            var removeOrigin = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("remote", "remove", "origin"),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(removeOrigin, "The managed Development workspace inherited remote could not be removed.");

            var remotes = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("remote"),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(remotes, "The managed Development workspace remotes could not be listed.");
            if (!string.IsNullOrWhiteSpace(remotes.StandardOutput))
            {
                throw new DevelopmentWorkspaceSecurityException("The managed Development workspace must not reference any Git remote.");
            }

            var head = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(head, "The managed Development workspace HEAD could not be resolved.");
            if (!string.Equals(head.StandardOutput.Trim(), baseCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new DevelopmentWorkspaceSecurityException("The managed Development workspace clone does not stand on its resolved base commit.");
            }
        }
        catch
        {
            // Only reachable when the workspace directory did not exist before this call, so removing it cannot destroy
            // a preserved workspace. Leaving a half-cloned tree behind would be worse than none: the next attempt takes
            // the preserved-workspace branch and trusts it.
            StandaloneGitClone.TryDelete(worktreePath);
            throw;
        }
    }

    /// <summary>
    ///     Re-validates a workspace that survived a restart. ADR 0001 decision 3 requires the workspace and its diff to
    ///     be preserved, so this runs on the reuse path as well as immediately after creation.
    ///     <para>
    ///         The <c>--git-common-dir</c> check <em>inverted</em> its meaning once the workspace became a standalone clone. It used to assert the
    ///         workspace's common directory <em>equals</em> the trusted source repository's, which is what proved the
    ///         workspace was a linked worktree of the bound repository. A standalone clone must assert the opposite: the
    ///         common directory resolves <em>inside</em> the workspace, and is explicitly <em>not</em> the trusted
    ///         source's. The negative is stated separately on purpose — a change that silently re-pointed the workspace
    ///         at the source repository would otherwise satisfy the first clause by accident on a host where the two
    ///         paths coincide, and this is exactly the condition this check exists to prevent.
    ///     </para>
    ///     <para>
    ///         <c>rev-parse --git-common-dir</c> prints a <em>relative</em> <c>.git</c> in a clone (it printed an
    ///         absolute path for a linked worktree), which needs no new plumbing:
    ///         <see cref="ResolveGitPathAsync" /> already resolves against the working directory.
    ///     </para>
    /// </summary>
    private static async Task ValidatePreservedWorktreeAsync(HostGitRunner git,
        string worktreePath,
        string trustedCommonGitDirectory,
        string baseCommit,
        CancellationToken cancellationToken)
    {
        var canonicalWorktree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(worktreePath));
        var topLevel = await ResolveGitPathAsync(git,
            worktreePath,
            "--show-toplevel",
            "The preserved Development worktree is not a valid Git worktree.",
            cancellationToken).ConfigureAwait(false);
        var commonGitDirectory = await ResolveGitPathAsync(git,
            worktreePath,
            "--git-common-dir",
            "The preserved Development worktree Git directory could not be resolved.",
            cancellationToken).ConfigureAwait(false);
        var head = await git.RunAsync(worktreePath,
            AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(head, "The preserved Development worktree HEAD could not be resolved.");

        if (!PathEquals(topLevel, canonicalWorktree)
            || !PathEquals(commonGitDirectory, Path.Combine(canonicalWorktree, ".git"))
            || PathEquals(commonGitDirectory, trustedCommonGitDirectory)
            || !StandaloneGitClone.IsStandalone(canonicalWorktree)
            || !string.Equals(head.StandardOutput.Trim(), baseCommit, StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentWorkspaceSecurityException("The preserved Development worktree no longer matches its exact trusted base.");
        }
    }

    private static async Task<string> ResolveGitPathAsync(HostGitRunner git,
        string workingDirectory,
        string argument,
        string error,
        CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(workingDirectory,
            AgentHomeGit.Arguments("rev-parse", argument),
            cancellationToken).ConfigureAwait(false);
        EnsureGitSuccess(result, error);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(result.StandardOutput.Trim(), workingDirectory));
    }

    private static async Task<WorkspaceManifest> ReadWorkspaceManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new DevelopmentWorkspaceSecurityException("The preserved Development worktree has no trusted workspace manifest.");
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<WorkspaceManifest>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new DevelopmentWorkspaceSecurityException("The preserved Development workspace manifest is invalid.");
    }

    private static async Task WriteWorkspaceManifestAsync(string path,
        WorkspaceManifest manifest,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static bool PathEquals(string first, string second) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed record WorkspaceManifest(
        int Version,
        string RepositoryIdentityHash,
        Guid? SelectedFolderId,
        string BaseCommit);
}
