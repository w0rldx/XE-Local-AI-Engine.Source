namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

internal sealed class DevelopmentWorkspaceProvider : IDevelopmentWorkspaceProvider
{
    private const string RuntimeProfile = "development-local";

    /// <summary>
    ///     The runtime profile of the short-lived warm-restore sandbox. A DIFFERENT profile from
    ///     <see cref="RuntimeProfile" /> is what makes its <see cref="SandboxAttachKey" /> different, which is what
    ///     stops <c>CreateOrAttachAsync</c> handing back — and later killing — the agent-facing sandbox.
    /// </summary>
    private const string WarmRuntimeProfile = "development-warm";

    private const int WorkspaceManifestVersion = 2;

    /// <summary>The in-sandbox root the per-task runtime directories are requested at.</summary>
    /// <remarks>
    ///     A <em>requested</em> path: the process provider identity-maps and reports the host path instead, and only
    ///     a provider with a mount layer places anything here. It sits outside the workspace and the scratch tmpfs so
    ///     it cannot shadow either.
    /// </remarks>
    private const string RuntimeMountRoot = "/xe-runtime";

    /// <summary><c>.git/config</c> named in the sandbox-path namespace, whose root IS the workspace.</summary>
    /// <remarks>
    ///     A provider with a mount layer derives the real target from the host path, which is inside the trusted
    ///     workspace, so the engine never has to know what that workspace is called inside the sandbox; this is the
    ///     neutral spelling of the same place.
    /// </remarks>
    private const string GitConfigSandboxPath = "/.git/config";

    /// <summary>Caps the git stderr excerpt carried in a failure message. See <c>RedactGitError</c>.</summary>
    private const int GitErrorExcerptLimit = 500;

    /// <summary>
    ///     The engine-owned directory, inside <c>RuntimePath</c> and never mounted as a directory, holding one empty
    ///     file per shadowed credential.
    /// </summary>
    /// <remarks>
    ///     It sits outside the workspace because the whole point of the shadow is that the real file is left
    ///     byte-unchanged, so the substitute must not be in the tree the diff model reads.
    /// </remarks>
    private const string ShadowDirectoryName = "shadow";

    /// <summary>
    ///     How many committed credentials this engine will neutralize with read-only mounts before refusing. It bounds
    ///     an engine-generated mount list against a repository that could otherwise name thousands of them.
    /// </summary>
    private const int MaxShadowedSecrets = 32;

    /// <summary>The per-task runtime subdirectories a build needs.</summary>
    /// <remarks>
    ///     <c>workspace.json</c>, the workspace control manifest, sits directly in <c>RuntimePath</c> and must be
    ///     unreachable from inside any sandbox; mounting these four named subdirectories rather than their parent is
    ///     what keeps it out. Nothing inside a sandbox needs it, every accessor being host-side in
    ///     <see cref="PrepareAsync" /> and running before the sandbox exists.
    /// </remarks>
    private static readonly string[] RuntimeDirectoryNames = ["home", "tmp", "nuget", "dotnet"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The MSBuild and NuGet files whose discovery walks <em>up</em> from a project directory, written empty one
    ///     level above every managed workspace so that walk stops there.
    /// </summary>
    /// <remarks>
    ///     Without it, on the process provider, a node data root inside another repository makes a registered
    ///     repository's <c>dotnet restore</c> inherit that repository's <c>Directory.Packages.props</c> and fail
    ///     <c>NU1008</c> — validation measuring the host's build configuration, not the repository under test. A
    ///     repository bringing its own copy is unaffected, MSBuild and NuGet stopping at the first file found one
    ///     level below. It lives ABOVE the workspace, or every file here would land in the changed-file manifest.
    /// </remarks>
    private static readonly ConfigurationFile[] BuildConfigurationBarrier =
    [
        new("Directory.Build.props", "<Project>\n  <!-- Bounds MSBuild's upward search to the managed Development workspace below. -->\n</Project>\n"),
        new("Directory.Build.targets", "<Project>\n  <!-- Bounds MSBuild's upward search to the managed Development workspace below. -->\n</Project>\n"),
        new("Directory.Packages.props",
            "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>\n"),
        new("Directory.Solution.props", "<Project>\n  <!-- Bounds MSBuild's upward search to the managed Development workspace below. -->\n</Project>\n")
    ];

    private readonly ComputeOptions _ceilingDefaults;
    private readonly LocalContainerOptions _nodeOptions;
    private readonly INodeDataDirectory _dataDirectory;
    private readonly ISensitiveFileExclusionService _exclusions;
    private readonly DevelopmentOptions _options;
    private readonly IDevelopmentSandboxRuntimeProvider _sandbox;
    private readonly DevelopmentSandboxOptions _sandboxOptions;
    private readonly IDevelopmentWorkspaceSecretsSink _secretsSink;
    private readonly TimeProvider _timeProvider;

    public DevelopmentWorkspaceProvider(INodeDataDirectory dataDirectory,
        IDevelopmentSandboxRuntimeProvider sandbox,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider,
        IDevelopmentWorkspaceSecretsSink secretsSink,
        ISensitiveFileExclusionService? exclusions = null,
        IOptions<DevelopmentSandboxOptions>? sandboxOptions = null,
        IOptions<ComputeOptions>? ceilingDefaults = null,
        IOptions<LocalContainerOptions>? nodeOptions = null)
    {
        _dataDirectory = dataDirectory ?? throw new ArgumentNullException(nameof(dataDirectory));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _secretsSink = secretsSink ?? throw new ArgumentNullException(nameof(secretsSink));

        // The product's single definition of "this file may hold a credential", shared with the tools' read gate and
        // AgentHome's copy filter. Defaulted so a directly constructed provider behaves as the DI-resolved one.
        _exclusions = exclusions ?? new SensitiveFileExclusionService();

        // Defaulted for the reason `exclusions` is, and all of these are bound unconditionally, so the fallbacks are
        // the shipped values rather than a second configuration path, safe posture included.
        _sandboxOptions = (sandboxOptions ?? Options.Create(new DevelopmentSandboxOptions())).Value;
        _ceilingDefaults = (ceilingDefaults ?? Options.Create(new ComputeOptions())).Value;
        _nodeOptions = (nodeOptions ?? Options.Create(new LocalContainerOptions())).Value;
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
        await EnsureBuildConfigurationBarrierAsync(Path.GetDirectoryName(worktreePath)!, cancellationToken);

        // Created here, not only in the tools that run after this returns: a mount layer binds these at create time,
        // and a bind source the daemon invents gets the daemon's ownership, so the container cannot write its HOME.
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
            cancellationToken);
        string baseCommit;
        if (!Directory.Exists(worktreePath))
        {
            var resolve = await git.RunAsync(canonicalRepositoryRoot,
                AgentHomeGit.Arguments("rev-parse", "--verify", $"refs/heads/{snapshot.BaseBranch}^{{commit}}"),
                cancellationToken);
            EnsureGitSuccess(resolve, "The configured base branch could not be resolved.");
            baseCommit = resolve.StandardOutput.Trim();

            await CreateStandaloneWorkspaceAsync(git,
                canonicalRepositoryRoot,
                worktreePath,
                snapshot.BaseBranch,
                baseCommit,
                cancellationToken);
            await WriteWorkspaceManifestAsync(workspaceManifestPath,
                new WorkspaceManifest(WorkspaceManifestVersion,
                    identity,
                    repository.SelectedFolderId,
                    baseCommit),
                cancellationToken);
        }
        else
        {
            var preserved = await ReadWorkspaceManifestAsync(workspaceManifestPath, cancellationToken);
            if (preserved.Version is not (1 or WorkspaceManifestVersion)
                || !string.Equals(preserved.RepositoryIdentityHash, identity, StringComparison.OrdinalIgnoreCase)
                || preserved.SelectedFolderId is { } manifestFolderId && manifestFolderId != repository.SelectedFolderId)
            {
                throw new DevelopmentWorkspaceSecurityException("The preserved Development worktree does not match its trusted workspace manifest.");
            }

            baseCommit = preserved.BaseCommit;
        }

        // Before the first host-side Git command touches a workspace a previous attempt could have written to: the
        // validation below runs on the HOST, so a repository-local exec-bearing key would execute here, not sandboxed.
        await DevelopmentWorkspaceGitConfig.RestoreMinimalAsync(worktreePath, cancellationToken);

        await ValidatePreservedWorktreeAsync(git,
            worktreePath,
            trustedCommonGitDirectory,
            baseCommit,
            cancellationToken);

        // Derived from the index and rewritten every preparation, after the config rewrite and before the gate's first
        // command — which is `git diff --check`, and fails every changed line of a CRLF repository without it.
        await DevelopmentWorkspaceWhitespacePolicy.ApplyAsync(git, worktreePath, cancellationToken);

        var manifest = await ReadWorkspaceManifestAsync(workspaceManifestPath, cancellationToken);
        if (manifest.Version != WorkspaceManifestVersion || manifest.SelectedFolderId is null)
        {
            // Upgrading an older manifest in place. Warm state rides along with `with` rather than being dropped by a
            // fresh construction, or a version bump would make an already-warmed base commit look un-warmed.
            manifest = manifest with
            {
                Version = WorkspaceManifestVersion,
                RepositoryIdentityHash = identity,
                SelectedFolderId = repository.SelectedFolderId,
                BaseCommit = baseCommit
            };
            await WriteWorkspaceManifestAsync(workspaceManifestPath, manifest, cancellationToken);
        }

        var branch = await git.RunAsync(worktreePath,
            AgentHomeGit.Arguments("symbolic-ref", "--quiet", "--short", "HEAD"),
            cancellationToken);
        if (branch.ExitCode == 0)
        {
            throw new DevelopmentWorkspaceSecurityException("The managed Development worktree must remain detached from protected branches.");
        }

        // BEFORE either sandbox is created, so both the warm restore and the attempt see the same shadowed view. The
        // warm restore runs the repository's own MSBuild, which can read a committed credential exactly as a test can.
        var secrets = await DetectCommittedSecretsAsync(git, worktreePath, cancellationToken);
        if (!manifest.DetectedSecretPaths.SequenceEqual(secrets, StringComparer.Ordinal))
        {
            manifest = manifest with
            {
                DetectedSecretPaths = secrets
            };
            await WriteWorkspaceManifestAsync(workspaceManifestPath, manifest, cancellationToken);
        }

        if (secrets.Count > 0)
        {
            // Through the sink, not the store: the snapshot's task and attempt ids are the workspace's isolation keys
            // here, and a caller outside Dev Mode has no rows behind them (see IDevelopmentWorkspaceSecretsSink).
            await _secretsSink.RecordAsync(snapshot.TaskId, snapshot.AttemptId, secrets, cancellationToken);
        }

        await EnsureWarmRestoreAsync(git,
            snapshot,
            identity,
            worktreePath,
            runtimePath,
            workspaceManifestPath,
            manifest,
            baseCommit,
            secrets,
            cancellationToken);

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
            NetworkPolicy = ResolveAgentFacingNetworkPolicy(),
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = worktreePath
            },
            Mounts = BuildMounts(runtimePath, worktreePath, secrets),

            // The node's ceilings wherever the backend can impose them, through the helper every create site shares.
            // Read SandboxResourceCeilings first: its defaults are sized for run_python, not for a `dotnet build`.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.DevelopmentModeHostToolchain,
                _sandbox.Capabilities,
                _ceilingDefaults,
                _nodeOptions)
        }, cancellationToken);

        return new DevelopmentWorkspaceSession
        {
            ProjectId = snapshot.ProjectId,
            TaskId = snapshot.TaskId,
            AttemptId = snapshot.AttemptId,
            BaseCommit = baseCommit,
            RepositoryIdentityHash = identity,
            HostWorktreePath = worktreePath,
            RuntimePath = runtimePath,
            SandboxHandle = handle
        };
    }

    /// <summary>
    ///     The committed files in the managed workspace whose names mark them as credential-bearing, as
    ///     repository-relative paths, sorted.
    /// </summary>
    /// <remarks>
    ///     Read from <c>git ls-files</c> rather than by walking the directory, which is the precise question: only
    ///     tracked content reaches a clone, a committed credential being the real exposure, and the index bounds the
    ///     work by the repository's size rather than by what a build wrote into <c>obj/</c>. Every path SEGMENT is
    ///     tested, so a file under <c>.ssh/</c> is found, as elsewhere for
    ///     <see cref="ISensitiveFileExclusionService.IsSecret" />.
    /// </remarks>
    private async Task<IReadOnlyList<string>> DetectCommittedSecretsAsync(HostGitRunner git,
        string worktreePath,
        CancellationToken cancellationToken)
    {
        var tracked = await git.RunAsync(worktreePath,
            AgentHomeGit.Arguments("ls-files", "-z", "--", "."),
            cancellationToken);
        EnsureGitSuccess(tracked, "The managed Development worktree's tracked files could not be listed.");

        return
        [
            .. tracked.StandardOutput
                      .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                      .Where(path => path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => _exclusions.IsSecret(segment)))
                      .Distinct(StringComparer.Ordinal)
                      .OrderBy(static path => path, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    ///     The egress posture requested for the AGENT-FACING sandbox: <see cref="SandboxNetworkPolicy.None" /> where
    ///     the backend advertises real network confinement, <see cref="SandboxNetworkPolicy.Unrestricted" /> where it
    ///     does not.
    /// </summary>
    /// <remarks>
    ///     Capability-gated, a real limitation rather than defensive coding: a backend fails a request it cannot
    ///     honour CLOSED, so an unconditional <c>None</c> would remove Development Mode from every node resolved to
    ///     the process backend, where the attempt therefore still has egress — reported as the posture SERVED.
    ///     <see cref="DevelopmentSandboxOptions.RequireEgressDenial" /> makes denial a precondition instead, and
    ///     <see cref="SandboxEgressPolicy" /> holds the decision, so no consumer can stop matching the backend.
    /// </remarks>
    private SandboxNetworkPolicy ResolveAgentFacingNetworkPolicy() =>
        SandboxEgressPolicy.Resolve(_sandbox.Capabilities,
            _sandboxOptions.RequireEgressDenial,
            SandboxEgressPolicy.DevelopmentOptionKey,
            SandboxWorkloads.DevelopmentModeHostToolchain.Workload);

    /// <summary>
    ///     Populates the per-task package cache from the BASE COMMIT's dependency manifests, in a second short-lived
    ///     sandbox that has egress, so the agent-facing sandbox created afterwards does not need any.
    /// </summary>
    /// <remarks>
    ///     Running the repository's own restore with network is sound here and only here: at warm time the tree is
    ///     the operator's base commit and the agent has written nothing, so the gate is not "is this code safe" but
    ///     "is this tree provably still the base commit", which the clean-tracked-tree check decides. The cache
    ///     outlives the sandbox in the per-task runtime directories and the worktree's <c>obj/</c> trees, and the warm
    ///     record lives in the never-mounted <c>workspace.json</c>. A profile with no restore command skips this.
    /// </remarks>
    private async Task EnsureWarmRestoreAsync(HostGitRunner git,
        DevelopmentExecutionSnapshot snapshot,
        string identity,
        string worktreePath,
        string runtimePath,
        string workspaceManifestPath,
        WorkspaceManifest manifest,
        string baseCommit,
        IReadOnlyList<string> detectedSecrets,
        CancellationToken cancellationToken)
    {
        var profile = DevelopmentCommandProfileCatalog.ResolveStored(snapshot.CommandProfileJson);
        if (!profile.Commands.Any(static command => string.Equals(command.CommandId, DevelopmentCommandIds.DotnetRestore, StringComparison.Ordinal)))
        {
            return;
        }

        if (string.Equals(manifest.WarmRestoreCommit, baseCommit, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Untracked and ignored files are excluded deliberately: a restore writes obj/ and nothing else, so demanding
        // a pristine directory would refuse every second attempt on a workspace legitimately built once.
        var status = await git.RunAsync(worktreePath,
            AgentHomeGit.Arguments("status", "--porcelain", "--untracked-files=no"),
            cancellationToken);
        EnsureGitSuccess(status, "The managed Development worktree status could not be read before the dependency warm restore.");
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            // Reachable only after a crash between the clone and the first warm. Refusing is the only safe answer:
            // warming against agent-written content is what this design exists to prevent, and a skip names a symptom.
            throw new DevelopmentWorkspaceSecurityException("The managed Development worktree has uncommitted tracked changes and its dependencies have not been warmed for this base "
                                                            + "commit. Reset the task's workspace so the warm restore can run against the base commit alone.");
        }

        var warmHandle = await _sandbox.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = new SandboxAttachKey
            {
                OwnerUserId = snapshot.ProjectId.ToString("N"),
                NodeId = snapshot.TaskId.ToString("N"),
                ProviderName = _sandbox.ProviderName,
                RuntimeProfile = WarmRuntimeProfile,
                ManifestVersion = WorkspaceManifestVersion
            },
            RuntimeProfile = WarmRuntimeProfile,

            // The one Development sandbox that asks for egress, unconditionally and exempt from RequireEgressDenial by
            // design: it never runs agent-written content, and denying it would empty the cache every build relies on.
            NetworkPolicy = SandboxNetworkPolicy.Unrestricted,
            TrustedHostWorkspace = new SandboxTrustedHostWorkspace
            {
                RootPath = worktreePath
            },

            // The SAME mount set as the agent-facing sandbox. A warm that wrote its cache anywhere else would warm
            // nothing the attempt can read.
            Mounts = BuildMounts(runtimePath, worktreePath, detectedSecrets),

            // The SAME ceilings as the agent-facing sandbox too: the warm runs the repository's own restore, so it is
            // exactly as capable of a runaway as the attempt is.
            ResourceLimits = SandboxResourceCeilings.Resolve(SandboxWorkloads.DevelopmentModeHostToolchain,
                _sandbox.Capabilities,
                _ceilingDefaults,
                _nodeOptions)
        }, cancellationToken);

        try
        {
            var warmSession = new DevelopmentWorkspaceSession
            {
                ProjectId = snapshot.ProjectId,
                TaskId = snapshot.TaskId,
                AttemptId = snapshot.AttemptId,
                BaseCommit = baseCommit,
                RepositoryIdentityHash = identity,
                HostWorktreePath = worktreePath,
                RuntimePath = runtimePath,
                SandboxHandle = warmHandle
            };

            // Routed through the same tools the attempt uses, so the warm shares its environment, per-command budget
            // and post-command invariants; a second execution path is how it would drift from the later --no-restore.
            var tools = new DevelopmentWorkspaceTools(_sandbox, warmSession, Options.Create(_options), profile);
            _ = await tools.RunCommandAsync(DevelopmentCommandIds.DotnetRestore, cancellationToken);

            var evidence = tools.CommandEvidence[^1];
            if (!evidence.Completed || evidence.ExitCode != 0)
            {
                throw new InvalidOperationException("The Development dependency warm restore did not succeed, so the attempt's sandbox would have no packages to build against "
                                                    + $"(exit {evidence.ExitCode}, completed {evidence.Completed}). Check the repository's package sources and try again.");
            }
        }
        finally
        {
            // Before PrepareAsync returns, unconditionally. A warm sandbox that outlived this method would be a second
            // container per task with egress, held open for the whole attempt — precisely what the split sandbox design prevents.
            await _sandbox.KillAsync(warmHandle, CancellationToken.None);
        }

        await WriteWorkspaceManifestAsync(workspaceManifestPath,
            manifest with
            {
                WarmRestoreCommit = baseCommit,
                WarmRestoreCompletedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            },
            cancellationToken);
    }

    /// <summary>The engine-generated mounts this feature needs beyond the workspace itself.</summary>
    /// <remarks>
    ///     Naming the four runtime subdirectories individually rather than mounting their parent is the whole of the
    ///     control-state exclusion, <c>workspace.json</c> living in that unmounted parent. <c>.git/config</c> is a
    ///     nested read-only file mount over the read-write workspace, requested only where
    ///     <see cref="SandboxProviderCapabilities.SupportsReadOnlyMounts" /> is advertised: a filter driver that
    ///     cannot be DEFINED cannot run, and a provider without a mount layer fails the request closed.
    /// </remarks>
    private IReadOnlyList<SandboxMount> BuildMounts(string runtimePath, string worktreePath, IReadOnlyList<string> detectedSecrets)
    {
        var mounts = RuntimeDirectoryNames
                     .Select(name => new SandboxMount
                     {
                         HostPath = Path.Combine(runtimePath, name),
                         SandboxPath = RuntimeMountRoot + "/" + name,
                         ReadOnly = false
                     })
                     .ToList();

        var supportsReadOnly = (_sandbox.Capabilities & SandboxProviderCapabilities.SupportsReadOnlyMounts) != SandboxProviderCapabilities.None;
        var gitConfigPath = Path.Combine(worktreePath, ".git", "config");
        if (supportsReadOnly && File.Exists(gitConfigPath))
        {
            mounts.Add(new SandboxMount
            {
                HostPath = gitConfigPath,
                SandboxPath = GitConfigSandboxPath,
                ReadOnly = true
            });
        }

        if (!supportsReadOnly || detectedSecrets.Count == 0)
        {
            return mounts;
        }

        if (detectedSecrets.Count > MaxShadowedSecrets)
        {
            // The cap bounds the MOUNT LIST, so it is checked only where one is generated. Failing closed above it
            // rather than shadowing the first 32 is the point: a partial shadow reads as a control and is not one.
            throw new DevelopmentWorkspaceSecurityException($"The registered repository has {detectedSecrets.Count} committed files whose names mark them as credentials, above the "
                                                            + $"{MaxShadowedSecrets} this engine will neutralize with read-only mounts. Remove them from the repository, or run this "
                                                            + "project on a node whose sandbox has no mount layer, where they are reported rather than shadowed.");
        }

        var shadowRoot = Path.Combine(runtimePath, ShadowDirectoryName);
        Directory.CreateDirectory(shadowRoot);
        foreach (var relativePath in detectedSecrets)
        {
            // One empty file per shadowed path, named by that path's hash so a second prepare reuses it and no two
            // shadows share a mount source, which would make the handle's path resolution answer arbitrarily.
            var shadowPath = Path.Combine(shadowRoot, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(relativePath)))[..32]);
            if (!File.Exists(shadowPath))
            {
                File.WriteAllBytes(shadowPath, []);
            }

            mounts.Add(new SandboxMount
            {
                HostPath = shadowPath,

                // Workspace-RELATIVE: an engine-generated source outside the workspace with a target inside it is the
                // one shape host-path derivation cannot express. The real file is untouched, so no diff, no deletion.
                SandboxPath = "/" + relativePath,
                TargetIsWorkspaceRelative = true,
                ReadOnly = true
            });
        }

        return mounts;
    }

    /// <summary>
    ///     Writes <see cref="BuildConfigurationBarrier" /> into the workspace's parent directory, which is
    ///     engine-owned and outside every sandbox mount.
    /// </summary>
    /// <remarks>
    ///     Rewritten on every prepare rather than only on creation, because an operator or a stray build can delete
    ///     these and a silently missing barrier reopens the defect with no symptom until a restore fails confusingly.
    ///     A file already holding the expected content is left alone.
    /// </remarks>
    private static async Task EnsureBuildConfigurationBarrierAsync(string workspaceParentPath, CancellationToken cancellationToken)
    {
        foreach (var (fileName, content) in BuildConfigurationBarrier)
        {
            var path = Path.Combine(workspaceParentPath, fileName);
            if (File.Exists(path)
                && string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
            {
                continue;
            }

            await File.WriteAllTextAsync(path, content, cancellationToken);
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

    /// <summary>The sanitized excerpt of git's own stderr, which is the only thing saying WHY a git step failed.</summary>
    /// <remarks>
    ///     Dropping it leaves <see cref="EnsureGitSuccess" /> throwing a bare sentence with the cause unrecoverable,
    ///     which is a dead end for an operator and for a failing test alike. Mirrors <c>NodePatchApplyService.Redact</c>:
    ///     strip the temporary-directory prefix so a workspace path cannot ride into an operator-visible message, and
    ///     bound the length so a runaway git diagnostic cannot become the message.
    /// </remarks>
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
    /// </summary>
    /// <remarks>
    ///     A linked worktree's <c>.git</c> is a pointer FILE into the trusted source repository, so binding it into a
    ///     container either breaks git or hands the container the user's real refs, config, objects and <c>hooks</c>,
    ///     which is host-side code execution; a clone owns its own <c>.git</c>. Three steps are not optional: HEAD is
    ///     left attached by a clone and must be detached, the inherited <c>origin</c> is a live path back to the
    ///     source and must go, and the result must still stand on the base commit resolved before the clone.
    /// </remarks>
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
                cancellationToken);
            EnsureGitSuccess(clone, "The managed Development workspace could not be cloned.");

            if (!StandaloneGitClone.IsStandalone(worktreePath))
            {
                throw new DevelopmentWorkspaceSecurityException("The managed Development workspace clone does not own a standalone Git directory.");
            }

            // Detaching onto the pre-resolved base commit rather than the clone's tip closes the window in which the
            // source branch moved: the shallow clone then lacks that commit and this fails instead of drifting.
            var detach = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("checkout", "--detach", baseCommit),
                cancellationToken);
            EnsureGitSuccess(detach, "The managed Development workspace could not be detached onto its base commit.");

            var removeOrigin = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("remote", "remove", "origin"),
                cancellationToken);
            EnsureGitSuccess(removeOrigin, "The managed Development workspace inherited remote could not be removed.");

            var remotes = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("remote"),
                cancellationToken);
            EnsureGitSuccess(remotes, "The managed Development workspace remotes could not be listed.");
            if (!string.IsNullOrWhiteSpace(remotes.StandardOutput))
            {
                throw new DevelopmentWorkspaceSecurityException("The managed Development workspace must not reference any Git remote.");
            }

            var head = await git.RunAsync(worktreePath,
                AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
                cancellationToken);
            EnsureGitSuccess(head, "The managed Development workspace HEAD could not be resolved.");
            if (!string.Equals(head.StandardOutput.Trim(), baseCommit, StringComparison.OrdinalIgnoreCase))
            {
                throw new DevelopmentWorkspaceSecurityException("The managed Development workspace clone does not stand on its resolved base commit.");
            }
        }
        catch
        {
            // Only reachable when the workspace directory did not exist before this call, so removing it cannot
            // destroy a preserved one — and the next attempt would take the preserved branch and trust a half clone.
            StandaloneGitClone.TryDelete(worktreePath);
            throw;
        }
    }

    /// <summary>
    ///     Re-validates a workspace that survived a restart, on the reuse path as well as immediately after creation,
    ///     because ADR 0001 decision 3 requires the workspace and its diff to be preserved.
    /// </summary>
    /// <remarks>
    ///     For a standalone clone the <c>--git-common-dir</c> check asserts that the common directory resolves INSIDE
    ///     the workspace and is explicitly NOT the trusted source's. The negative is stated separately on purpose: a
    ///     change that silently re-pointed the workspace at the source repository would satisfy the first clause by
    ///     accident on a host where the two paths coincide, which is the condition this check exists to prevent. A
    ///     clone prints a relative <c>.git</c>, which <see cref="ResolveGitPathAsync" /> already resolves.
    /// </remarks>
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
            cancellationToken);
        var commonGitDirectory = await ResolveGitPathAsync(git,
            worktreePath,
            "--git-common-dir",
            "The preserved Development worktree Git directory could not be resolved.",
            cancellationToken);
        var head = await git.RunAsync(worktreePath,
            AgentHomeGit.Arguments("rev-parse", "--verify", "HEAD^{commit}"),
            cancellationToken);
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
            cancellationToken);
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
        return await JsonSerializer.DeserializeAsync<WorkspaceManifest>(stream, JsonOptions, cancellationToken)
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
                cancellationToken);
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

    // One barrier file written above the workspace: the name MSBuild/NuGet would search upward for, and the inert
    // content that stops the search there.
    private sealed record ConfigurationFile(string FileName, string Content);

    /// <summary>
    ///     The workspace CONTROL MANIFEST, living directly in the never-mounted <c>RuntimePath</c>, so nothing inside
    ///     any sandbox can read or forge it.
    /// </summary>
    /// <remarks>
    ///     <see cref="WarmRestoreCommit" /> is the base commit whose dependency manifests are already restored into
    ///     this task's package cache, and is what makes the warm run exactly once per base commit. It is nullable
    ///     rather than version-gated, so a manifest written before warming existed deserializes with no warm
    ///     recorded, which is the correct answer for it.
    /// </remarks>
    private sealed record WorkspaceManifest(
        int Version,
        string RepositoryIdentityHash,
        Guid? SelectedFolderId,
        string BaseCommit)
    {
        public string? WarmRestoreCommit { get; init; }

        public long? WarmRestoreCompletedAtUtc { get; init; }

        /// <summary>The committed files whose names mark them as credentials, as this prepare found them.</summary>
        /// <remarks>
        ///     Recorded so the finding survives a restart, and so the operator-facing event and the mount list are two
        ///     views of one value rather than two independent walks.
        /// </remarks>
        public IReadOnlyList<string> DetectedSecretPaths { get; init; } = [];
    }
}
