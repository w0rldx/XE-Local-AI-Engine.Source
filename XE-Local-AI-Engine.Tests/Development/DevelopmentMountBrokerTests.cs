namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Options;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Development.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     What Development Mode asks the sandbox for, and what it does with the answer.
///     <para>
///         The load-bearing assertions here are about a path that is NOT mounted. <c>workspace.json</c> is the workspace
///         control manifest and must be unreachable from inside any sandbox; mounting the four
///         named runtime subdirectories rather than their parent is the whole of that exclusion, and it is invisible
///         unless a test says so.
///     </para>
/// </summary>
[Category(TestCategories.Integration)]
public sealed class DevelopmentMountBrokerTests : IDisposable
{
    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    /// <summary>A profile with a restore command, so <c>PrepareAsync</c> runs the warm restore.</summary>
    private static readonly DevelopmentCommandProfile RestoringProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetCsproj, "src/App/App.csproj");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-mounts-" + Guid.NewGuid().ToString("N"));

    /// <summary>The provider and binding of the last <see cref="PrepareAsync" />.</summary>
    private DevelopmentWorkspaceProvider? _provider;

    private DevelopmentRepositoryBinding? _binding;

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
    public async Task PrepareAsync_MountsTheFourRuntimeSubdirectoriesAndNotTheirParent()
    {
        var (session, _) = await PrepareAsync(CreateMappingSandbox());

        foreach (var name in new[]
                 {
                     "home",
                     "tmp",
                     "nuget",
                     "dotnet"
                 })
        {
            AssertEx.NotNull(session.SandboxHandle.TryResolveSandboxPath(Path.Combine(session.RuntimePath, name)));
        }

        // The parent itself is never a mount, which is what keeps its contents out.
        AssertEx.Empty(session.SandboxHandle.Mounts
                              .Where(mount => string.Equals(Path.TrimEndingDirectorySeparator(mount.HostPath),
                                  Path.TrimEndingDirectorySeparator(session.RuntimePath),
                                  StringComparison.Ordinal)));
    }

    [Test]
    public async Task PrepareAsync_TheWorkspaceControlManifestIsNotReachableFromInsideTheSandbox()
    {
        // Assert this boundary as the absence it is. workspace.json lives directly in RuntimePath and holds the repository
        // identity, the selected folder and the base commit the whole trust chain is anchored to.
        var (session, _) = await PrepareAsync(CreateMappingSandbox());
        var manifestPath = Path.Combine(session.RuntimePath, "workspace.json");

        AssertEx.True(File.Exists(manifestPath), "the fixture did not produce a workspace manifest, so this asserts nothing.");
        AssertEx.Null(session.SandboxHandle.TryResolveSandboxPath(manifestPath));
    }

    [Test]
    public async Task BuildEnvironment_EmitsSandboxResolvedPathsRatherThanHostPaths()
    {
        // The measured defect: every one of these was an ABSOLUTE HOST path. Under the process provider that works
        // because the child runs on the host; inside a container none of them exist and the rootfs is read-only, so
        // restore, build and test all fail. Asserting the values — not merely that some environment was passed — is
        // what distinguishes "the mapping was applied" from "nothing ran".
        var sandbox = CreateMappingSandbox();
        var (session, tools) = await PrepareAsync(sandbox);

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        var environment = AssertEx.NotNull(sandbox.Executed[0].Environment);
        AssertEx.Equal("/xe-runtime/home", environment["HOME"]);
        AssertEx.Equal("/xe-runtime/tmp", environment["TMPDIR"]);
        AssertEx.Equal("/xe-runtime/tmp", environment["TMP"]);
        AssertEx.Equal("/xe-runtime/tmp", environment["TEMP"]);
        AssertEx.Equal("/xe-runtime/nuget", environment["NUGET_PACKAGES"]);
        AssertEx.Equal("/xe-runtime/dotnet", environment["DOTNET_CLI_HOME"]);

        // Node reuse OFF, or the per-task NUGET_PACKAGES above outlives the task: a reusable MSBuild worker keeps it
        // and a later restore on the same host attaches to that worker and restores against a deleted directory.
        AssertEx.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
        AssertEx.Empty(environment.Values.Where(value => value.Contains(session.RuntimePath, StringComparison.Ordinal)));

        // /xe-runtime/tmp already fits a test-platform socket, and a host directory would not exist in this namespace.
        AssertEx.False(environment.ContainsKey("TESTINGPLATFORM_PIPE_DIRECTORY"));
    }

    [Test]
    public async Task BuildEnvironment_WhenTheIdentityMappedTempIsTooDeepForASocket_GivesTheTestPlatformAShortPrivatePipeDirectory()
    {
        // The measured defect: MTP 2.4.1 binds `{TMPDIR}/{32 hex}` as a Unix domain socket and aborts the test host (exit 134)
        // past sun_path's 103 bytes, so under the process provider's identity map every `dotnet test` died before running.
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Windows named pipes are not filesystem sockets; there is no path limit to work around.");
        }

        var sandbox = new MappingSandboxRuntimeProvider
        {
            IdentityMap = true
        };
        var (session, tools) = await PrepareAsync(sandbox);
        var hostTemporary = Path.Combine(session.RuntimePath, "tmp");
        AssertEx.True(Encoding.UTF8.GetByteCount(hostTemporary) + 33 > 103,
            "the fixture's per-task temp directory fits a socket, so this asserts nothing.");

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        var environment = AssertEx.NotNull(sandbox.Executed[0].Environment);
        AssertEx.Equal(hostTemporary, environment["TMPDIR"]);
        var pipeDirectory = environment["TESTINGPLATFORM_PIPE_DIRECTORY"];
        AssertEx.True(Encoding.UTF8.GetByteCount(pipeDirectory) + 33 <= 103, $"'{pipeDirectory}' is still too deep for a socket.");
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, sandbox.PipeDirectoryModes[0]);
        AssertEx.False(Directory.Exists(pipeDirectory), "the per-command pipe directory outlived its command.");
    }

    [Test]
    public async Task BuildEnvironment_WhenTheSandboxIsFilesystemIsolated_GivesNoHostPipeDirectory()
    {
        // Even where a handle reports host paths under isolation, the bwrap view does not bind the host system temp, so a host pipe
        // directory would name a path the child cannot see.
        var sandbox = new MappingSandboxRuntimeProvider
        {
            IdentityMap = true,
            Isolation = SandboxIsolationMode.Filesystem
        };
        var (session, tools) = await PrepareAsync(sandbox);

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        var environment = AssertEx.NotNull(sandbox.Executed[0].Environment);
        AssertEx.Equal(Path.Combine(session.RuntimePath, "tmp"), environment["TMPDIR"]);
        AssertEx.False(environment.ContainsKey("TESTINGPLATFORM_PIPE_DIRECTORY"));
    }

    [Test]
    public async Task BuildEnvironment_OnTheProcessProviderWithoutABoundary_StillEmitsTheHostPathsItAlwaysDid()
    {
        // The other direction, and the reason the identity map exists: a host without the bubblewrap chain runs Development as a host
        // child, and a translation that changed these values there would break every build on that path.
        using var sandbox = CreateBoundarylessProcessProvider();
        var (session, tools) = await PrepareAsync(sandbox);

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus);

        AssertEx.Equal(Path.Combine(session.RuntimePath, "home"),
            session.SandboxHandle.TryResolveSandboxPath(Path.Combine(session.RuntimePath, "home")));
        AssertEx.Equal(Path.Combine(session.RuntimePath, "nuget"),
            session.SandboxHandle.TryResolveSandboxPath(Path.Combine(session.RuntimePath, "nuget")));
    }

    [Test]
    public async Task PrepareAsync_RequestsTheReadOnlyGitConfigMountOnlyFromAProviderThatCanServeIt()
    {
        // Capability-gated on purpose. A provider with no mount layer fails a read-only request CLOSED rather than
        // serving it writable, so an unconditional request would kill Development Mode outright on the process
        // provider — which is exactly why the engine-side config rewrite is the provider-independent half.
        var mapping = CreateMappingSandbox();
        var (mapped, _) = await PrepareAsync(mapping);
        AssertEx.Contains(mapped.SandboxHandle.Mounts,
            mount => mount.ReadOnly && mount.SandboxPath.EndsWith(".git/config", StringComparison.Ordinal));

        using var process = CreateBoundarylessProcessProvider();
        var (plain, _) = await PrepareAsync(process);
        AssertEx.Empty(plain.SandboxHandle.Mounts.Where(static mount => mount.ReadOnly));
    }

    /// <summary>
    ///     Behind the bubblewrap chain the coder's commands write the workspace and their own runtime directories and nothing else: not
    ///     the host beside the jail, not the package folder validation trusts, not <c>.git/config</c>.
    /// </summary>
    [Test]
    [Category(TestCategories.ExternalInfra)]
    public async Task PrepareAsync_BehindTheBubblewrapChain_ConfinesWritesToTheWorkspaceAndItsRuntimeDirectories()
    {
        using var sandbox = RequireBoundaryProcessProvider();
        var (session, _) = await PrepareAsync(sandbox);
        // Under the account's home, not this test's /tmp root: inside the chain /tmp is the sandbox's own private directory, where the
        // package folder's bind already created the test root's name.
        var outside = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".xe-dev-boundary-" + Guid.NewGuid().ToString("N"));
        var nuget = Path.Combine(session.RuntimePath, "nuget");

        AssertEx.Equal(SandboxIsolationMode.Filesystem, session.SandboxHandle.Isolation);
        AssertEx.Equal("/work", session.SandboxHandle.TryResolveSandboxPath(session.HostWorktreePath));
        AssertEx.Equal("/xe-runtime/home", session.SandboxHandle.TryResolveSandboxPath(Path.Combine(session.RuntimePath, "home")));
        // The package folder keeps its host path, so obj/ written by the warm restore (a host child) still resolves.
        AssertEx.Equal(nuget, session.SandboxHandle.TryResolveSandboxPath(nuget));

        var result = await sandbox.ExecuteAsync(session.SandboxHandle, new SandboxCommandRequest
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            Executable = "/bin/sh",
            Arguments =
            [
                "-c",
                $"""
                 if touch '{outside}' 2>/dev/null; then echo OUTSIDE=WRITTEN; else echo OUTSIDE=REFUSED; fi
                 if touch '{nuget}/planted' 2>/dev/null; then echo NUGET=WRITTEN; else echo NUGET=REFUSED; fi
                 if echo x >> /work/.git/config 2>/dev/null; then echo GITCONFIG=WRITTEN; else echo GITCONFIG=REFUSED; fi
                 if touch /xe-runtime/home/ok 2>/dev/null; then echo HOME=WRITTEN; else echo HOME=REFUSED; fi
                 if touch /work/inside.txt 2>/dev/null; then echo WORK=WRITTEN; else echo WORK=REFUSED; fi
                 """
            ],
            WorkingDirectory = "/",
            Timeout = TimeSpan.FromSeconds(30)
        });

        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
        AssertEx.Contains(result.StandardOutput, "OUTSIDE=REFUSED");
        AssertEx.Contains(result.StandardOutput, "NUGET=REFUSED");
        AssertEx.Contains(result.StandardOutput, "GITCONFIG=REFUSED");
        AssertEx.Contains(result.StandardOutput, "HOME=WRITTEN");
        AssertEx.Contains(result.StandardOutput, "WORK=WRITTEN");
        var escaped = File.Exists(outside);
        File.Delete(outside);
        AssertEx.False(escaped, "a sandboxed write outside the jail reached the host");
        AssertEx.False(File.Exists(Path.Combine(nuget, "planted")), "a sandboxed write reached the read-only package folder");
        AssertEx.True(File.Exists(Path.Combine(session.HostWorktreePath, "inside.txt")), "the workspace must be the jail the chain binds at /work");
    }

    /// <summary>
    ///     The coder's sandbox wrote the package folder, and validation trusted every package in it that had a
    ///     <c>.nupkg.metadata</c>. Only the warm restore, which runs the base commit alone, may write it now.
    /// </summary>
    [Test]
    public async Task PrepareAsync_MountsThePackageFolderReadOnlyForEverySandboxButTheWarmRestore()
    {
        var sandbox = CreateMappingSandbox();
        var (session, _) = await PrepareAsync(sandbox, RestoringProfile);
        var nuget = Path.Combine(session.RuntimePath, "nuget");

        AssertEx.Equal("development-warm:writable,development-local:read-only",
            string.Join(',', sandbox.Created.Select(request => request.RuntimeProfile + ":" + PackageFolderMode(request, nuget))));
    }

    /// <summary>
    ///     The warm restore runs with egress, so it must not read a NuGet.Config or MSBuild import a coder left in the
    ///     task's HOME: it gets home, tmp and dotnet directories of its own, removed afterwards.
    /// </summary>
    [Test]
    public async Task PrepareAsync_RunsTheWarmRestoreUnderFreshDirectories()
    {
        var sandbox = CreateMappingSandbox();
        var (session, _) = await PrepareAsync(sandbox, RestoringProfile);
        var warm = sandbox.Created.Single(static request => request.RuntimeProfile == "development-warm");

        foreach (var name in new[] { "home", "tmp", "dotnet" })
        {
            var mount = warm.Mounts!.Single(mount => mount.SandboxPath == "/xe-runtime/" + name);
            AssertEx.NotEqual(Path.Combine(session.RuntimePath, name), mount.HostPath);
            AssertEx.False(Directory.Exists(mount.HostPath), $"the warm restore's {name} directory outlived it");
        }
    }

    /// <summary>
    ///     Validation's home, tmp and dotnet directories are fresh, outside the tree, and its package folder is the
    ///     task's warm one mounted read-only.
    /// </summary>
    [Test]
    public async Task PrepareValidationAsync_GivesFreshDirectoriesAndAReadOnlyPackageFolder()
    {
        var sandbox = CreateMappingSandbox();
        var (session, _) = await PrepareAsync(sandbox);
        await File.WriteAllTextAsync(Path.Combine(session.RuntimePath, "home", ".gitconfig"), "[core]\n\twhitespace = -blank-at-eol\n");
        await File.WriteAllTextAsync(Path.Combine(session.HostWorktreePath, "feature.txt"), "implemented\n");
        var evidence = await new DevelopmentPatchEvidenceService(Options.Create(OptionsValue())).ExportAsync(session);

        var tree = await _provider!.PrepareValidationAsync(session, _binding!, evidence.PatchBytes);
        _ = await new DevelopmentWorkspaceTools(sandbox, tree, Options.Create(OptionsValue()), GenericProfile).RunCommandAsync(DevelopmentCommandIds.GitStatus);

        var request = sandbox.Created[^1];
        AssertEx.Equal("development-validation", request.RuntimeProfile);
        AssertEx.Equal("read-only", PackageFolderMode(request, Path.Combine(session.RuntimePath, "nuget")));
        foreach (var name in new[] { "home", "tmp", "dotnet" })
        {
            var hostPath = request.Mounts!.Single(mount => mount.SandboxPath == "/xe-runtime/" + name).HostPath;
            AssertEx.NotEqual(Path.Combine(session.RuntimePath, name), hostPath);
            AssertEx.False(hostPath.StartsWith(tree.HostWorktreePath, StringComparison.Ordinal), $"validation's {name} directory is inside the tree it judges");
            AssertEx.Empty(Directory.EnumerateFileSystemEntries(hostPath));
        }

        var environment = AssertEx.NotNull(sandbox.Executed[^1].Environment);
        AssertEx.Equal("/dev/null", environment["GIT_CONFIG_GLOBAL"]);
        AssertEx.Equal("1", environment["GIT_CONFIG_NOSYSTEM"]);
        AssertEx.Equal("1", environment["GIT_ATTR_NOSYSTEM"]);
    }

    /// <summary>A boundary no command can get (a writable runtime bind turned group-writable) stops the task as runner_not_installed.</summary>
    /// <remarks>
    ///     Every validation command keeps its launch-failure evidence; the workspace invariant probes used to fail through the same
    ///     boundary and throw before any evidence was kept.
    /// </remarks>
    [Test]
    [Category(TestCategories.ExternalInfra)]
    [UnsupportedOSPlatform("windows")]
    public async Task ACommandWhoseBoundaryCannotBeEstablished_KeepsItsEvidence_AndStopsTheTaskAsRunnerNotInstalled()
    {
        using var sandbox = RequireBoundaryProcessProvider();
        var (session, tools) = await PrepareAsync(sandbox);
        var home = Path.Combine(session.RuntimePath, "home");
        File.SetUnixFileMode(home, File.GetUnixFileMode(home) | UnixFileMode.GroupWrite);

        foreach (var commandId in GenericProfile.ValidationCommandIds)
        {
            _ = await tools.RunCommandAsync(commandId);
        }

        AssertEx.Equal(GenericProfile.ValidationCommandIds.Count, tools.CommandEvidence.Count);
        AssertEx.True(tools.CommandEvidence.All(static evidence => evidence.StandardError.StartsWith(SandboxCommandResult.BoundaryFailurePrefix, StringComparison.Ordinal)),
            string.Join(" | ", tools.CommandEvidence.Select(static evidence => evidence.StandardError)));
        var verdict = DevelopmentValidationVerdict.Evaluate(GenericProfile, tools.CommandEvidence);
        AssertEx.Equal(DevelopmentValidationFailureCodes.RunnerNotInstalled, verdict.FailureCode);
        AssertEx.True(verdict.StopsTask);
    }

    /// <summary>Only an engine-composed "nothing started" result skips the invariant probes; the wrapper-rewritten form came from a child.</summary>
    [Test]
    public async Task OnlyAnEngineComposedLaunchFailure_CountsAsNeverStarted()
    {
        static SandboxCommandResult Failed(string error) => new() { ExecutionId = "x", ExitCode = -1, Completed = false, StandardError = error };

        AssertEx.True(DevelopmentWorkspaceTools.NeverStarted(Failed(SandboxCommandResult.BoundaryFailurePrefix + " ('/x' is writable by group or world).")));
        AssertEx.True(DevelopmentWorkspaceTools.NeverStarted(Failed(SandboxCommandResult.LaunchFailurePrefix + ".")));
        AssertEx.False(DevelopmentWorkspaceTools.NeverStarted(Failed(SandboxCommandResult.LaunchFailurePrefix + " (bwrap: execvp dotnet: No such file or directory).")),
            "a running workload can print the wrapper's line, so it must not be able to skip the invariant with it.");
        AssertEx.False(DevelopmentWorkspaceTools.NeverStarted(Failed("Command timed out.")));

        await Task.CompletedTask;
    }

    private static string PackageFolderMode(SandboxCreateRequest request, string packageFolder) =>
        request.Mounts!.Single(mount => string.Equals(mount.HostPath, packageFolder, StringComparison.Ordinal)).ReadOnly ? "read-only" : "writable";

    private async Task<(DevelopmentWorkspaceSession Session, DevelopmentWorkspaceTools Tools)> PrepareAsync(IDevelopmentSandboxRuntimeProvider sandbox,
        DevelopmentCommandProfile? profile = null)
    {
        var repository = await CreateRepositoryAsync();
        var data = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)), profile ?? GenericProfile);
        if (sandbox is MappingSandboxRuntimeProvider mapping)
        {
            // Before the prepare, whose warm restore already runs the workspace invariant probes.
            mapping.BaseCommit = (await File.ReadAllTextAsync(Path.Combine(repository, ".git", "refs", "heads", "main"))).Trim();
        }

        _provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink(),
            StubNodeRuntimeSettings.Create().Build());
        _binding = Binding(snapshot, repository);
        var session = await _provider.PrepareAsync(snapshot, _binding);
        return (session, new DevelopmentWorkspaceTools(sandbox, session, options, profile ?? GenericProfile));
    }

    private static MappingSandboxRuntimeProvider CreateMappingSandbox()
    {
        return new MappingSandboxRuntimeProvider();
    }

    // The process provider on a host that measured no boundary, whatever this host has: the shape every non-Linux host gets.
    private static ProcessSandboxRuntimeProvider CreateBoundarylessProcessProvider() =>
        new(Options.Create(new LocalContainerOptions()), TimeProvider.System, launcher: new SandboxLauncher(new NoContainmentProbe()));

    // The real process provider, skipped visibly where this host cannot run the chain; a present bwrap that cannot isolate fails
    // under the opt-in live switch, as the sandbox live suite does.
    private static ProcessSandboxRuntimeProvider RequireBoundaryProcessProvider()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new SkipTestException("the bubblewrap chain is Linux-only.");
        }

        if (TrustedBinaryResolver.Resolve("bwrap") is null)
        {
            throw new SkipTestException("this host has no root-owned bwrap under /usr/bin, /bin or /usr/local/bin.");
        }

        var provider = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        if (!provider.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation))
        {
            var reason = provider.FilesystemIsolationUnavailableReason;
            provider.Dispose();
            AssertEx.False(string.Equals(Environment.GetEnvironmentVariable("XE_COMPUTE_LIVE"), "1", StringComparison.Ordinal),
                $"this host has a trusted bwrap, so the filesystem boundary must hold; the probe reported: {reason}");
            throw new SkipTestException($"this host's bwrap cannot isolate here: {reason}");
        }

        return provider;
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

    private static DevelopmentExecutionSnapshot Snapshot(string identity, DevelopmentCommandProfile profile)
    {
        return new DevelopmentExecutionSnapshot
        {
            ProjectId = Guid.NewGuid(),
            TaskId = Guid.NewGuid(),
            AttemptId = Guid.NewGuid(),
            SelectedFolderId = Guid.NewGuid(),
            RepositoryIdentityHash = identity,
            BaseBranch = "main",
            EgressPolicy = DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion = 1,
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion,
            TrustedRepositoryAcknowledgedAtUtc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens = 2048,
            MaxDurationSeconds = 60,
            Title = "Implement feature",
            Requirements = "Add the bounded feature file.",
            AcceptanceCriteriaJson = "[\"feature.txt exists\"]",
            TaskStatus = DevelopmentTaskStatus.InProgress,
            TaskVersion = 3,
            AttemptRole = DevelopmentAttemptRole.Coder,
            AttemptStatus = PersistenceDevelopmentAttemptStatus.Running,
            ModelId = "local-model",
            Provider = "local",
            AttemptVersion = 1,
            CommandProfileJson = Encoding.UTF8.GetString(profile.ToCanonicalUtf8())
        };
    }

    private static DevelopmentRepositoryBinding Binding(DevelopmentExecutionSnapshot snapshot, string repository) =>
        new()
        {
            ProjectId = snapshot.ProjectId,
            SelectedFolderId = snapshot.SelectedFolderId ?? throw new InvalidOperationException("The test snapshot must have a selected folder."),
            Alias = "repository",
            RepositoryRoot = repository,
            RepositoryIdentityHash = snapshot.RepositoryIdentityHash
        };

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "--initial-branch=main", ".");
        await RunGitAsync(repository, "config", "user.email", "development-mounts@example.invalid");
        await RunGitAsync(repository, "config", "user.name", "Development Mount Test");
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n");
        await RunGitAsync(repository, "add", "README.md");
        await RunGitAsync(repository, "commit", "-m", "base");
        return repository;
    }

    internal static async Task RunGitAsync(string workingDirectory, params string[] arguments)
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
        _ = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        AssertEx.Equal(expected: 0, process.ExitCode, await error);
    }

    /// <summary>
    ///     A provider that really MAPS: the workspace becomes <c>/workspace</c> and every other mount lands at the
    ///     target it asked for. It exists because neither shipping provider can prove the translation — the process
    ///     provider identity-maps by contract, so a test built on it cannot tell a correct mapping from no mapping at
    ///     all, and the container provider needs a daemon.
    /// </summary>
    private sealed class NoContainmentProbe : ISandboxContainmentProbe
    {
        public SandboxContainment Containment => SandboxContainment.None;
    }

    private sealed class MappingSandboxRuntimeProvider : IDevelopmentSandboxRuntimeProvider
    {
        private const string WorkspaceTarget = "/workspace";

        private readonly List<SandboxCommandRequest> _executed = [];
        private SandboxHandle? _handle;

        public IReadOnlyList<SandboxCommandRequest> Executed => _executed;

        public List<SandboxCreateRequest> Created { get; } = [];

        /// <summary>Maps every mount onto its own host path, as the process provider does by contract.</summary>
        public bool IdentityMap { get; init; }

        /// <summary>The isolation the handle reports as delivered.</summary>
        public SandboxIsolationMode Isolation { get; init; }

        /// <summary>The mode of each command's TESTINGPLATFORM_PIPE_DIRECTORY, read while the command ran; null when absent.</summary>
        public List<UnixFileMode?> PipeDirectoryModes { get; } = [];

        public string ProviderName => "mapping";

        public SandboxProviderCapabilities Capabilities =>
            SandboxProviderCapabilities.SupportsTrustedHostWorkspace
            | SandboxProviderCapabilities.SupportsReadOnlyMounts
            | SandboxProviderCapabilities.SupportsCopyInto
            | SandboxProviderCapabilities.SupportsCopyOut
            | SandboxProviderCapabilities.SupportsKill;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            Created.Add(request);
            var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TrustedHostWorkspace!.RootPath));
            var mounts = new List<SandboxMountBinding>
            {
                new()
                {
                    HostPath = workspaceRoot,
                    SandboxPath = IdentityMap ? workspaceRoot : WorkspaceTarget,
                    ReadOnly = false
                }
            };
            mounts.AddRange((request.Mounts ?? []).Select(mount =>
            {
                var hostPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath));
                var prefix = workspaceRoot + Path.DirectorySeparatorChar;
                var target = hostPath.StartsWith(prefix, StringComparison.Ordinal)
                    ? WorkspaceTarget + "/" + hostPath[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/')
                    : mount.SandboxPath;
                return new SandboxMountBinding
                {
                    HostPath = hostPath,
                    SandboxPath = IdentityMap ? hostPath : target,
                    ReadOnly = mount.ReadOnly
                };
            }));

            _handle = new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = "mapping-sandbox",
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = mounts,
                Isolation = Isolation
            };

            return Task.FromResult(_handle);
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default) =>
            _handle is null ? throw new SandboxHandleInvalidException("nothing to attach to") : Task.FromResult(_handle);

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle,
            SandboxCommandRequest request,
            CancellationToken cancellationToken = default)
        {
            _executed.Add(request);
            PipeDirectoryModes.Add(request.Environment?.TryGetValue("TESTINGPLATFORM_PIPE_DIRECTORY", out var pipeDirectory) == true
                                   && !OperatingSystem.IsWindows()
                ? File.GetUnixFileMode(pipeDirectory)
                : null);

            // The workspace invariant probes run after EVERY catalog command: HEAD must equal the recorded base commit,
            // and symbolic-ref must fail (a detached HEAD has no branch). Answering both correctly is what keeps these
            // tests failing for the reason they assert rather than for a fixture artefact.
            var detachedProbe = request.Arguments.Contains("symbolic-ref", StringComparer.Ordinal);
            return Task.FromResult(new SandboxCommandResult
            {
                ExecutionId = request.ExecutionId,
                ExitCode = detachedProbe ? 1 : 0,
                StandardOutput = request.Arguments.Contains("HEAD^{commit}", StringComparer.Ordinal) ? BaseCommit : string.Empty,
                Completed = true
            });
        }

        /// <summary>Set by the fixture so the workspace invariant probe answers with the session's real base commit.</summary>
        public string BaseCommit { get; set; } = string.Empty;

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
