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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

        await ValidatePreservedWorktreeAsync(git,
            worktreePath,
            trustedCommonGitDirectory,
            baseCommit,
            cancellationToken).ConfigureAwait(false);

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
            // AgentHome. AgentHome now requests default-deny egress (PLAN-sandbox-hardening S2) because everything it
            // runs in the sandbox is local. Development Mode is different: the dotnet-slnx / dotnet-csproj profiles run
            // `dotnet restore` into a per-task NUGET_PACKAGES root that starts COLD, so denying egress here today would
            // not harden Development Mode — it would break it outright, along with the validation gate that depends on
            // a real restore/build/test run.
            //
            // Turning this off is S3.6, and it is only safe once D6's companion machinery exists: restore limited to
            // the base commit's manifests, plus a dependency-manifest change failing validation with its specific
            // reason. Until both halves land, "network off" here is an outage rather than a hardening win.
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = worktreePath
            }
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
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    ///     Creates the managed workspace as an engine-owned standalone clone (decision D8), replacing
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
    ///         (Slice 2 gets this for free by discarding <c>.git</c>, and this path deliberately cannot); and the result
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
    ///         The <c>--git-common-dir</c> check <em>inverted</em> its meaning under D8. It used to assert the
    ///         workspace's common directory <em>equals</em> the trusted source repository's, which is what proved the
    ///         workspace was a linked worktree of the bound repository. A standalone clone must assert the opposite: the
    ///         common directory resolves <em>inside</em> the workspace, and is explicitly <em>not</em> the trusted
    ///         source's. The negative is stated separately on purpose — a change that silently re-pointed the workspace
    ///         at the source repository would otherwise satisfy the first clause by accident on a host where the two
    ///         paths coincide, and this is exactly the condition D8 exists to prevent.
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
