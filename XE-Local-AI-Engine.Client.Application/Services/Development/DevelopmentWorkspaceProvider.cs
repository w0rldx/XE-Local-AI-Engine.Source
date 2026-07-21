namespace XE_Local_AI_Engine.Client.Services.Development;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Providers.Abstractions;

internal sealed class DevelopmentWorkspaceProvider : IDevelopmentWorkspaceProvider
{
    private const string RuntimeProfile = "development-local";
    private const int WorkspaceManifestVersion = 1;

    private readonly INodeDataDirectory _dataDirectory;
    private readonly DevelopmentOptions _options;
    private readonly ISandboxRuntimeProvider _sandbox;
    private readonly TimeProvider _timeProvider;

    public DevelopmentWorkspaceProvider(INodeDataDirectory dataDirectory,
        ISandboxRuntimeProvider sandbox,
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
        string repositoryRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        DevelopmentTrustPolicy.EnsureCurrent(snapshot, _timeProvider);
        if ((_sandbox.Capabilities & SandboxProviderCapabilities.SupportsTrustedHostWorkspace) == 0)
        {
            throw new SandboxCapabilityNotSupportedException($"The '{_sandbox.ProviderName}' provider cannot bind a preserved trusted host workspace.");
        }

        var canonicalRepositoryRoot = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repositoryRoot);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonicalRepositoryRoot);
        if (!string.Equals(identity, snapshot.RepositoryIdentityHash, StringComparison.OrdinalIgnoreCase))
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

        var git = new HostGitRunner(_options.MaxAttemptDurationSeconds);
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
        }
        else
        {
            var resolve = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
                cancellationToken).ConfigureAwait(false);
            EnsureGitSuccess(resolve, "The preserved Development worktree is not a valid Git worktree.");
            baseCommit = resolve.StandardOutput.Trim();
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
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace { RootPath = worktreePath }
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
        if (baseBranch.StartsWith('-', StringComparison.Ordinal)
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
}
