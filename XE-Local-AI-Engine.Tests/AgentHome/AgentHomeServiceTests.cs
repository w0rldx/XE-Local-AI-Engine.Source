namespace XE_Local_AI_Engine.Tests.AgentHome;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Marker I-pre service-level coverage: the real <see cref="AgentHomeService" /> drives the real
///     <see cref="AgentHomeManifestService" /> (temp host root) and the <see cref="FakeSandboxRuntimeProvider" />
///     end-to-end, with a fake resolver/identity injected through a real scope factory. No Docker, no Ollama.
/// </summary>
public sealed class AgentHomeServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 5, 29, 12, 0, 0, TimeSpan.Zero);

    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Test]
    public async Task RunAsync_WhenPreparedWithKnownFolder_ReturnsRunScopedResult()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        AssertEx.Equal(AgentHomeStatus.Ready, prepared.Layout.Manifest.Status);
        AssertEx.Equal("fake", prepared.Handle.ProviderName);
        AssertEx.Equal(1, prepared.ResolvedFolders.Count);

        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "analyze the project",
            AllowedActions = ["read_workspace"]
        });

        AssertEx.NotNullOrEmpty(run.RunId);
        AssertEx.True(run.Completed, "the scripted no-op probe completes on the fake provider");
        AssertEx.Equal(0, run.ExitCode);
        AssertEx.True(Directory.Exists(run.LogPath), "the run-scoped log directory must exist");
        AssertEx.Contains(run.LogPath, Path.Combine("runs", run.RunId, "logs"));
    }

    [Test]
    public async Task PrepareAsync_WhenFolderHasMixedTree_CopiesSurvivorsExcludesSecretsAndOutputs()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();

        var source = Path.Combine(Path.GetTempPath(), "agenthome-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(source, "src"));
        Directory.CreateDirectory(Path.Combine(source, "bin"));
        await File.WriteAllTextAsync(Path.Combine(source, "src", "Program.cs"), "class P { }");
        await File.WriteAllTextAsync(Path.Combine(source, ".env"), "SECRET=1");
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "app.dll"), "binary");
        _tempRoots.Add(source);
        resolver.Add(folderId, "selected-project", source);

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        AssertEx.Equal(1, prepared.FolderSnapshots.Count);
        var snapshot = prepared.FolderSnapshots[0];
        AssertEx.Equal(SelectedFolderCopyStatus.Copied, snapshot.Status);
        AssertEx.Equal(1, snapshot.CopiedFileCount);
        AssertEx.Equal("workspace/selected/selected-project", snapshot.WorkspacePath);

        var copied = provider.SnapshotSandboxPaths(prepared.Handle);
        AssertEx.Contains(copied, path => path.EndsWith("/src/Program.cs", StringComparison.Ordinal));
        AssertEx.True(copied.All(path => !path.EndsWith("/.env", StringComparison.Ordinal)), ".env must be excluded");
        AssertEx.True(copied.All(path => !path.Contains("/bin/", StringComparison.Ordinal)), "bin/ must be pruned");
    }

    [Test]
    public async Task RunAsync_WhenWorkspaceHasChanges_ExportsPatchUnderRunPatchesDirectory()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        // The fake has no real git, so script the two diff commands: one changed file and a small patch body.
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, "M\tselected-project/README.md\n");
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, "diff --git a/selected-project/README.md b/selected-project/README.md\n");

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            // export_patch is required for the Marker G export (Marker I gates it on AllowedActions in addition to the
            // baseline-exists gate).
            AllowedActions = ["read_workspace", "export_patch"]
        });

        AssertEx.Equal(1, run.Patch.ChangedFileCount);
        AssertEx.False(run.Patch.Blocked, "the small scripted patch is under budget");
        AssertEx.Equal($"runs/{run.RunId}/patches/changes.patch", run.Patch.PatchRelativePath);

        var patchFile = Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches", "changes.patch");
        var changedFilesFile = Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches", "changed-files.json");
        AssertEx.True(File.Exists(patchFile), "changes.patch must be written host-side under the run dir");
        AssertEx.True(File.Exists(changedFilesFile), "changed-files.json must be written host-side under the run dir");

        var changedJson = await File.ReadAllTextAsync(changedFilesFile);
        AssertEx.Contains(changedJson, folderId.ToString());
        AssertEx.Contains(changedJson, "README.md");
        AssertEx.False(
            changedJson.Contains(prepared.Layout.RootPath, StringComparison.Ordinal),
            "changed-files.json must not leak a host path");
    }

    [Test]
    public async Task RunAsync_WhenNoFolderCopied_SkipsPatchExport()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = []
        });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            AllowedActions = ["read_workspace"]
        });

        AssertEx.Equal(0, run.Patch.ChangedFileCount);
        AssertEx.True(run.Patch.PatchRelativePath is null, "no baseline means no patch path");
        AssertEx.True(run.Patch.ChangedFilesRelativePath is null, "no baseline means no changed-files path");
        AssertEx.True(
            provider.ExecutedCommands.All(command => !(command.Executable == "git" && command.Arguments.Contains("diff"))),
            "no git diff is issued when there is no baseline");
        AssertEx.False(
            Directory.Exists(Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches")),
            "no patches directory is created when nothing was exported");
    }

    [Test]
    public async Task PrepareAsync_WhenFolderIdUnknown_ThrowsBeforeAnyProviderCall()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        using var harness = CreateHarness(clock, provider, resolver);

        await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() =>
            harness.Service.PrepareAsync(new AgentHomePrepareRequest
            {
                SelectedFolderIds = [Guid.NewGuid().ToString()]
            }));

        // Resolution precedes manifest/provider work, so no sandbox was created for any key.
        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() =>
            provider.ConnectAsync(AnyKey()));
    }

    [Test]
    public async Task PrepareAsync_WhenRuntimeProfileNotAllowed_ThrowsBeforeAnyProviderCall()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            harness.Service.PrepareAsync(new AgentHomePrepareRequest
            {
                SelectedFolderIds = [folderId.ToString()],
                RuntimeProfile = "unsupported-profile"
            }));

        await AssertEx.ThrowsAsync<SandboxHandleInvalidException>(() =>
            provider.ConnectAsync(AnyKey()));
    }

    [Test]
    public async Task RunAsync_WhenCancelledDuringBlockingCommand_PropagatesCancellation()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        provider.RegisterBlockingCommand("dotnet --version");
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        using var cancellation = new CancellationTokenSource();
        var runTask = harness.Service.RunAsync(
            new AgentHomeRunRequest { Prepared = prepared, Goal = "g", AllowedActions = ["run_commands"] },
            cancellation.Token);

        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runTask);
    }

    [Test]
    public async Task PrepareAsync_WhenOwnerChanges_ReinitializesUnderNewOwner()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        var identity = new MutableIdentityProvider("owner-a", "node-1");
        using var harness = CreateHarness(clock, provider, resolver, identity);

        var first = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });
        AssertEx.Equal("owner-a", first.Layout.Manifest.OwnerUserId);

        identity.OwnerUserId = "owner-b";
        var second = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });

        AssertEx.Equal("owner-b", second.Layout.Manifest.OwnerUserId);
    }

    [Test]
    public async Task RunLifecycleAsync_WhenSecondConcurrentRunForSameOwnerNode_RejectsWithBusy()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        provider.RegisterBlockingCommand("dotnet --version");
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        // First run holds the guard on its blocking command; the second run for the SAME owner-node must be rejected
        // (not queued) while the first is in flight.
        using var firstCancellation = new CancellationTokenSource();
        var first = harness.Service.RunLifecycleAsync(NewLifecycle(folderId), firstCancellation.Token);
        await WaitForInFlightCommandAsync(provider);

        await AssertEx.ThrowsAsync<AgentHomeBusyException>(() => harness.Service.RunLifecycleAsync(NewLifecycle(folderId)));

        // Cancel the first run to release the guard, then a later run for the same owner-node succeeds (guard released
        // in finally on cancel/timeout/success).
        await firstCancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => first);

        provider.RegisterCommand("dotnet --version", 0);
        var third = await harness.Service.RunLifecycleAsync(NewLifecycle(folderId));
        AssertEx.True(third.Completed, "the guard must be released so a later run for the same owner-node succeeds");
    }

    [Test]
    public async Task RunLifecycleAsync_WhenDifferentOwnerNode_NotBlockedByConcurrentRun()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        provider.RegisterBlockingCommand("dotnet --version");
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        var identity = new MutableIdentityProvider("owner-a", "node-1");
        using var harness = CreateHarness(clock, provider, resolver, identity);

        using var firstCancellation = new CancellationTokenSource();
        var first = harness.Service.RunLifecycleAsync(NewLifecycle(folderId), firstCancellation.Token);
        await WaitForInFlightCommandAsync(provider);

        // A different owner-node keys a different guard, so its run is not rejected. Use a distinct node id too so the
        // two runs do not contend on the same node-scoped manifest/sandbox; the point is the guard key differs. Assert
        // it got past the guard (a SECOND in-flight command exists) rather than throwing AgentHomeBusy.
        identity.OwnerUserId = "owner-b";
        identity.NodeId = "node-2";
        using var secondCancellation = new CancellationTokenSource();
        var second = harness.Service.RunLifecycleAsync(NewLifecycle(folderId), secondCancellation.Token);
        await WaitForInFlightCommandCountAsync(provider, 2);

        // The real assertion: the second run got PAST the guard (two in-flight commands exist) instead of being
        // rejected with AgentHomeBusy. Both runs are still blocking, so neither has faulted.
        AssertEx.False(second.IsFaulted, "a different owner-node must not be rejected by the first owner's guard");
        AssertEx.False(first.IsFaulted, "the first run is still blocking, not faulted");

        // Drain both blocking runs to leave no orphan (cancellation is cleanup here, not the assertion).
        await firstCancellation.CancelAsync();
        await secondCancellation.CancelAsync();
        await SwallowAsync(first);
        await SwallowAsync(second);
    }

    [Test]
    public async Task RunAsync_WhenCommandTimesOut_ReturnsTimedOutResultWithoutThrowing()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        provider.RegisterBlockingCommand("dotnet --version");
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        // A short command timeout fires the internal CancelAfter while the caller token stays un-cancelled, so the run
        // is a TIMEOUT (non-throwing, TimedOut=true) rather than a caller cancel.
        using var harness = CreateHarness(clock, provider, resolver, commandTimeoutSeconds: 1);

        var run = await harness.Service.RunLifecycleAsync(NewLifecycle(folderId));

        AssertEx.False(run.Completed, "a timed-out run did not complete");
        AssertEx.True(run.TimedOut, "the command timeout must surface as TimedOut, not an exception");
        AssertEx.Equal(-1, run.ExitCode);
    }

    [Test]
    public async Task RunAsync_WhenCancelled_FiresProviderCancelAndPropagates()
    {
        var clock = new FixedClock(FixedNow);
        var inner = new FakeSandboxRuntimeProvider(clock);
        inner.RegisterBlockingCommand("dotnet --version");
        var provider = new CancelRecordingProvider(inner);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        using var cancellation = new CancellationTokenSource();
        var runTask = harness.Service.RunLifecycleAsync(NewLifecycle(folderId), cancellation.Token);
        await WaitForInFlightCommandAsync(inner);

        await cancellation.CancelAsync();

        // A user cancel propagates OperationCanceledException, and the in-flight command is best-effort cancelled.
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runTask);
        AssertEx.True(provider.CancelCommandCallCount > 0, "a caller cancel must fire CancelCommandAsync on the provider");
    }

    [Test]
    public async Task RunAsync_WhenWorkspaceCopied_RunsCommandAtWorkspaceSelectedRoot()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var run = await harness.Service.RunLifecycleAsync(NewLifecycle(folderId));
        AssertEx.True(run.Completed, "the scripted probe completes on the fake provider");

        var probe = provider.ExecutedCommands.Single(command =>
            string.Equals(command.Executable, "dotnet", StringComparison.Ordinal) && command.Arguments.Contains("--version"));
        AssertEx.Equal("/agent-home/workspace/selected", probe.WorkingDirectory);
    }

    [Test]
    public async Task RunAsync_WhenNoWorkspaceCopied_RunsCommandWithoutWorkingDirectory()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [] });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            AllowedActions = ["run_commands"]
        });

        AssertEx.True(run.Completed, "the scripted probe completes on the fake provider");
        var probe = provider.ExecutedCommands.Single(command =>
            string.Equals(command.Executable, "dotnet", StringComparison.Ordinal) && command.Arguments.Contains("--version"));
        AssertEx.True(probe.WorkingDirectory is null, "with no copied workspace the command runs with no CWD override");
    }

    [Test]
    public async Task RunAsync_WhenExportPatchNotAllowed_SkipsPatchEvenWithBaseline()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        // Script the diff commands so a patch WOULD export if the gate let it; AllowedActions omits export_patch, so it
        // must be skipped despite a real baseline.
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, 0, "M\tselected-project/README.md\n");
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, 0, "diff --git a/selected-project/README.md b/selected-project/README.md\n");

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            AllowedActions = ["read_workspace"]
        });

        AssertEx.Equal(0, run.Patch.ChangedFileCount);
        AssertEx.True(run.Patch.PatchRelativePath is null, "export_patch was not granted, so no patch path is produced");
    }

    [Test]
    public async Task RunAsync_WhenProposeMemoryNotAllowed_SkipsMemoryCollection()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });

        // Seed a memory proposals file under the run dir BEFORE the run so that, IF collection ran, it would read it.
        // The run id is allocated inside RunAsync, so assert via the log instead: with propose_memory omitted, no
        // memory_collected event is written. (Collection on the fake is a no-op regardless, so the gate is the signal.)
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            AllowedActions = ["read_workspace"]
        });

        var eventsLog = Path.Combine(run.LogPath, "events.jsonl");
        AssertEx.True(File.Exists(eventsLog), "the run logger must write events.jsonl");
        var eventsContent = await File.ReadAllTextAsync(eventsLog);
        AssertEx.False(eventsContent.Contains("memory_collected", StringComparison.Ordinal),
            "propose_memory was not granted, so memory collection (and its event) must be skipped");
    }

    [Test]
    public async Task RunAsync_WhenProposeMemoryAllowed_RunsMemoryCollection()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            AllowedActions = ["read_workspace", "propose_memory"]
        });

        var eventsLog = Path.Combine(run.LogPath, "events.jsonl");
        var eventsContent = await File.ReadAllTextAsync(eventsLog);
        AssertEx.Contains(eventsContent, "memory_collected");
    }

    [Test]
    public async Task RunAsync_WhenOwnerSubjectInToken_FlowsIntoAttachKey()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        var identity = new MutableIdentityProvider("user-subject-42", "node-1");
        using var harness = CreateHarness(clock, provider, resolver, identity);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest { SelectedFolderIds = [folderId.ToString()] });

        AssertEx.Equal("user-subject-42", prepared.Handle.AttachKey.OwnerUserId);
        AssertEx.Equal("node-1", prepared.Handle.AttachKey.NodeId);
    }

    private static AgentHomeRunLifecycleRequest NewLifecycle(Guid folderId)
    {
        return new AgentHomeRunLifecycleRequest
        {
            SelectedFolderIds = [folderId.ToString()],
            Goal = "g",
            AllowedActions = ["run_commands"]
        };
    }

    private static async Task WaitForInFlightCommandAsync(FakeSandboxRuntimeProvider provider)
    {
        await WaitForInFlightCommandCountAsync(provider, 1);
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            // Expected when a blocking run is drained by cancelling its token; this is cleanup, not the assertion.
        }
    }

    private static async Task WaitForInFlightCommandCountAsync(FakeSandboxRuntimeProvider provider, int count)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (InFlightExecutionIds(provider).Count >= count)
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"Expected {count} in-flight command(s) but the fake never reached it.");
    }

    private static IReadOnlyList<string> InFlightExecutionIds(FakeSandboxRuntimeProvider provider)
    {
        // The blocking command records the run id as its execution id; ExecutedCommands holds every issued command.
        // A command that is still blocking has no completion recorded, so its execution id is in flight. The fake's
        // CancelCommandAsync targets it by id, so collecting the executed ids of blocking probes is sufficient here.
        return provider.ExecutedCommands
            .Where(command => string.Equals(command.Executable, "dotnet", StringComparison.Ordinal))
            .Select(command => command.ExecutionId)
            .ToList();
    }

    private sealed class CancelRecordingProvider : ISandboxRuntimeProvider
    {
        private readonly FakeSandboxRuntimeProvider _inner;
        private int _cancelCommandCallCount;

        public CancelRecordingProvider(FakeSandboxRuntimeProvider inner)
        {
            _inner = inner;
        }

        public int CancelCommandCallCount => Volatile.Read(ref _cancelCommandCallCount);

        public string ProviderName => _inner.ProviderName;

        public SandboxProviderCapabilities Capabilities => _inner.Capabilities;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            return _inner.CreateOrAttachAsync(request, cancellationToken);
        }

        public Task<SandboxHandle> ConnectAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default)
        {
            return _inner.ConnectAsync(attachKey, cancellationToken);
        }

        public Task<SandboxCommandResult> ExecuteAsync(SandboxHandle handle, SandboxCommandRequest request, CancellationToken cancellationToken = default)
        {
            return _inner.ExecuteAsync(handle, request, cancellationToken);
        }

        public Task CopyIntoAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
        {
            return _inner.CopyIntoAsync(handle, request, cancellationToken);
        }

        public Task<string> ReadFileAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
        {
            return _inner.ReadFileAsync(handle, sandboxPath, cancellationToken);
        }

        public Task CopyOutAsync(SandboxHandle handle, SandboxCopyRequest request, CancellationToken cancellationToken = default)
        {
            return _inner.CopyOutAsync(handle, request, cancellationToken);
        }

        public Task CancelCommandAsync(SandboxHandle handle, string executionId, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _cancelCommandCallCount);
            return _inner.CancelCommandAsync(handle, executionId, cancellationToken);
        }

        public Task KillAsync(SandboxHandle handle, CancellationToken cancellationToken = default)
        {
            return _inner.KillAsync(handle, cancellationToken);
        }
    }

    private static SandboxAttachKey AnyKey()
    {
        return new SandboxAttachKey
        {
            OwnerUserId = "owner-a",
            NodeId = "node-1",
            ProviderName = "fake",
            RuntimeProfile = "dotnet-agent-home",
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
    }

    private string CreateSourceFolder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agenthome-src-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "README.md"), "# project");
        _tempRoots.Add(directory);
        return directory;
    }

    private ServiceHarness CreateHarness(
        TimeProvider clock,
        ISandboxRuntimeProvider provider,
        ISelectedFolderResolver resolver,
        IAgentHomeIdentityProvider? identity = null,
        int commandTimeoutSeconds = 300)
    {
        var root = Path.Combine(Path.GetTempPath(), "agenthome-svc-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        var options = Options.Create(new AgentHomeOptions { RootPath = root, CommandTimeoutSeconds = commandTimeoutSeconds });
        var hostEnvironment = new TestHostEnvironment { ContentRootPath = root };
        var manifestService = new AgentHomeManifestService(
            hostEnvironment, options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);

        var serviceProvider = new ServiceCollection()
            .AddScoped(_ => resolver)
            // Marker K: the service resolves a fresh per-run logger from a scope. Register the real logger so the run
            // writes JSONL into the temp run dir (best-effort; never fails the run).
            .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(clock))
            .BuildServiceProvider();

        var memoryProposalService = new AgentHomeMemoryProposalService(
            NullLogger<AgentHomeMemoryProposalService>.Instance);

        var workspaceService = new AgentHomeWorkspaceService(
            provider,
            new SensitiveFileExclusionService(),
            options,
            NullLogger<AgentHomeWorkspaceService>.Instance);

        var patchService = new AgentHomePatchService(
            provider,
            options,
            NullLogger<AgentHomePatchService>.Instance);

        var service = new AgentHomeService(
            manifestService,
            provider,
            identity ?? new MutableIdentityProvider("owner-a", "node-1"),
            workspaceService,
            patchService,
            memoryProposalService,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            clock,
            NullLogger<AgentHomeService>.Instance);

        return new ServiceHarness(service, manifestService, serviceProvider);
    }

    private sealed class ServiceHarness : IDisposable
    {
        private readonly AgentHomeManifestService _manifestService;
        private readonly ServiceProvider _serviceProvider;

        public ServiceHarness(AgentHomeService service, AgentHomeManifestService manifestService, ServiceProvider serviceProvider)
        {
            Service = service;
            _manifestService = manifestService;
            _serviceProvider = serviceProvider;
        }

        public AgentHomeService Service { get; }

        public void Dispose()
        {
            _manifestService.Dispose();
            _serviceProvider.Dispose();
        }
    }

    private sealed class MutableIdentityProvider : IAgentHomeIdentityProvider
    {
        public MutableIdentityProvider(string ownerUserId, string nodeId)
        {
            OwnerUserId = ownerUserId;
            NodeId = nodeId;
        }

        public string OwnerUserId { get; set; }

        public string NodeId { get; set; }

        public Task<AgentHomeOwnerIdentity> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentHomeOwnerIdentity(OwnerUserId, NodeId));
        }
    }

    private sealed class FakeSelectedFolderResolver : ISelectedFolderResolver
    {
        private readonly Dictionary<Guid, ResolvedSelectedFolder> _folders = [];

        public void Add(Guid id, string alias, string hostPath)
        {
            _folders[id] = new ResolvedSelectedFolder(id, alias, hostPath, SelectedFolderMode.Copy);
        }

        public Task<SelectedFolderReference> RegisterAsync(SelectedFolderRegistration registration, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<SelectedFolderReference>> ListReferencesAsync(CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SelectedFolderReference> references =
                _folders.Values.Select(folder => new SelectedFolderReference(folder.Id.ToString(), folder.Alias)).ToList();
            return Task.FromResult(references);
        }

        public Task<ResolvedSelectedFolder> ResolveAsync(string id, CancellationToken cancellationToken = default)
        {
            if (Guid.TryParse(id, out var parsed) && _folders.TryGetValue(parsed, out var folder))
            {
                return Task.FromResult(folder);
            }

            throw new SelectedFolderValidationException($"Unknown selected folder id '{id}'.");
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedClock(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";

        public string EnvironmentName { get; set; } = "Development";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
