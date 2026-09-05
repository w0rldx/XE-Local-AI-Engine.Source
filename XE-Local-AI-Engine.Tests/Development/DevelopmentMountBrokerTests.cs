namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
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
public sealed class DevelopmentMountBrokerTests : IDisposable
{
    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-mounts-" + Guid.NewGuid().ToString("N"));

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
        var (session, _) = await PrepareAsync(CreateMappingSandbox()).ConfigureAwait(false);

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
        var (session, _) = await PrepareAsync(CreateMappingSandbox()).ConfigureAwait(false);
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
        var (session, tools) = await PrepareAsync(sandbox).ConfigureAwait(false);

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus).ConfigureAwait(false);

        var environment = AssertEx.NotNull(sandbox.Executed[0].Environment);
        AssertEx.Equal("/xe-runtime/home", environment["HOME"]);
        AssertEx.Equal("/xe-runtime/tmp", environment["TMPDIR"]);
        AssertEx.Equal("/xe-runtime/tmp", environment["TMP"]);
        AssertEx.Equal("/xe-runtime/tmp", environment["TEMP"]);
        AssertEx.Equal("/xe-runtime/nuget", environment["NUGET_PACKAGES"]);
        AssertEx.Equal("/xe-runtime/dotnet", environment["DOTNET_CLI_HOME"]);

        // Node reuse OFF, or the per-task NUGET_PACKAGES above outlives the task: a reusable MSBuild worker keeps it
        // and a later restore on this box attaches to that worker and restores against a deleted directory.
        AssertEx.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
        AssertEx.Empty(environment.Values.Where(value => value.Contains(session.RuntimePath, StringComparison.Ordinal)));
    }

    [Test]
    public async Task BuildEnvironment_OnTheProcessProvider_StillEmitsTheHostPathsItAlwaysDid()
    {
        // The other direction, and the reason the identity map exists: Development runs on the process provider today,
        // and a translation that changed these values there would break every build on the currently shipping path.
        using var sandbox = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        var (session, tools) = await PrepareAsync(sandbox).ConfigureAwait(false);

        _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus).ConfigureAwait(false);

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
        var (mapped, _) = await PrepareAsync(mapping).ConfigureAwait(false);
        AssertEx.Contains(mapped.SandboxHandle.Mounts,
            mount => mount.ReadOnly && mount.SandboxPath.EndsWith(".git/config", StringComparison.Ordinal));

        using var process = new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
        var (plain, _) = await PrepareAsync(process).ConfigureAwait(false);
        AssertEx.Empty(plain.SandboxHandle.Mounts.Where(static mount => mount.ReadOnly));
    }

    private async Task<(DevelopmentWorkspaceSession Session, DevelopmentWorkspaceTools Tools)> PrepareAsync(IDevelopmentSandboxRuntimeProvider sandbox)
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System, new RecordingWorkspaceSecretsSink());
        var session = await provider.PrepareAsync(snapshot, Binding(snapshot, repository)).ConfigureAwait(false);
        if (sandbox is MappingSandboxRuntimeProvider mapping)
        {
            mapping.BaseCommit = session.BaseCommit;
        }

        return (session, new DevelopmentWorkspaceTools(sandbox, session, options, GenericProfile));
    }

    private static MappingSandboxRuntimeProvider CreateMappingSandbox()
    {
        return new MappingSandboxRuntimeProvider();
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

    private static DevelopmentExecutionSnapshot Snapshot(string identity)
    {
        return new DevelopmentExecutionSnapshot(Guid.NewGuid(),
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
            Encoding.UTF8.GetString(GenericProfile.ToCanonicalUtf8()));
    }

    private static DevelopmentRepositoryBinding Binding(DevelopmentExecutionSnapshot snapshot, string repository) =>
        new(snapshot.ProjectId,
            snapshot.SelectedFolderId ?? throw new InvalidOperationException("The test snapshot must have a selected folder."),
            "repository",
            repository,
            snapshot.RepositoryIdentityHash);

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await RunGitAsync(repository, "config", "user.email", "development-mounts@example.invalid").ConfigureAwait(false);
        await RunGitAsync(repository, "config", "user.name", "Development Mount Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        await RunGitAsync(repository, "add", "README.md").ConfigureAwait(false);
        await RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);
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
        _ = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        AssertEx.Equal(expected: 0, process.ExitCode, await error.ConfigureAwait(false));
    }

    /// <summary>
    ///     A provider that really MAPS: the workspace becomes <c>/workspace</c> and every other mount lands at the
    ///     target it asked for. It exists because neither shipping provider can prove the translation — the process
    ///     provider identity-maps by contract, so a test built on it cannot tell a correct mapping from no mapping at
    ///     all, and the container provider needs a daemon.
    /// </summary>
    private sealed class MappingSandboxRuntimeProvider : IDevelopmentSandboxRuntimeProvider
    {
        private const string WorkspaceTarget = "/workspace";

        private readonly List<SandboxCommandRequest> _executed = [];
        private SandboxHandle? _handle;

        public IReadOnlyList<SandboxCommandRequest> Executed => _executed;

        public string ProviderName => "mapping";

        public SandboxProviderCapabilities Capabilities =>
            SandboxProviderCapabilities.SupportsTrustedHostWorkspace
            | SandboxProviderCapabilities.SupportsReadOnlyMounts
            | SandboxProviderCapabilities.SupportsCopyInto
            | SandboxProviderCapabilities.SupportsCopyOut
            | SandboxProviderCapabilities.SupportsKill;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.TrustedHostWorkspace!.RootPath));
            var mounts = new List<SandboxMountBinding>
            {
                new(workspaceRoot, WorkspaceTarget, ReadOnly: false)
            };
            mounts.AddRange((request.Mounts ?? []).Select(mount =>
            {
                var hostPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(mount.HostPath));
                var prefix = workspaceRoot + Path.DirectorySeparatorChar;
                var target = hostPath.StartsWith(prefix, StringComparison.Ordinal)
                    ? WorkspaceTarget + "/" + hostPath[prefix.Length..].Replace(Path.DirectorySeparatorChar, '/')
                    : mount.SandboxPath;
                return new SandboxMountBinding(hostPath, target, mount.ReadOnly);
            }));

            _handle = new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = "mapping-sandbox",
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = mounts
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
