namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     G1(a): the engine-run warm restore that populates the per-task package cache from the BASE COMMIT before the
///     agent-facing sandbox exists, which is what lets that sandbox be created with no egress at all.
///     <para>
///         Two properties carry the design and both are asserted as facts about the create requests rather than as
///         behaviour of a build: the warm sandbox is a SECOND sandbox under its own attach key (one sandbox cannot have
///         network for one command and not the next — <see cref="SandboxNetworkPolicy" /> is fixed at create), and it
///         does not outlive <c>PrepareAsync</c>.
///     </para>
/// </summary>
public sealed class DevelopmentWarmRestoreTests : IDisposable
{
    private static readonly DevelopmentCommandProfile DotnetProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Fixture.slnx");

    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-warm-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    [Test]
    public async Task PrepareAsync_WarmsUnderItsOwnAttachKeyWithEgressAndTheSameMounts_ThenKillsIt()
    {
        var sandbox = new RecordingSandbox();
        var (session, _) = await PrepareAsync(sandbox, DotnetProfile).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, sandbox.Created.Count, "the warm restore must be a SECOND sandbox, not a re-used one.");
        var warm = sandbox.Created[0];
        var agentFacing = sandbox.Created[1];

        // Warm FIRST, agent-facing second. The order is the whole point: the cache has to exist before the sandbox
        // that cannot fetch it is created.
        AssertEx.Equal("development-warm", warm.RuntimeProfile);
        AssertEx.Equal("development-local", agentFacing.RuntimeProfile);
        AssertEx.NotEqual(warm.AttachKey, agentFacing.AttachKey);
        AssertEx.Equal(SandboxNetworkPolicy.Unrestricted, warm.NetworkPolicy);

        // The ceilings axis, asserted through the SHARED derivation rather than against a literal, so a create site
        // cannot disagree with its declaration or with the node's numbers. Both sandboxes are asserted: the warm one
        // runs the repository's own restore and is exactly as capable of a runaway as the attempt.
        var expectedCeilings = SandboxResourceCeilings.Resolve(SandboxWorkloads.DevelopmentModeHostToolchain,
            sandbox.Capabilities,
            new ComputeOptions());
        AssertEx.Equal(expectedCeilings, warm.ResourceLimits);
        AssertEx.Equal(expectedCeilings, agentFacing.ResourceLimits);

        // The same mount set, or the warm writes its cache somewhere the attempt cannot read.
        AssertEx.Equal(string.Join('|', (agentFacing.Mounts ?? []).Select(static mount => mount.HostPath + "=>" + mount.SandboxPath)),
            string.Join('|', (warm.Mounts ?? []).Select(static mount => mount.HostPath + "=>" + mount.SandboxPath)));
        AssertEx.Equal(session.HostWorktreePath, AssertEx.NotNull(warm.TrustedHostWorkspace).RootPath);

        // Killed before PrepareAsync returned, and only the warm one. A warm sandbox that outlived this method would
        // be a second container per task holding egress open for the whole attempt.
        AssertEx.Equal(expected: 1, sandbox.Killed.Count);
        AssertEx.Equal("development-warm", sandbox.Killed[0].AttachKey.RuntimeProfile);
    }

    /// <summary>
    ///     Exactly the frozen profile's <c>dotnet_restore</c>, and nothing else. The warm runs repository-authored
    ///     MSBuild with network, so the set of things it may run is the narrowest possible one — a second command here
    ///     would be a second thing executing against the base commit with egress.
    /// </summary>
    [Test]
    public async Task PrepareAsync_RunsExactlyTheProfilesRestoreCommandInTheWarmSandbox()
    {
        var sandbox = new RecordingSandbox();
        _ = await PrepareAsync(sandbox, DotnetProfile).ConfigureAwait(false);

        var restoreCommand = DotnetProfile.ResolveCommand(DevelopmentCommandIds.DotnetRestore);
        var catalogCommands = sandbox.Executed
                                     .Where(static request => !request.ExecutionId.StartsWith("verify_detached", StringComparison.Ordinal))
                                     .ToArray();

        AssertEx.Equal(expected: 1, catalogCommands.Length, string.Join(", ", catalogCommands.Select(static request => request.ExecutionId)));
        AssertEx.True(catalogCommands[0].ExecutionId.StartsWith(DevelopmentCommandIds.DotnetRestore + "-", StringComparison.Ordinal),
            catalogCommands[0].ExecutionId);
        AssertEx.Equal(restoreCommand.Executable, catalogCommands[0].Executable);
        AssertEx.Equal(string.Join(' ', restoreCommand.Arguments), string.Join(' ', catalogCommands[0].Arguments));
    }

    /// <summary>
    ///     Once per base commit. The recorded warm lives in <c>workspace.json</c>, which sits in the runtime path and
    ///     is never mounted, so nothing inside a sandbox can clear it to force a second networked run.
    /// </summary>
    [Test]
    public async Task PrepareAsync_WhenTheBaseCommitIsAlreadyWarm_DoesNotWarmAgain()
    {
        var sandbox = new RecordingSandbox();
        var (_, prepare) = await PrepareAsync(sandbox, DotnetProfile).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, sandbox.Created.Count(static request => request.RuntimeProfile == "development-warm"));

        _ = await prepare().ConfigureAwait(false);

        AssertEx.Equal(expected: 1, sandbox.Created.Count(static request => request.RuntimeProfile == "development-warm"));
    }

    /// <summary>
    ///     The gate. A warm against a tree the agent has written to would restore the agent's own manifests with
    ///     network, which is the exact thing G1 exists to prevent — so a dirty tracked tree with no recorded warm
    ///     fails the prepare, naming the cause rather than leaving the attempt to fail later with "restore could not
    ///     reach the network".
    /// </summary>
    [Test]
    public async Task PrepareAsync_WhenAWarmIsNeededAndTheTrackedTreeIsDirty_RefusesRatherThanWarmingAgainstIt()
    {
        var sandbox = new RecordingSandbox
        {
            RestoreExitCode = 1
        };
        var (repository, data, snapshot, baseCommit) = await SeedAsync(DotnetProfile).ConfigureAwait(false);
        sandbox.BaseCommit = baseCommit;
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, Options.Create(OptionsValue()), TimeProvider.System, Substitute.For<IDevelopmentStore>());

        // A failing warm records nothing, which is what leaves the workspace in the "cloned but never warmed" state a
        // crash between the clone and the first warm would also produce.
        var failed = await AssertEx.ThrowsAsync<InvalidOperationException>(() => provider.PrepareAsync(snapshot, Binding(snapshot, repository)))
                                   .ConfigureAwait(false);
        AssertEx.Contains(failed.Message, "warm restore", StringComparison.Ordinal);

        var worktree = Path.Combine(data, "development", "workspaces", snapshot.ProjectId.ToString("N"), snapshot.TaskId.ToString("N"));
        await File.WriteAllTextAsync(Path.Combine(worktree, "README.md"), "agent wrote this\n").ConfigureAwait(false);
        sandbox.RestoreExitCode = 0;

        var refused = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => provider.PrepareAsync(snapshot, Binding(snapshot, repository)))
                                    .ConfigureAwait(false);
        AssertEx.Contains(refused.Message, "uncommitted tracked changes", StringComparison.Ordinal);
    }

    /// <summary>
    ///     A profile with no restore command has nothing to warm, and a second sandbox for it would be pure cost.
    /// </summary>
    [Test]
    public async Task PrepareAsync_WhenTheProfileDeclaresNoRestoreCommand_SkipsWarmingEntirely()
    {
        var sandbox = new RecordingSandbox();
        _ = await PrepareAsync(sandbox, GenericProfile).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, sandbox.Created.Count);
        AssertEx.Equal("development-local", sandbox.Created[0].RuntimeProfile);
        AssertEx.Empty(sandbox.Killed);
        AssertEx.Empty(sandbox.Executed);
    }

    private async Task<(DevelopmentWorkspaceSession Session, Func<Task<DevelopmentWorkspaceSession>> Prepare)> PrepareAsync(RecordingSandbox sandbox,
        DevelopmentCommandProfile profile)
    {
        var (repository, data, snapshot, baseCommit) = await SeedAsync(profile).ConfigureAwait(false);
        sandbox.BaseCommit = baseCommit;
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, Options.Create(OptionsValue()), TimeProvider.System, Substitute.For<IDevelopmentStore>());
        var binding = Binding(snapshot, repository);
        var session = await provider.PrepareAsync(snapshot, binding).ConfigureAwait(false);
        return (session, () => provider.PrepareAsync(snapshot, binding));
    }

    private async Task<(string Repository, string Data, DevelopmentExecutionSnapshot Snapshot, string BaseCommit)> SeedAsync(DevelopmentCommandProfile profile)
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repository);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.email", "development-warm@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.name", "Development Warm Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "add", "README.md").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);
        var baseCommit = await ReadGitOutputAsync(repository, "rev-parse", "--verify", "refs/heads/main^{commit}").ConfigureAwait(false);

        var data = Path.Combine(_root, "d-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository));
        return (repository, data, Snapshot(identity, profile), baseCommit);
    }

    /// <summary>
    ///     The base commit has to be known BEFORE the prepare, because the warm restore runs inside it and the
    ///     recording sandbox has to answer the post-command workspace invariant probe with the right value.
    /// </summary>
    private static async Task<string> ReadGitOutputAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process
        {
            StartInfo = startInfo
        };
        process.Start();
        var error = process.StandardError.ReadToEndAsync();
        var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 0, process.ExitCode, await error.ConfigureAwait(false));
        return output.Trim();
    }

    private static DevelopmentOptions OptionsValue() =>
        new()
        {
            Enabled = true,
            MaxArtifactBytes = 2 * 1024 * 1024,
            MaxPatchBytes = 1024 * 1024,
            MaxFileWriteBytes = 1024 * 1024,
            MaxCommandOutputBytes = 256 * 1024,
            MaxChangedFiles = 32,
            MaxToolCalls = 16,
            MaxAttemptDurationSeconds = 60,
            MaxOutputTokens = 2048
        };

    private static DevelopmentExecutionSnapshot Snapshot(string identity, DevelopmentCommandProfile profile) =>
        new(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            identity,
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            TrustedRepositoryAcknowledged: true,
            DevelopmentTrustPolicy.CurrentVersion,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 60,
            "Implement feature",
            "Add the bounded feature file.",
            "[\"feature.txt exists\"]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 3,
            DevelopmentAttemptRole.Coder,
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1,
            Encoding.UTF8.GetString(profile.ToCanonicalUtf8()));

    private static DevelopmentRepositoryBinding Binding(DevelopmentExecutionSnapshot snapshot, string repository) =>
        new(snapshot.ProjectId,
            snapshot.SelectedFolderId ?? throw new InvalidOperationException("The test snapshot must have a selected folder."),
            "repository",
            repository,
            snapshot.RepositoryIdentityHash);

    /// <summary>
    ///     Records what the provider ASKED for. The warm restore's properties are properties of create requests and of
    ///     the command vector — not of a real build — so a recording double states them without needing a toolchain, a
    ///     network, or the several seconds a genuine restore costs. The live evidence that a restore really populates
    ///     the cache and a no-network build then succeeds lives in
    ///     <c>DevelopmentValidationReviewAndApplyTests</c>'s synthetic-solution runs.
    /// </summary>
    private sealed class RecordingSandbox : IDevelopmentSandboxRuntimeProvider
    {
        private readonly List<SandboxCreateRequest> _created = [];
        private readonly List<SandboxCommandRequest> _executed = [];
        private readonly List<SandboxHandle> _killed = [];

        public IReadOnlyList<SandboxCreateRequest> Created => _created;

        public IReadOnlyList<SandboxCommandRequest> Executed => _executed;

        public IReadOnlyList<SandboxHandle> Killed => _killed;

        /// <summary>What a warm restore exits with. Non-zero must fail the prepare rather than record a warm.</summary>
        public int RestoreExitCode { get; set; }

        public string ProviderName => "recording";

        public SandboxProviderCapabilities Capabilities =>
            SandboxProviderCapabilities.SupportsTrustedHostWorkspace
            | SandboxProviderCapabilities.SupportsNetworkPolicy
            | SandboxProviderCapabilities.SupportsKill;

        public string BaseCommit { get; set; } = string.Empty;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            _created.Add(request);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TrustedHostWorkspace!.RootPath));
            return Task.FromResult(new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = request.RuntimeProfile + "-" + _created.Count.ToString(CultureInfo.InvariantCulture),
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion,
                WorkingRoot = root,
                Mounts = [.. (request.Mounts ?? []).Select(static mount => new SandboxMountBinding(Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath)),
                    mount.ReadOnly))]
            });
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default) =>
            throw new SandboxHandleInvalidException("nothing to attach to");

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle,
            SandboxCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            _executed.Add(request);

            // The workspace invariant probes run after every catalog command: HEAD must equal the recorded base
            // commit, and symbolic-ref must fail because a detached HEAD has no branch.
            var detachedProbe = request.Arguments.Contains("symbolic-ref", StringComparer.Ordinal);
            var headProbe = request.Arguments.Contains("HEAD^{commit}", StringComparer.Ordinal);
            var exitCode = RestoreExitCode;
            if (detachedProbe)
            {
                exitCode = 1;
            }
            else if (headProbe)
            {
                exitCode = 0;
            }

            return Task.FromResult(new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = exitCode,
                StandardOutput = headProbe ? BaseCommit : string.Empty,
                Completed = true
            });
        }

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
        {
            _killed.Add(handle);
            return Task.CompletedTask;
        }
    }
}
