namespace XE_Local_AI_Engine.Tests.AgentHome;

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.AgentHome;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Workspace;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Service-level coverage: the real <see cref="AgentHomeService" /> drives the real
///     <see cref="AgentHomeManifestService" /> (temp host root) and the <see cref="FakeSandboxRuntimeProvider" />
///     end-to-end, with a fake resolver/identity injected through a real scope factory. No Docker, no Ollama.
/// </summary>
public sealed class AgentHomeServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(year: 2026, month: 5, day: 29, hour: 12, minute: 0, second: 0, TimeSpan.Zero);

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
        AssertEx.Equal(expected: 1, prepared.ResolvedFolders.Count);

        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "analyze the project",
            AllowedActions = ["read_workspace"]
        });

        AssertEx.NotNullOrEmpty(run.RunId);
        AssertEx.True(run.Completed, "the scripted no-op probe completes on the fake provider");
        AssertEx.Equal(expected: 0, run.ExitCode);
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

        AssertEx.Equal(expected: 1, prepared.FolderSnapshots.Count);
        var snapshot = prepared.FolderSnapshots[0];
        AssertEx.Equal(SelectedFolderCopyStatus.Copied, snapshot.Status);
        AssertEx.Equal(expected: 1, snapshot.CopiedFileCount);
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
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, exitCode: 0, "M\tselected-project/README.md\n");
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, exitCode: 0, "diff --git a/selected-project/README.md b/selected-project/README.md\n");

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            // export_patch is required for patch export; the service gates it on AllowedActions in addition to the
            // baseline-exists gate.
            AllowedActions = ["read_workspace", "export_patch"]
        });

        AssertEx.Equal(expected: 1, run.Patch.ChangedFileCount);
        AssertEx.False(run.Patch.Blocked, "the small scripted patch is under budget");
        AssertEx.Equal($"runs/{run.RunId}/patches/changes.patch", run.Patch.PatchRelativePath);

        var patchFile = Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches", "changes.patch");
        var changedFilesFile = Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches", "changed-files.json");
        AssertEx.True(File.Exists(patchFile), "changes.patch must be written host-side under the run dir");
        AssertEx.True(File.Exists(changedFilesFile), "changed-files.json must be written host-side under the run dir");

        var changedJson = await File.ReadAllTextAsync(changedFilesFile);
        AssertEx.Contains(changedJson, folderId.ToString());
        AssertEx.Contains(changedJson, "README.md");
        AssertEx.False(changedJson.Contains(prepared.Layout.RootPath, StringComparison.Ordinal),
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

        AssertEx.Equal(expected: 0, run.Patch.ChangedFileCount);
        AssertEx.True(run.Patch.PatchRelativePath is null, "no baseline means no patch path");
        AssertEx.True(run.Patch.ChangedFilesRelativePath is null, "no baseline means no changed-files path");
        AssertEx.True(provider.ExecutedCommands.All(command => !(command.Executable == "git" && command.Arguments.Contains("diff"))),
            "no git diff is issued when there is no baseline");
        AssertEx.False(Directory.Exists(Path.Combine(prepared.Layout.RootPath, "runs", run.RunId, "patches")),
            "no patches directory is created when nothing was exported");
    }

    [Test]
    public async Task PrepareAsync_WhenFolderIdUnknown_ClearsPriorWorkspaceBeforeRejecting()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        using var harness = CreateHarness(clock, provider, resolver);

        var stale = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AnyKey(),
            RuntimeProfile = "dotnet-agent-home"
        });
        provider.WriteHostFile("stale", "old");
        await provider.CopyIntoAsync(stale, new SandboxCopyRequest
        {
            SourcePath = "stale",
            DestinationPath = AgentHomeGit.WorkspaceSelectedRoot + "/old/stale.txt"
        });

        await AssertEx.ThrowsAsync<SelectedFolderValidationException>(() =>
            harness.Service.PrepareAsync(new AgentHomePrepareRequest
            {
                SelectedFolderIds = [Guid.NewGuid().ToString()]
            }));

        var after = await provider.ConnectAsync(AnyKey());
        AssertEx.Empty(provider.SnapshotSandboxPaths(after));
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
        var runTask = harness.Service.RunAsync(new AgentHomeRunRequest
            {
                Prepared = prepared,
                Goal = "g",
                AllowedActions = ["run_commands"]
            },
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

        var first = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });
        AssertEx.Equal("owner-a", first.Layout.Manifest.OwnerUserId);

        identity.OwnerUserId = "owner-b";
        var second = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

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

        // First run holds the owner-node lease on its blocking command; the second run for the SAME owner-node must be rejected
        // (not queued) while the first is in flight.
        using var firstCancellation = new CancellationTokenSource();
        var first = harness.Service.RunLifecycleAsync(NewLifecycle(folderId), firstCancellation.Token);
        await WaitForInFlightCommandAsync(provider);

        await AssertEx.ThrowsAsync<AgentHomeBusyException>(() => harness.Service.RunLifecycleAsync(NewLifecycle(folderId)));

        // Cancel the first run to release the guard, then a later run for the same owner-node succeeds (guard released
        // in finally on cancel/timeout/success).
        await firstCancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => first);

        provider.RegisterCommand("dotnet --version", exitCode: 0);
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
        await WaitForInFlightCommandCountAsync(provider, count: 2, first, second);

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
    public async Task RunLifecycleAsync_WhenOwnerNodeIsPoisoned_RefusesBeforeProviderUse()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var leases = new AgentHomeExecutionLeaseManager();
        leases.MarkPoisoned(new AgentHomeExecutionLeaseKey("owner-a", "node-1"));
        using var harness = CreateHarness(clock, provider, resolver, leaseManager: leases);

        await AssertEx.ThrowsAsync<AgentHomeRequestRejectedException>(() =>
            harness.Service.RunLifecycleAsync(NewLifecycle(Guid.NewGuid())));
        AssertEx.False(await HasLiveSandboxAsync(provider));
    }

    [Test]
    public async Task RunLifecycleAsync_WhenManifestInitializationFails_ClearsPriorSelection()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var stale = await SeedStaleSelectionAsync(provider);
        using var harness = CreateHarness(clock,
            provider,
            new FakeSelectedFolderResolver(),
            manifestOverride: new ThrowingManifestService());

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.RunLifecycleAsync(NewLifecycle(Guid.NewGuid())));

        AssertEx.Empty(provider.SnapshotSandboxPaths(stale));
    }

    [Test]
    public async Task RunLifecycleAsync_WhenCreateOrAttachFails_ClearsPriorSelection()
    {
        var clock = new FixedClock(FixedNow);
        var inner = new FakeSandboxRuntimeProvider(clock);
        var stale = await SeedStaleSelectionAsync(inner);
        var provider = new CancelRecordingProvider(inner)
        {
            FailCreateOrAttach = true
        };
        using var harness = CreateHarness(clock, provider, new FakeSelectedFolderResolver());

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            harness.Service.RunLifecycleAsync(NewLifecycle(Guid.NewGuid())));

        AssertEx.Empty(inner.SnapshotSandboxPaths(stale));
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
        AssertEx.Equal(expected: -1, run.ExitCode);
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

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = []
        });
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
        provider.RegisterCommand(GitDiffCommandKeys.NameStatus, exitCode: 0, "M\tselected-project/README.md\n");
        provider.RegisterCommand(GitDiffCommandKeys.PatchDiff, exitCode: 0, "diff --git a/selected-project/README.md b/selected-project/README.md\n");

        using var harness = CreateHarness(clock, provider, resolver);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });
        var run = await harness.Service.RunAsync(new AgentHomeRunRequest
        {
            Prepared = prepared,
            Goal = "g",
            AllowedActions = ["read_workspace"]
        });

        AssertEx.Equal(expected: 0, run.Patch.ChangedFileCount);
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

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

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

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });
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

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        AssertEx.Equal("user-subject-42", prepared.Handle.AttachKey.OwnerUserId);
        AssertEx.Equal("node-1", prepared.Handle.AttachKey.NodeId);
    }

    [Test]
    public async Task PrepareAsync_WhenConversationHasAttachments_AppendsAttachmentsFolderAndStagesMarkdown()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        var conversationId = Guid.NewGuid();
        var store = new FakeConversationUploadedFileStore();
        store.Add(conversationId, "report.pdf", "# Quarterly report\n\nRevenue grew 12%.");

        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()],
            ConversationId = conversationId
        });

        // The synthetic "attachments" folder is appended alongside the user folder, so both are resolved and copied.
        AssertEx.Contains(prepared.ResolvedFolders, folder => folder.Alias == "attachments");
        var attachments = prepared.FolderSnapshots.Single(snapshot => snapshot.Alias == "attachments");
        AssertEx.Equal(SelectedFolderCopyStatus.Copied, attachments.Status);
        AssertEx.Equal(expected: 1, attachments.CopiedFileCount);
        AssertEx.Equal("workspace/selected/attachments", attachments.WorkspacePath);

        // The decrypted Markdown was copied into the sandbox so the agent's read tools discover it.
        var copied = provider.SnapshotSandboxPaths(prepared.Handle);
        AssertEx.Contains(copied, path => path.EndsWith("/attachments/report.md", StringComparison.Ordinal));

        // The staging snapshot holds DECRYPTED plaintext; it must be disposed once the copy completes so it never lingers.
        AssertEx.Equal(expected: 1, store.CreatedSnapshotPaths.Count);
        AssertEx.False(Directory.Exists(store.CreatedSnapshotPaths[0]),
            "the decrypted staging temp dir must be removed after the workspace copy completes");
    }

    [Test]
    public async Task PrepareAsync_WhenNoConversationId_DoesNotAppendAttachmentsFolder()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        var conversationId = Guid.NewGuid();
        var store = new FakeConversationUploadedFileStore();
        store.Add(conversationId, "report.pdf", "# Quarterly report");

        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store);

        // No ConversationId seeded, so even a conversation WITH files contributes nothing — no snapshot is even created.
        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()]
        });

        AssertEx.True(prepared.ResolvedFolders.All(folder => folder.Alias != "attachments"),
            "with no conversation context no attachments folder is appended");
        AssertEx.True(prepared.FolderSnapshots.All(snapshot => snapshot.Alias != "attachments"),
            "with no conversation context no attachments folder is copied");
        AssertEx.Equal(expected: 0, store.CreatedSnapshotPaths.Count);
    }

    [Test]
    public async Task PrepareAsync_WhenConversationHasNoFiles_DoesNotAppendAttachmentsFolder()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var folderId = Guid.NewGuid();
        resolver.Add(folderId, "selected-project", CreateSourceFolder());

        // A conversation id is set but the store has no files for it, so the staging step is a no-op.
        var store = new FakeConversationUploadedFileStore();

        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store);

        var prepared = await harness.Service.PrepareAsync(new AgentHomePrepareRequest
        {
            SelectedFolderIds = [folderId.ToString()],
            ConversationId = Guid.NewGuid()
        });

        AssertEx.True(prepared.ResolvedFolders.All(folder => folder.Alias != "attachments"),
            "a conversation with no files appends no attachments folder");
        AssertEx.True(prepared.FolderSnapshots.All(snapshot => snapshot.Alias != "attachments"),
            "a conversation with no files copies no attachments folder");
        AssertEx.Equal(expected: 0, store.CreatedSnapshotPaths.Count);
    }

    [Test]
    public async Task PrepareConversationAttachmentsAsync_WhenAgentModeDisabled_StagesNothing()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        var conversationId = Guid.NewGuid();
        var store = new FakeConversationUploadedFileStore();
        store.Add(conversationId, "report.pdf", "# Report\n\nBearing housing B-12.");

        // enabled defaults to false → the coder tools refuse at execution anyway, so no sandbox is created and no
        // decrypted snapshot is staged.
        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store);

        await using var staged = await harness.Service.PrepareConversationAttachmentsAsync(conversationId);

        AssertEx.Empty(staged.StagedPaths);
        AssertEx.Equal(expected: 0, store.CreatedSnapshotPaths.Count);
        AssertEx.False(await HasLiveSandboxAsync(provider), "Agent Mode disabled must not create a sandbox.");
    }

    [Test]
    public async Task PrepareConversationAttachmentsAsync_WhenConversationHasNoFiles_ReplacesPriorSelectionWithEmpty()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();
        var store = new FakeConversationUploadedFileStore();
        var leases = new AgentHomeExecutionLeaseManager();

        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store, enabled: true, leaseManager: leases);

        // A pre-existing project selection must be removed even when the new attachment set is empty.
        var existing = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AnyKey(),
            RuntimeProfile = "dotnet-agent-home",
            NetworkPolicy = SandboxNetworkPolicy.None
        });
        provider.WriteHostFile("stale", "old project");
        await provider.CopyIntoAsync(existing, new SandboxCopyRequest
        {
            SourcePath = "stale",
            DestinationPath = AgentHomeGit.WorkspaceSelectedRoot + "/old/stale.txt"
        });

        var staged = await harness.Service.PrepareConversationAttachmentsAsync(Guid.NewGuid());

        AssertEx.Empty(staged.StagedPaths);
        var after = await provider.ConnectAsync(AnyKey());
        AssertEx.Equal(existing.SandboxId, after.SandboxId);
        AssertEx.Empty(provider.SnapshotSandboxPaths(after));
        AssertEx.Equal(expected: 0, store.CreatedSnapshotPaths.Count);

        Task<IAgentHomeExecutionLease?> contender;
        using (ExecutionContext.SuppressFlow())
        {
            contender = Task.Run(() => leases.TryAcquire(new AgentHomeExecutionLeaseKey("owner-a", "node-1")));
        }

        AssertEx.Null(await contender);
        await staged.DisposeAsync();
    }

    [Test]
    public async Task PrepareConversationAttachmentsAsync_WhenConversationHasFiles_StagesAttachmentsIntoSandbox()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        var conversationId = Guid.NewGuid();
        var store = new FakeConversationUploadedFileStore();
        store.Add(conversationId, "spec.pdf", "# Spec\n\nPeak battery endurance 38 minutes.");

        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store, enabled: true);

        await using var staged = await harness.Service.PrepareConversationAttachmentsAsync(conversationId);

        // The returned workspace-relative path points the model straight at the staged file.
        AssertEx.Contains(staged.StagedPaths, path => string.Equals(path, "attachments/spec.md", StringComparison.Ordinal));

        var handle = await provider.ConnectAsync(AnyKey());
        var copied = provider.SnapshotSandboxPaths(handle);
        AssertEx.Contains(copied, path => path.EndsWith("/attachments/spec.md", StringComparison.Ordinal));
    }

    [Test]
    public async Task PrepareConversationAttachmentsAsync_WhenRestagedForAnotherConversation_LeavesNoResidue()
    {
        var clock = new FixedClock(FixedNow);
        var provider = new FakeSandboxRuntimeProvider(clock);
        var resolver = new FakeSelectedFolderResolver();

        var conversationA = Guid.NewGuid();
        var conversationB = Guid.NewGuid();
        var store = new FakeConversationUploadedFileStore();
        store.Add(conversationA, "alpha.pdf", "# Alpha");
        store.Add(conversationB, "bravo.pdf", "# Bravo");

        using var harness = CreateHarness(clock, provider, resolver, uploadedFileStore: store, enabled: true);

        await using (var stagedA = await harness.Service.PrepareConversationAttachmentsAsync(conversationA))
        {
            AssertEx.Contains(stagedA.StagedPaths, path => string.Equals(path, "attachments/alpha.md", StringComparison.Ordinal));
        }

        await using var stagedB = await harness.Service.PrepareConversationAttachmentsAsync(conversationB);

        // The second re-stage reports only conversation B's file.
        AssertEx.Contains(stagedB.StagedPaths, path => string.Equals(path, "attachments/bravo.md", StringComparison.Ordinal));
        AssertEx.True(stagedB.StagedPaths.All(path => !string.Equals(path, "attachments/alpha.md", StringComparison.Ordinal)),
            "the re-stage for conversation B must not report conversation A's file.");

        // The per-node sandbox is shared across conversations, so the second re-stage must leave only conversation B's
        // attachments — never conversation A's (no cross-conversation residue).
        var handle = await provider.ConnectAsync(AnyKey());
        var copied = provider.SnapshotSandboxPaths(handle);
        AssertEx.Contains(copied, path => path.EndsWith("/attachments/bravo.md", StringComparison.Ordinal));
        AssertEx.True(copied.All(path => !path.EndsWith("/attachments/alpha.md", StringComparison.Ordinal)),
            "conversation A's attachment must not survive the re-stage for conversation B.");
    }

    private static async Task<bool> HasLiveSandboxAsync(FakeSandboxRuntimeProvider provider)
    {
        try
        {
            _ = await provider.ConnectAsync(AnyKey());
            return true;
        }
        catch (SandboxHandleInvalidException)
        {
            return false;
        }
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
        await WaitForInFlightCommandCountAsync(provider, count: 1);
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

    private static async Task WaitForInFlightCommandCountAsync(FakeSandboxRuntimeProvider provider,
        int count,
        params Task[] runs)
    {
        // Deliberately NOT TestBudgets.Contended. The commands this waits on are registered blocking, so they stay
        // in flight until the test cancels them: if the count has not been reached, waiting longer does not reach it.
        // A 120s budget here bought nothing and made the same failure take two minutes to surface on CI.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (InFlightExecutionIds(provider).Count >= count)
            {
                return;
            }

            // A run that faulted will never produce its command, so report why instead of polling out with a
            // count that hides the real exception.
            foreach (var run in runs)
            {
                if (run.IsFaulted)
                {
                    throw new InvalidOperationException(
                        $"A run faulted before {count} command(s) were in flight.",
                        run.Exception);
                }
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"Expected {count} in-flight command(s) but the fake never reached it; "
                                            + $"observed {InFlightExecutionIds(provider).Count}.");
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

    private ServiceHarness CreateHarness(TimeProvider clock,
        IAgentSandboxRuntimeProvider provider,
        ISelectedFolderResolver resolver,
        IAgentHomeIdentityProvider? identity = null,
        int commandTimeoutSeconds = 300,
        IConversationUploadedFileStore? uploadedFileStore = null,
        bool enabled = false,
        IAgentHomeExecutionLeaseManager? leaseManager = null,
        IAgentHomeManifestService? manifestOverride = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "agenthome-svc-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        var options = Options.Create(new AgentHomeOptions
        {
            RootPath = root,
            CommandTimeoutSeconds = commandTimeoutSeconds,
            Enabled = enabled
        });
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithAgentHomeCommandTimeoutSeconds(commandTimeoutSeconds)
                                                     .Build();
        var manifestService = new AgentHomeManifestService(new FakeNodeDataDirectory(root), options, provider, clock, NullLogger<AgentHomeManifestService>.Instance);

        var serviceProvider = new ServiceCollection()
                              .AddScoped(_ => resolver)
                              // The service resolves a fresh per-run logger from a scope. Register the real logger so the run
                              // writes JSONL into the temp run dir (best-effort; never fails the run).
                              .AddTransient<IAgentHomeRunLogger>(_ => new AgentHomeRunLogger(clock))
                              .BuildServiceProvider();

        var memoryProposalService = new AgentHomeMemoryProposalService(NullLogger<AgentHomeMemoryProposalService>.Instance);

        var leases = leaseManager ?? new AgentHomeExecutionLeaseManager();
        var isolation = new AgentHomeWorkspaceIsolation(provider, leases, NullLogger<AgentHomeWorkspaceIsolation>.Instance);
        var workspaceService = new AgentHomeWorkspaceService(provider,
            isolation,
            new SensitiveFileExclusionService(),
            runtimeSettings,
            NullLogger<AgentHomeWorkspaceService>.Instance);

        var patchService = new AgentHomePatchService(provider,
            runtimeSettings,
            NullLogger<AgentHomePatchService>.Instance);

        var service = new AgentHomeService(manifestOverride ?? manifestService,
            provider,
            identity ?? new MutableIdentityProvider("owner-a", "node-1"),
            leases,
            isolation,
            workspaceService,
            patchService,
            memoryProposalService,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            options,
            runtimeSettings,
            uploadedFileStore ?? new FakeConversationUploadedFileStore(),
            clock,
            NullLogger<AgentHomeService>.Instance);

        return new ServiceHarness(service, manifestService, serviceProvider);
    }

    private sealed class CancelRecordingProvider : IAgentSandboxRuntimeProvider
    {
        private readonly FakeSandboxRuntimeProvider _inner;
        private int _cancelCommandCallCount;

        public CancelRecordingProvider(FakeSandboxRuntimeProvider inner)
        {
            _inner = inner;
        }

        public int CancelCommandCallCount => Volatile.Read(ref _cancelCommandCallCount);

        public bool FailCreateOrAttach { get; init; }

        public string ProviderName => _inner.ProviderName;

        public SandboxProviderCapabilities Capabilities => _inner.Capabilities;

        public Task<SandboxHandle> CreateOrAttachAsync(SandboxCreateRequest request, CancellationToken cancellationToken = default)
        {
            if (FailCreateOrAttach)
            {
                throw new InvalidOperationException("injected create failure");
            }

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

        public Task ResetDirectoryAsync(SandboxHandle handle, string sandboxPath, CancellationToken cancellationToken = default)
        {
            return _inner.ResetDirectoryAsync(handle, sandboxPath, cancellationToken);
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

        public void Add(Guid id, string alias, string hostPath)
        {
            _folders[id] = new ResolvedSelectedFolder(id, alias, hostPath, SelectedFolderMode.Copy);
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

    private sealed class ThrowingManifestService : IAgentHomeManifestService
    {
        public Task<AgentHomeLayout> InitializeAsync(SandboxAttachKey attachKey, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("injected manifest failure");
    }

    private static async Task<SandboxHandle> SeedStaleSelectionAsync(FakeSandboxRuntimeProvider provider)
    {
        var handle = await provider.CreateOrAttachAsync(new SandboxCreateRequest
        {
            AttachKey = AnyKey(),
            RuntimeProfile = "dotnet-agent-home"
        });
        provider.WriteHostFile("stale-selection", "old");
        await provider.CopyIntoAsync(handle, new SandboxCopyRequest
        {
            SourcePath = "stale-selection",
            DestinationPath = AgentHomeGit.WorkspaceSelectedRoot + "/old/stale.txt"
        });
        return handle;
    }
}

internal static class AgentHomeServicePhaseTestAccess
{
    private static readonly MethodInfo PrepareMethod = typeof(AgentHomeService)
        .GetMethod("PrepareUnderLeaseAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static readonly MethodInfo RunMethod = typeof(AgentHomeService)
        .GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static async Task<AgentHomePrepareResult> PrepareAsync(this AgentHomeService service,
        AgentHomePrepareRequest request,
        CancellationToken cancellationToken = default)
    {
        var identityField = typeof(AgentHomeService).GetField("_identityProvider", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var providerField = typeof(AgentHomeService).GetField("_provider", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var identityProvider = (IAgentHomeIdentityProvider)identityField.GetValue(service)!;
        var provider = (IAgentSandboxRuntimeProvider)providerField.GetValue(service)!;
        var identity = await identityProvider.GetAsync(cancellationToken);
        if (request.RuntimeProfile is not null
            && !string.Equals(request.RuntimeProfile, "dotnet-agent-home", StringComparison.Ordinal))
        {
            throw new AgentHomeRequestRejectedException("the requested runtime profile is not enabled for this worker.");
        }

        var profile = string.IsNullOrWhiteSpace(request.RuntimeProfile) ? "dotnet-agent-home" : request.RuntimeProfile;
        var attachKey = new SandboxAttachKey
        {
            OwnerUserId = identity.OwnerUserId,
            NodeId = identity.NodeId,
            ProviderName = provider.ProviderName,
            RuntimeProfile = profile,
            ManifestVersion = AgentHomeManifest.CurrentVersion
        };
        return await (Task<AgentHomePrepareResult>)PrepareMethod.Invoke(service, [request, attachKey, profile, cancellationToken])!;
    }

    public static Task<AgentHomeRunResult> RunAsync(this AgentHomeService service,
        AgentHomeRunRequest request,
        CancellationToken cancellationToken = default) =>
        (Task<AgentHomeRunResult>)RunMethod.Invoke(service, [request, cancellationToken])!;
}
