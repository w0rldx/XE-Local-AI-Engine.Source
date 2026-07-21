namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using PersistenceDevelopmentAttemptStatus = XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentAttemptStatus;

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
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentTrustPolicy.EnsureCurrent(snapshot, TimeProvider.System));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentTrustPolicy.EnsureCurrent(snapshot with
        {
            TrustedRepositoryAcknowledged = true,
            TrustedRepositoryPolicyVersion = DevelopmentTrustPolicy.CurrentVersion - 1,
            TrustedRepositoryAcknowledgedAtUtc = now
        }, TimeProvider.System));

        AssertEx.False(DevelopmentWorkspaceSecurity.Confine("../../outside", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".git/config", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".GIT/config", allowRoot: false).IsAccepted);
        AssertEx.False(DevelopmentWorkspaceSecurity.Confine(".omx/ultragoal/goals.json", allowRoot: false).IsAccepted);
        AssertEx.True(DevelopmentWorkspaceSecurity.Confine("src/feature.cs", allowRoot: false).IsAccepted);
    }

    [Test]
    public async Task ApplyPatch_WhenRenameHeaderTargetsProtectedPath_RejectsWithoutMutation()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "protected-rename-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(
            DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var session = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options);
        const string patch = """
                             diff --git a/README.md b/README.md
                             similarity index 100%
                             rename from README.md
                             rename to .omx/ultragoal/VERIFIER_SENTINEL
                             """ + "\n";

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.ApplyPatchAsync(patch));
        AssertEx.True(File.Exists(Path.Combine(session.HostWorktreePath, "README.md")));
        AssertEx.False(File.Exists(Path.Combine(session.HostWorktreePath, ".omx", "ultragoal", "VERIFIER_SENTINEL")));
    }

    [Test]
    public async Task ApplyPatch_WhenAnyExtendedHeaderTargetsProtectedPath_RejectsWholePatch()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "protected-headers-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(
            DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var session = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options);
        string[] patches =
        [
            "diff --git a/README.md b/README.md\nsimilarity index 100%\nrename from .git/config\nrename to README.md\n",
            "diff --git a/README.md b/README.md\nsimilarity index 100%\ncopy from README.md\ncopy to .git/config\n",
            "diff --git a/README.md b/README.md\n--- a/.git/config\n+++ b/README.md\n@@ -1 +1 @@\n-base\n+changed\n",
            "diff --git a/README.md b/README.md\n--- a/README.md\n+++ b/.git/config\n@@ -1 +1 @@\n-base\n+changed\n"
        ];

        foreach (var patch in patches)
        {
            await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => tools.ApplyPatchAsync(patch));
        }

        AssertEx.Equal("base\n", await File.ReadAllTextAsync(Path.Combine(session.HostWorktreePath, "README.md")).ConfigureAwait(false));
    }

    [Test]
    public async Task ApplyPatch_WhenChangedFileExceedsWriteBound_RejectsWithoutMutation()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "patch-write-bound-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue(maxFileWriteBytes: 16));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(
            DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var session = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options);
        const string patch = """
                             diff --git a/large.txt b/large.txt
                             new file mode 100644
                             index 0000000..ae52be8
                             --- /dev/null
                             +++ b/large.txt
                             @@ -0,0 +1 @@
                             +0123456789abcdefg
                             """ + "\n";

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => tools.ApplyPatchAsync(patch));
        AssertEx.False(File.Exists(Path.Combine(session.HostWorktreePath, "large.txt")));
    }

    [Test]
    public async Task ReadFile_WhenFileExceedsReadBound_FailsClosed()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "read-bound-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue(maxCommandOutputBytes: 16, maxFileWriteBytes: 64));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(
            DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var session = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options);
        _ = await tools.WriteFileAsync("large.txt", "0123456789abcdefg").ConfigureAwait(false);

        await AssertEx.ThrowsAsync<InvalidDataException>(() => tools.ReadFileAsync("large.txt"));
    }

    [Test]
    public async Task CoderModel_WhenModelIsUnknown_RejectsBeforeTransport()
    {
        using var chat = new ThrowingChatClient();
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("unknown-model").Returns(false);
        var model = new DevelopmentCoderModel(chat, cloud, LocalModelResolver());

        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => model.RunAsync("unknown-model",
            "prompt",
            new NullWorkspaceTools(),
            maxOutputTokens: 100,
            maxToolCalls: 2));
        AssertEx.Equal(expected: 0, chat.CallCount);
    }

    [Test]
    public async Task CoderModel_ReportsUsageAndRejectsAggregateTokenCapPlusOne()
    {
        var cloud = Substitute.For<IActiveCloudChatClientFactory>();
        cloud.IsCloudProviderSelected("local-model").Returns(false);
        var tools = new NullWorkspaceTools();
        var resolver = LocalModelResolver(new LocalModelDescriptor
        {
            ModelName = "local-model",
            ProviderName = "local",
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = 4096,
            IsToolCapable = true
        });

        using var exactChat = new SubmittingChatClient(inputTokens: 40, outputTokens: 60);
        var exact = new DevelopmentCoderModel(exactChat, cloud, resolver);
        var result = await exact.RunAsync("local-model", "prompt", tools, maxOutputTokens: 100, maxToolCalls: 2).ConfigureAwait(false);
        AssertEx.Equal<long?>(40, result.InputTokens);
        AssertEx.Equal<long?>(60, result.OutputTokens);

        using var overChat = new SubmittingChatClient(inputTokens: 40, outputTokens: 61);
        var over = new DevelopmentCoderModel(overChat, cloud, resolver);
        await AssertEx.ThrowsAsync<InvalidOperationException>(() => over.RunAsync("local-model",
            "prompt",
            tools,
            maxOutputTokens: 100,
            maxToolCalls: 2));
    }

    [Test]
    [NotInParallel]
    public async Task EvidenceExport_WhenAlreadyCancelled_DoesNotStartGit()
    {
        if (!OperatingSystem.IsLinux())
        {
            Skip.Test("The executable PATH probe uses a Linux shell script.");
            return;
        }

        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var runtime = Path.Combine(_root, "cancel-runtime");
        var fakeBin = Path.Combine(_root, "fake-bin");
        var marker = Path.Combine(_root, "git-started");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(fakeBin);
        var fakeGit = Path.Combine(fakeBin, "git");
        await File.WriteAllTextAsync(fakeGit, $"#!/bin/sh\ntouch '{marker}'\nsleep 5\n").ConfigureAwait(false);
        File.SetUnixFileMode(fakeGit,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", fakeBin + Path.PathSeparator + originalPath);
            using var sandbox = CreateSandbox();
            var service = new DevelopmentPatchEvidenceService(sandbox, Options.Create(OptionsValue()));
            var session = new DevelopmentWorkspaceSession(Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "base",
                "identity",
                repository,
                runtime,
                new SandboxHandle
                {
                    ProviderName = "process",
                    SandboxId = Guid.NewGuid().ToString("N"),
                    AttachKey = new SandboxAttachKey
                    {
                        OwnerUserId = "development",
                        NodeId = "cancel",
                        ProviderName = "process",
                        RuntimeProfile = "development-local",
                        ManifestVersion = 1
                    },
                    CreatedAt = DateTimeOffset.UtcNow,
                    ManifestVersion = 1
                });
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync().ConfigureAwait(false);

            await AssertEx.ThrowsAsync<OperationCanceledException>(() => service.ExportAsync(session, cancelled.Token));
            AssertEx.False(File.Exists(marker), "an already-cancelled export must not start the first Git process");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
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
        var protectedBefore = await RunProcessAsync(repository, "git", "rev-parse", "refs/heads/main").ConfigureAwait(false);
        EnsureSuccess(protectedBefore);
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
            var replayEvidence = await evidenceService.ExportAsync(first).ConfigureAwait(false);
            AssertEx.Equal(evidence.PatchHash, replayEvidence.PatchHash);
            AssertEx.Equal(evidence.ManifestHash, replayEvidence.ManifestHash);
            AssertEx.Equal(evidence.SubjectHash, replayEvidence.SubjectHash);

            await sandbox.KillAsync(first.SandboxHandle).ConfigureAwait(false);
            AssertEx.True(Directory.Exists(first.HostWorktreePath), "killing a sandbox must preserve the managed Git worktree");
        }

        using var replacementSandbox = CreateSandbox();
        var replacementProvider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), replacementSandbox, options, TimeProvider.System);
        var replacement = await replacementProvider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        AssertEx.Equal(first.HostWorktreePath, replacement.HostWorktreePath);
        var replacementTools = new DevelopmentWorkspaceTools(replacementSandbox, replacement, options);
        AssertEx.Equal("bounded change\n", await replacementTools.ReadFileAsync("src/feature.txt").ConfigureAwait(false));

        var symbolic = await RunProcessAsync(replacement.HostWorktreePath, "git", "symbolic-ref", "--quiet", "HEAD").ConfigureAwait(false);
        AssertEx.NotEqual(notExpected: 0, symbolic.ExitCode, "the managed worktree must be detached from the protected base branch");
        var protectedAfter = await RunProcessAsync(repository, "git", "rev-parse", "refs/heads/main").ConfigureAwait(false);
        EnsureSuccess(protectedAfter);
        AssertEx.Equal(protectedBefore.StandardOutput.Trim(), protectedAfter.StandardOutput.Trim());
    }

    [Test]
    public async Task WorkspaceProvider_RejectsPreservedWorktreeWhoseHeadNoLongerMatchesPersistedBase()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "tampered-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue());
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(
            DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));
        DevelopmentWorkspaceSession session;

        using (var sandbox = CreateSandbox())
        {
            var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
            session = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
            EnsureSuccess(await RunProcessAsync(session.HostWorktreePath, "git", "commit", "--allow-empty", "-m", "unexpected-head").ConfigureAwait(false));
            await sandbox.KillAsync(session.SandboxHandle).ConfigureAwait(false);
        }

        using var replacementSandbox = CreateSandbox();
        var replacement = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), replacementSandbox, options, TimeProvider.System);
        await AssertEx.ThrowsAsync<DevelopmentWorkspaceSecurityException>(() => replacement.PrepareAsync(snapshot, repository));
    }

    [Test]
    public async Task EvidenceExport_WhenExactPatchExceedsBound_FailsClosed()
    {
        var repository = await CreateRepositoryAsync().ConfigureAwait(false);
        var data = Path.Combine(_root, "bounded-data");
        Directory.CreateDirectory(data);
        var options = Options.Create(OptionsValue(maxPatchBytes: 128));
        var snapshot = Snapshot(DevelopmentWorkspaceSecurity.RepositoryIdentityHash(
            DevelopmentWorkspaceSecurity.CanonicalRepositoryRoot(repository)));

        using var sandbox = CreateSandbox();
        var provider = new DevelopmentWorkspaceProvider(new FakeNodeDataDirectory(data), sandbox, options, TimeProvider.System);
        var session = await provider.PrepareAsync(snapshot, repository).ConfigureAwait(false);
        var tools = new DevelopmentWorkspaceTools(sandbox, session, options);
        _ = await tools.WriteFileAsync("large.txt", new string('x', 1024)).ConfigureAwait(false);

        var evidence = new DevelopmentPatchEvidenceService(sandbox, options);
        await AssertEx.ThrowsAsync<InvalidDataException>(() => evidence.ExportAsync(session));
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
        _ = store.Received(5).AttachArtifactAsync(Arg.Any<DevelopmentAttachArtifactCommand>(), Arg.Any<CancellationToken>());
        _ = store.Received(1).AttachArtifactAsync(Arg.Is<DevelopmentAttachArtifactCommand>(command => command.Kind == XE_Local_AI_Engine.Client.Persistence.Entities.DevelopmentArtifactKind.CoderSubmission),
            Arg.Any<CancellationToken>());
        _ = store.Received(1).TerminalizeAttemptAsync(Arg.Is<DevelopmentTerminalizeAttemptCommand>(command => command.Status == PersistenceDevelopmentAttemptStatus.Succeeded
                                                                                                           && command.InputTokens == 10
                                                                                                           && command.OutputTokens == 20),
            Arg.Any<CancellationToken>());
    }

    private static DevelopmentOptions OptionsValue(int maxPatchBytes = 1024 * 1024,
        int maxFileWriteBytes = 1024 * 1024,
        int maxCommandOutputBytes = 256 * 1024) => new()
    {
        Enabled = true,
        MaxArtifactBytes = 2 * 1024 * 1024,
        MaxPatchBytes = maxPatchBytes,
        MaxFileWriteBytes = maxFileWriteBytes,
        MaxCommandOutputBytes = maxCommandOutputBytes,
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
            PersistenceDevelopmentAttemptStatus.Running,
            "local-model",
            "local",
            AttemptVersion: 1);
    }

    private async Task<string> CreateRepositoryAsync()
    {
        var repository = Path.Combine(_root, "repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repository);
        EnsureSuccess(await RunProcessAsync(repository, "git", "init", "--initial-branch=main", ".").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "config", "user.email", "gate3@example.invalid").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "config", "user.name", "Gate 3 Test").ConfigureAwait(false));
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "base\n").ConfigureAwait(false);
        EnsureSuccess(await RunProcessAsync(repository, "git", "add", "README.md").ConfigureAwait(false));
        EnsureSuccess(await RunProcessAsync(repository, "git", "commit", "-m", "base").ConfigureAwait(false));
        return repository;
    }

    private static ProcessSandboxRuntimeProvider CreateSandbox()
    {
        return new ProcessSandboxRuntimeProvider(Options.Create(new LocalContainerOptions()), TimeProvider.System);
    }

    private static async Task<CommandResult> RunProcessAsync(string workingDirectory, string executable, params string[] arguments)
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

    private static ILocalModelProviderResolver LocalModelResolver(params LocalModelDescriptor[] models)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ListModelsAsync(Arg.Any<CancellationToken>()).Returns(models);
        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(provider);
        return resolver;
    }

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

    private sealed class ThrowingChatClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("transport reached");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class NullWorkspaceTools : IDevelopmentWorkspaceTools
    {
        public IReadOnlyList<DevelopmentCommandEvidence> CommandEvidence => [];
        public Task<string> ListFilesAsync(string? path, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> ReadFileAsync(string path, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> SearchTextAsync(string pattern, string? path, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> WriteFileAsync(string path, string content, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> ApplyPatchAsync(string patch, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> GetDiffAsync(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
        public Task<string> RunCommandAsync(string commandId, CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    }

    private sealed class SubmittingChatClient(long inputTokens, long outputTokens) : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var submit = AssertEx.NotNull(options?.Tools?.OfType<AIFunction>()
                .SingleOrDefault(static tool => tool.Name == "submit_implementation"));
            _ = await submit.InvokeAsync(new AIFunctionArguments
            {
                ["summary"] = "done",
                ["changedFiles"] = Array.Empty<string>(),
                ["commandIds"] = Array.Empty<string>()
            }, cancellationToken).ConfigureAwait(false);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "done"))
            {
                Usage = new UsageDetails
                {
                    InputTokenCount = inputTokens,
                    OutputTokenCount = outputTokens,
                    TotalTokenCount = inputTokens + outputTokens
                }
            };
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
