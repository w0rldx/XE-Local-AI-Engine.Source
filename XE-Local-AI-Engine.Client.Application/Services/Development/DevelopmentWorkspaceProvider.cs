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

            var create = await git.RunAsync(canonicalRepositoryRoot,
                AgentHomeGit.Arguments("worktree", "add", "--detach", worktreePath, baseCommit),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(create, "The managed Development worktree could not be created.");
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
            || !PathEquals(commonGitDirectory, trustedCommonGitDirectory)
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
