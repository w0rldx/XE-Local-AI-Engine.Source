namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DevelopmentGate3Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-gate3-" + Guid.NewGuid().ToString("N"));

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
    public void TrustAndPathGuards_RejectStaleAcknowledgementTraversalAndProtectedPaths()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var snapshot = Snapshot("identity", acknowledged: false, policyVersion: null, acknowledgedAt: null);
        AssertEx.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentTrustPolicy.EnsureCurrent(snapshot, TimeProvider.System));
        AssertEx.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentTrustPolicy.EnsureCurrent(snapshot with
        {
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion - 1,
            TrustedRepositoryAcknowledgedAtUtc = now
        }, TimeProvider.System));

        AssertEx.False(DevelopmentWorkspaceSecurity.Confine("../../outside", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".git/config", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".omx/ultragoal/goals.json", allowRoot: false).IsAccepted);
        AssertEx.True(DevelopmentWorkspaceSecurity.Confine("src/feature.cs", allowRoot: false).IsAccepted);
    }

    [Test]
    public async Task WorkspaceToolsAndEvidence_CreateDetachedReusableWorktreeWithFixedCommandsAndExactHashes()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical));
        DevelopmentWorkspaceSession first;

        using (var sandbox = CreateSandbox())
        {
            var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
            first = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
            var tools = new DevelopmentWorkspaceTools(sandbox, first, options);

            _ = await tools.WriteFileAsync("src/feature.txt", "bounded change\n").ConfigureAwait(false);
            AssertEx.Equal("bounded change\n", await tools.ReadFileAsync("src/feature.txt").ConfigureAwait(false));
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.WriteFileAsync("../outside.txt", "blocked"));
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.WriteFileAsync(".git/config", "blocked"));
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.RunCommandAsync("model_supplied_shell"));

            var status = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus).ConfigureAwait(false);
            AssertEx.Contains(status, "src/feature.txt", StringComparison.Ordinal);
            var evidenceService = new DevelopmentPatchEvidenceService(sandbox, options);
            var evidence = await evidenceService.ExportAsync(first).ConfigureAwait(false);
            AssertEx.Equal(first.BaseCommit, evidence.BaseCommit);
            AssertEx.NotNullOrEmpty(evidence.PatchHash);
            AssertEx.NotNullOrEmpty(evidence.ManifestHash);
            AssertEx.NotNullOrEmpty(evidence.SubjectHash);
            AssertEx.Contains(evidence.ChangedFiles, item => item.Path == "src/feature.txt" && item.ChangeType == "added");

            await sandbox.KillAsync(first.SandboxHandle).ConfigureAwait(false);
            AssertEx.True(Directory.Exists(first.HostWorktreePath), "killing a sandbox must preserve the managed Git worktree");
        }

        using var replacementSandbox = CreateSandbox();
        var replacementProvider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), replacementSandbox, options, TimeProvider.System);
        var replacement = await replacementProvider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        AssertEx.Equal(first.HostWorktreePath, replacement.HostWorktreePath);
        var replacementTools = new DevelopmentWorkspaceTools(replacementSandbox, replacement, options);
        AssertEx.Equal("bounded change\n", await replacementTools.ReadFileAsync("src/feature.txt").ConfigureAwait(false));

        var symbolic = await RunAsync(replacement.HostWorktreePath, "git", "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
        AssertEx.NotEqual(expected: 0, symbolic.ExitCode, "the managed worktree must be detached from the protected base branch");
    }

    [Test]
    public async Task CoderRunner_PersistsTypedExactEvidenceAndTerminalizesOnce()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "runner-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var canonical = DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository);
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(canonical));
        var store = Substitute.For<IDevelopmentStore>();
        store.GetExecutionSnapshotAsync(snapshot.AttemptId, Arg.Any<CancellationToken>()).Returns(snapshot);
        store.AttachArtifactAsync(Arg.Any<DevelopmentAttachArtifactCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(snapshot, call.Arg<DevelopmentAttachArtifactCommand>().ArtifactId));
        store.TerminalizeAttemptAsync(Arg.Any<DevelopmentTerminalizeAttemptCommand>(), Arg.Any<CancellationToken>())
             .Returns(call => Operation(snapshot, artifactId: null));

        var blob = Substitute.For<IDevelopmentArtifactBlobStore>();
        blob.WriteAsync(snapshot.ProjectId, Arg.Any<Guid>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var artifactId = call.ArgAt<Guid>(1);
                var content = call.ArgAt<ReadOnlyMemory<byte>>(2);
                return new DevelopmentArtifactBlobWriteResult($"{snapshot.ProjectId:N}/{artifactId:N}", "HASH-" + artifactId.ToString("N"), content.Length);
            });

        using var sandbox = CreateSandbox();
        var workspace = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var runner = new DevelopmentCoderAttemptRunner(store,
            workspace,
            sandbox,
            new DevelopmentPatchEvidenceService(sandbox, options),
            blob,
            new WritingCoderModel(),
            options);

        var result = await runner.RunAsync(snapshot.AttemptId, repository).ConfigureAwait(false);
        AssertEx.NotNullOrEmpty(result.SubjectHash);
        AssertEx.Contains(result.ChangedFiles, "feature.txt");
        _ = store.Received(4).AttachArtifactAsync(Arg.Any<DevelopmentAttachArtifactCommand>(), Arg.Any<CancellationToken>());
        _ = store.Received(1).TerminalizeAttemptAsync(Arg.Is<DevelopmentTerminalizeAttemptCommand>(command => command.Status == DevelopmentAttemptStatus.Succeeded),
            Arg.Any<CancellationToken>());
    }

    private static DevelopmentOptions OptionsValue() => new()
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

    private static DevelopmentExecutionSnapshot Snapshot(string identity,
        bool acknowledged = true,
        int? policyVersion = DevelopmentTrustPolicy.CurrentVersion,
        long? acknowledgedAt = null)
    {
        return new DevelopmentExecutionSnapshot(Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            identity,
            "main",
            DevelopmentEgressPolicy.LocalOnly,
            ConfigurationVersion: 1,
            acknowledged,
            policyVersion,
            acknowledgedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MaxTokens: 2048,
            MaxDurationSeconds: 60,
            "Implement feature",
            "Add the bounded feature file.",
            "[\"feature.txt exists\"]",
            DevelopmentTaskStatus.InProgress,
            TaskVersion: 3,
            DevelopmentAttemptRole.Coder,
            DevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1);
    }

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        EnsureSuccess(await RunAsync(repository, "git", "init", "--initial-branch=main", ".").ConfigureAwait(false));
        EnsureSuccess(await RunAsync(repository, "git", "config", "user.email", "gate3@example.invalid").ConfigureAwait(false));
        EnsureSuccess(await RunAsync(repository, "git", "config", "user.name", "Gate 3 Test").ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        EnsureSuccess(await RunAsync(repository, "git", "add", "README.md").ConfigureAwait(false));
        EnsureSuccess(await RunAsync(repository, "git", "commit", "-m", "base").ConfigureAwait(false));
        return repository;
    }

    private static ProcessSandboxRuntimeProvider CreateSandbox()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
    }

    private static async Task<CommandResult> RunAsync(string workingDirectory, string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static void EnsureSuccess(CommandResult result)
    {
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
    }

    private static DevelopmentOperationResult Operation(DevelopmentExecutionSnapshot snapshot, Guid? artifactId)
    {
        return new DevelopmentOperationResult(snapshot.ProjectId,
            snapshot.TaskId,
            snapshot.AttemptId,
            artifactId,
            Guid.NewGuid(),
            DevelopmentOperationPhases.Completed,
            "ok",
            "ok",
            Version: 1,
            Sequence: 1);
    }

    private sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class WritingCoderModel : IDevelopmentCoderModel
    {
        public async Task<DevelopmentCoderModelResult> RunAsync(string modelId,
            string prompt,
            IDevelopmentWorkspaceTools tools,
            int maxOutputTokens,
            int maxToolCalls,
            CancellationToken cancellationToken = default)
        {
            _ = await tools.WriteFileAsync("feature.txt", "implemented\n", cancellationToken).ConfigureAwait(false);
            _ = await tools.RunCommandAsync(DevelopmentCommandIds.GitStatus, cancellationToken).ConfigureAwait(false);
            return new DevelopmentCoderModelResult(new DevelopmentCoderSubmission("Implemented bounded feature.",
                    ["feature.txt"],
                    [DevelopmentCommandIds.GitStatus],
                    Notes: null),
                InputTokens: 10,
                OutputTokens: 20);
        }
    }
}
