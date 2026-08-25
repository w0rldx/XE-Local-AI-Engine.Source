namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

/// <summary>
///     G5: a repository can carry a COMMITTED credential, and the clone brings it into the sandbox.
///     <para>
///         The correction that shapes the whole design: the workspace is a <c>git clone</c>, so only TRACKED content
///         arrives — an untracked <c>.env</c> in the operator's repository never rides along. What matters is the
///         committed one, and the obvious fix for it is wrong: deleting the file from the worktree makes the tree dirty
///         against the base commit, so the diff model would see a deletion and an apply would delete the operator's
///         real file. Every assertion below therefore has a companion assertion that the worktree is unchanged.
///     </para>
/// </summary>
public sealed class DevelopmentWorkspaceSecretShadowTests : IDisposable
{
    private static readonly DevelopmentCommandProfile GenericProfile =
        DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

    private const string SecretContent = "AWS_SECRET_ACCESS_KEY=devmodesentinelvalue\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-dev-secret-" + Guid.NewGuid().ToString("N")[..12]);

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
    public async Task PrepareAsync_ShadowsEachCommittedSecretReadOnlyAtItsOwnPath_AndLeavesTheFileByteUnchanged()
    {
        var sandbox = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                           | SandboxProviderCapabilities.SupportsReadOnlyMounts
                                           | SandboxProviderCapabilities.SupportsKill);

        var (session, _) = await PrepareAsync(sandbox,
                ".env",
                "certs/server.pem",
                "deploy/.npmrc")
            .ConfigureAwait(false);

        var shadows = sandbox.Created[0].Mounts!
                             .Where(static mount => mount.TargetIsWorkspaceRelative)
                             .ToArray();
        AssertEx.Equal(expected: 3, shadows.Length, string.Join(", ", sandbox.Created[0].Mounts!.Select(static mount => mount.SandboxPath)));

        foreach (var path in new[]
                 {
                     "/.env",
                     "/certs/server.pem",
                     "/deploy/.npmrc"
                 })
        {
            var shadow = AssertEx.NotNull(shadows.FirstOrDefault(mount => string.Equals(mount.SandboxPath, path, StringComparison.Ordinal)), path);
            AssertEx.True(shadow.ReadOnly, path);

            // Engine-generated and EMPTY, and its source lives outside the workspace so the real file is not the thing
            // being mounted over itself.
            AssertEx.Equal(expected: 0, new FileInfo(shadow.HostPath).Length);
            AssertEx.False(shadow.HostPath.StartsWith(session.HostWorktreePath, StringComparison.Ordinal), shadow.HostPath);
        }

        // Two shadows must never share a mount source: a shared one would make SandboxHandle.TryResolveSandboxPath
        // answer for whichever it happened to see first.
        AssertEx.Equal(expected: 3, shadows.Select(static mount => mount.HostPath).Distinct(StringComparer.Ordinal).Count());

        // The load-bearing companion assertion. Nothing was deleted, emptied or moved on disk.
        foreach (var path in new[]
                 {
                     ".env",
                     "certs/server.pem",
                     "deploy/.npmrc"
                 })
        {
            var onDisk = Path.Combine(session.HostWorktreePath, path.Replace('/', Path.DirectorySeparatorChar));
            AssertEx.True(File.Exists(onDisk), onDisk);
            AssertEx.Equal(SecretContent, await File.ReadAllTextAsync(onDisk).ConfigureAwait(false));
        }
    }

    /// <summary>
    ///     The diff model must see nothing. A scheme that mutated the worktree would fight the transactional model:
    ///     <c>ValidatePreservedWorktreeAsync</c> and the subject hash would both register the change, and an apply
    ///     would carry it back to the operator's real repository.
    /// </summary>
    [Test]
    public async Task PrepareAsync_AfterShadowing_TheWorktreeIsStillCleanAgainstItsBaseCommit()
    {
        var sandbox = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                           | SandboxProviderCapabilities.SupportsReadOnlyMounts
                                           | SandboxProviderCapabilities.SupportsKill);

        var (session, _) = await PrepareAsync(sandbox, ".env").ConfigureAwait(false);

        var status = await ReadGitOutputAsync(session.HostWorktreePath, "status", "--porcelain").ConfigureAwait(false);
        AssertEx.Equal(string.Empty, status, "the shadow must leave the worktree byte-identical to its base commit.");
    }

    /// <summary>
    ///     On a backend with no mount layer part (2) does nothing, and the recorded event is the WHOLE control. Stated
    ///     as a test rather than only in the wiki, because "detection only" and "shadowed" look identical from the
    ///     outside and the difference is what an operator has to act on.
    /// </summary>
    [Test]
    public async Task PrepareAsync_OnABackendWithoutReadOnlyMounts_RecordsTheFindingAndRequestsNoShadowMount()
    {
        var sandbox = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                           | SandboxProviderCapabilities.SupportsKill);

        var (_, store) = await PrepareAsync(sandbox, ".env", "certs/server.pem").ConfigureAwait(false);

        AssertEx.Empty(sandbox.Created[0].Mounts!.Where(static mount => mount.ReadOnly));
        _ = store.Received(1).RecordWorkspaceSecretsAsync(Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Is<IReadOnlyList<string>>(paths => paths.Count == 2 && paths[0] == ".env" && paths[1] == "certs/server.pem"),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Above the cap the prepare is refused rather than shadowing the first 32: a partial shadow reads as a control
    ///     and is not one. The cap bounds an engine-generated mount list, so it applies only where such a list is being
    ///     generated.
    /// </summary>
    [Test]
    public async Task PrepareAsync_WhenTheRepositoryCarriesMoreSecretsThanTheCap_FailsClosedRatherThanShadowingSome()
    {
        var many = Enumerable.Range(0, 33).Select(static index => $"certs/key{index}.pem").ToArray();

        var shadowing = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                             | SandboxProviderCapabilities.SupportsReadOnlyMounts
                                             | SandboxProviderCapabilities.SupportsKill);
        var refused = await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => PrepareAsync(shadowing, many))
                                    .ConfigureAwait(false);
        AssertEx.Contains(refused.Message, "33 committed files", StringComparison.Ordinal);

        // And the same repository still prepares on a backend that shadows nothing, because there is no mount list to
        // bound there — detection alone is unbounded work the engine can afford.
        var detectionOnly = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                                 | SandboxProviderCapabilities.SupportsKill);
        var (_, store) = await PrepareAsync(detectionOnly, many).ConfigureAwait(false);
        _ = store.Received(1).RecordWorkspaceSecretsAsync(Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Is<IReadOnlyList<string>>(paths => paths.Count == 33),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A repository with no committed credential asks for no shadow and records no event. Without this the suite
    ///     would pass just as happily against a detector that flagged everything.
    /// </summary>
    [Test]
    public async Task PrepareAsync_WhenNothingCommittedLooksLikeACredential_RequestsNoShadowAndRecordsNothing()
    {
        var sandbox = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                           | SandboxProviderCapabilities.SupportsReadOnlyMounts
                                           | SandboxProviderCapabilities.SupportsKill);

        var (_, store) = await PrepareAsync(sandbox).ConfigureAwait(false);

        AssertEx.Empty(sandbox.Created[0].Mounts!.Where(static mount => mount.TargetIsWorkspaceRelative));
        _ = store.DidNotReceive().RecordWorkspaceSecretsAsync(Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     Only TRACKED content reaches the workspace, so an untracked credential in the operator's repository is not
    ///     the exposure and must not be reported as one — reporting it would train an operator to ignore the event.
    /// </summary>
    [Test]
    public async Task PrepareAsync_IgnoresAnUntrackedCredentialInTheSourceRepository()
    {
        var sandbox = new RecordingSandbox(SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                           | SandboxProviderCapabilities.SupportsReadOnlyMounts
                                           | SandboxProviderCapabilities.SupportsKill);

        var (_, store) = await PrepareAsync(sandbox, committed: [], untracked: [".env"]).ConfigureAwait(false);

        AssertEx.Empty(sandbox.Created[0].Mounts!.Where(static mount => mount.TargetIsWorkspaceRelative));
        _ = store.DidNotReceive().RecordWorkspaceSecretsAsync(Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<string>>(),
            Arg.Any<CancellationToken>());
    }

    private Task<(DevelopmentWorkspaceSession Session, IDevelopmentStore Store)> PrepareAsync(RecordingSandbox sandbox, params string[] committed) =>
        PrepareAsync(sandbox, committed, untracked: []);

    private async Task<(DevelopmentWorkspaceSession Session, IDevelopmentStore Store)> PrepareAsync(RecordingSandbox sandbox,
        string[] committed,
        string[] untracked)
    {
        Directory.CreateDirectory(_root);
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(repository);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.email", "development-secret@example.invalid").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "config", "user.name", "Development Secret Test").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        foreach (var path in committed)
        {
            var full = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, SecretContent).ConfigureAwait(false);
        }

        await DevelopmentMountBrokerTests.RunGitAsync(repository, "add", "-A", "--", ".").ConfigureAwait(false);
        await DevelopmentMountBrokerTests.RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);

        foreach (var path in untracked)
        {
            var full = Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            await File.WriteAllTextAsync(full, SecretContent).ConfigureAwait(false);
        }

        var data = Path.Combine(_root, "d-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(data);
        var identity = DevelopmentWorkspaceSecurity.RepositoryIdentityHash(DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository));
        var snapshot = Snapshot(identity);
        var store = Substitute.For<IDevelopmentStore>();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data),
            sandbox,
            Options.Create(OptionsValue()),
            TimeProvider.System,
            store);
        var session = await provider.PrepareAsync(snapshot,
                                        new DevelopmentRepositoryBinding(snapshot.ProjectId, snapshot.SelectedFolderId!.Value, "repository", repository, identity))
                                    .ConfigureAwait(false);
        return (session, store);
    }

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
            MaxChangedFiles = 64,
            MaxToolCalls = 16,
            MaxAttemptDurationSeconds = 60,
            MaxOutputTokens = 2048
        };

    private static DevelopmentExecutionSnapshot Snapshot(string identity) =>
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

            // generic-git: no restore command, so no warm sandbox and exactly one create request to inspect.
            Encoding.UTF8.GetString(GenericProfile.ToCanonicalUtf8()));

    /// <summary>Records the create request; its capability set is the only other thing about it that matters.</summary>
    private sealed class RecordingSandbox(SandboxProviderCapabilities capabilities) : IDevelopmentSandboxRuntimeProvider
    {
        private readonly List<SandboxCreateRequest> _created = [];

        public IReadOnlyList<SandboxCreateRequest> Created => _created;

        public string ProviderName => "recording";

        public SandboxProviderCapabilities Capabilities => capabilities;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            _created.Add(request);
            return Task.FromResult(new SandboxHandle
            {
                ProviderName = ProviderName,
                SandboxId = "recording-sandbox",
                AttachKey = request.AttachKey,
                CreatedAt = DateTimeOffset.UnixEpoch,
                ManifestVersion = request.AttachKey.ManifestVersion,
                Mounts = [.. (request.Mounts ?? []).Select(static mount => new SandboxMountBinding(mount.HostPath, mount.SandboxPath, mount.ReadOnly))]
            });
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default) =>
            throw new SandboxHandleInvalidException("nothing to attach to");

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("this fixture creates a sandbox and runs nothing in it.");

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
