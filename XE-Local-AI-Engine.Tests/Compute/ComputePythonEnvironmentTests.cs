namespace XE_Local_AI_Engine.Tests.Compute;

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.ManagedPython;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Contracts;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static XE_Local_AI_Engine.Tests.Providers.Training.TrainingRuntimeTestInfrastructure;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The compute venv's provisioning against the shared toolchain store, driven through the internal seam with a
///     seeded uv and a fake runner: nothing downloads, nothing spawns. The real-uv path is <see cref="ComputeSandboxLiveTests" />.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ComputePythonEnvironmentTests : IDisposable
{
    private readonly HttpClient _http = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-compute-env-" + Guid.NewGuid().ToString("N"));

    public ComputePythonEnvironmentTests()
    {
        ScriptsDirectory = Path.Combine(_root, "scripts");
        CacheRoot = Path.Combine(_root, "compute-runtime");
        Toolchain = new ManagedPythonToolchain(Path.Combine(_root, "python"));
        _ = Directory.CreateDirectory(ScriptsDirectory);
        File.WriteAllText(Path.Combine(ScriptsDirectory, "pyproject.toml"), "[project]\nname = \"xe-compute-runtime\"\n");
        File.WriteAllText(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 1\n");
        SeedCachedUv(Toolchain.Root);
    }

    private string ScriptsDirectory { get; }

    private string CacheRoot { get; }

    private ManagedPythonToolchain Toolchain { get; }

    private string VenvRoot => Path.Combine(CacheRoot, "venv", ".venv");

    private string StagingDirectory => Path.Combine(CacheRoot, "venv.staging");

    private string BackupDirectory => Path.Combine(CacheRoot, "venv.backup");

    public void Dispose()
    {
        _http.Dispose();
        if (!Directory.Exists(_root))
        {
            return;
        }

        // The provision strips write bits from the venv; restore them so the temp tree can go.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                File.SetUnixFileMode(directory, File.GetUnixFileMode(directory) | UnixFileMode.UserWrite);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Provision_OnALegacyVenv_DeletesIt_RebuildsOnTheSharedStore_AndBindsItsPythons()
    {
        // A pre-shared-store install: lock-digest-only state, a read-only venv linked into compute-runtime/pythons.
        SeedLegacyToolchain(CacheRoot);
        WriteVenv(Path.Combine(CacheRoot, "venv"), Path.Combine(CacheRoot, "pythons"));
        var staleMarker = Path.Combine(VenvRoot, "stale.marker");
        await File.WriteAllTextAsync(staleMarker, "built against the legacy CPython");
        await File.WriteAllTextAsync(Path.Combine(CacheRoot, "installed-compute-lock.sha256"), "0123");
        File.SetUnixFileMode(VenvRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var stagingHadAVenv = true;
        var liveVenvKeptDuringSync = false;
        var runner = new FakeProcessRunner((file, args, environment, _) =>
        {
            if (args[0] == "sync")
            {
                stagingHadAVenv = Directory.Exists(Path.Combine(args[^1], ".venv"));
                liveVenvKeptDuringSync = File.Exists(staleMarker);
                WriteVenv(args[^1], environment["UV_PYTHON_INSTALL_DIR"]);
            }

            return 0;
        });
        using var environment = Create(runner);

        var runtime = await environment.GetRuntimeAsync();

        AssertEx.False(stagingHadAVenv, "uv sync keeps an existing venv's home and bin/python link, so it must sync into a fresh project");
        AssertEx.True(liveVenvKeptDuringSync, "the live venv stays in service until the staged one is proven");
        AssertEx.False(File.Exists(staleMarker), "the swap replaced the old venv");
        AssertEx.Equal(2, runner.Invocations.Count, "one uv sync, one import check");
        var sync = runner.Invocations[0];
        AssertEx.Equal(StagingDirectory, sync.Args[^1]);
        var probe = runner.Invocations[1];
        AssertEx.Equal(Path.Combine(StagingDirectory, ".venv", "bin", "python"), probe.File);
        AssertEx.Equal("-I,-c,import numpy, scipy, sympy", string.Join(",", probe.Args));
        AssertEx.False(Directory.Exists(StagingDirectory), "the staging tree was renamed in");
        AssertEx.False(Directory.Exists(BackupDirectory), "the parked old venv was dropped");
        AssertEx.Equal(Toolchain.PythonInstallDirectory, sync.Environment["UV_PYTHON_INSTALL_DIR"]);
        AssertEx.Equal(Toolchain.CacheDirectory, sync.Environment["UV_CACHE_DIR"]);
        AssertEx.Equal("copy", sync.Environment["UV_LINK_MODE"], "a hardlinked venv would share its inodes, and so its stripped write bits, with the cache");
        AssertEx.True(sync.File.StartsWith(Toolchain.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal), "uv comes from the shared store");

        AssertEx.Equal(Path.Combine(VenvRoot, "bin", "python"), runtime.InterpreterPath);
        AssertEx.Equal(2, runtime.ReadOnlyTrees.Count);
        AssertEx.Equal(VenvRoot, runtime.ReadOnlyTrees[0]);
        AssertEx.Equal(Toolchain.PythonInstallDirectory, runtime.ReadOnlyTrees[1]);
        AssertEx.False(ManagedPythonToolchain.HasLegacyToolchain(CacheRoot), "nothing points into compute-runtime/{uv,pythons,uv-cache} any more");
        AssertEx.Equal(UnixFileMode.None, File.GetUnixFileMode(VenvRoot) & UnixFileMode.UserWrite, "the rebuilt venv is locked down again");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Provision_WhenTheStoredIdentityMatches_IsWarm_AndSpawnsNothing()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        var runner = SucceedingRunner();
        using var restarted = Create(runner);

        var runtime = await restarted.GetRuntimeAsync();

        AssertEx.Empty(runner.Invocations);
        AssertEx.Equal(Toolchain.PythonInstallDirectory, runtime.ReadOnlyTrees[1]);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Provision_WhenTheStoreMoves_RebuildsTheVenv()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        var moved = new ManagedPythonToolchain(Path.Combine(_root, "moved-store"));
        SeedCachedUv(moved.Root);
        var runner = SucceedingRunner();
        using var environment = new ComputePythonEnvironment(new UvBinaryAcquirer(_http),
            runner,
            NullLogger<ComputePythonEnvironment>.Instance,
            CacheRoot,
            ScriptsDirectory,
            moved);

        var runtime = await environment.GetRuntimeAsync();

        AssertEx.Equal(1, UvSyncs(runner), "a moved store invalidates the venv");
        AssertEx.Equal(moved.PythonInstallDirectory, runner.Invocations[0].Environment["UV_PYTHON_INSTALL_DIR"]);
        AssertEx.Equal(moved.PythonInstallDirectory, runtime.ReadOnlyTrees[1]);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Provision_TwoInstancesOnOneRoot_SyncOnce_BecauseTheSecondWaitsOnTheFileLock()
    {
        // Separate instances have separate in-process gates, exactly like two hosts; only the OS lock on the root
        // serializes them. Without it the second syncs into the same venv while the first locks it read-only.
        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GatedRunner(async (environment, projectDirectory) =>
        {
            syncEntered.TrySetResult();
            await releaseSync.Task;
            WriteVenv(projectDirectory, environment["UV_PYTHON_INSTALL_DIR"]);
        });
        using var first = Create(runner);
        using var second = Create(runner);

        var firstRuntime = first.GetRuntimeAsync();
        await syncEntered.Task;
        var secondRuntime = second.GetRuntimeAsync();
        await AssertEx.StaysIncompleteAsync(secondRuntime, "the second provision must queue behind the first's sync");
        releaseSync.SetResult();

        await Task.WhenAll(firstRuntime, secondRuntime);
        AssertEx.Equal(1, runner.SyncCount, "the queued provision finds the first one's venv warm");
        AssertEx.Equal((await firstRuntime).InterpreterPath, (await secondRuntime).InterpreterPath);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_OnAnEmptyRoot_IsNotProvisioned_AndSpawnsNothing()
    {
        var runner = SucceedingRunner();
        using var environment = Create(runner);

        var status = await environment.ReadStatusAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.NotProvisioned, status.State);
        AssertEx.Equal("compute", status.ProfileId);
        AssertEx.Empty(runner.Invocations);
        AssertEx.False(Directory.Exists(CacheRoot), "a status read must not create the root either");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_AfterProvision_IsReady_WithThePinnedIdentity()
    {
        using var environment = Create(SucceedingRunner());
        _ = await environment.GetRuntimeAsync();

        var status = await environment.ReadStatusAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, status.State);
        var installed = AssertEx.NotNull(status.Installed);
        AssertEx.Equal(ManagedPythonPins.PythonMinor, installed.PythonMinor);
        AssertEx.Equal(ManagedPythonPins.Current.Rid, installed.Rid);
        AssertEx.Equal(ManagedPythonPins.UvVersion, installed.UvVersion);
        AssertEx.Empty(status.Mismatches);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_WhenTheShippedLockfileChanges_IsUpdateRequired_AndTheNextCallRebuilds()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        await File.WriteAllTextAsync(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 2\n");
        var runner = SucceedingRunner();
        using var upgraded = Create(runner);

        var status = await upgraded.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.UpdateRequired, status.State);
        AssertEx.Equal("lockfile", string.Join(",", status.Mismatches));
        AssertEx.Empty(runner.Invocations);

        _ = await upgraded.GetRuntimeAsync();
        AssertEx.Equal(1, UvSyncs(runner), "the mismatch that status reports is the one the provision acts on");
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, (await upgraded.ReadStatusAsync(CancellationToken.None)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_OnTheLockDigestOnlyRecordOfAnOlderBuild_IsUpdateRequired()
    {
        WriteVenv(Path.Combine(CacheRoot, "venv"), Toolchain.PythonInstallDirectory);
        await File.WriteAllTextAsync(Path.Combine(CacheRoot, "installed-compute-lock.sha256"), "0123");
        using var environment = Create(SucceedingRunner());

        var status = await environment.ReadStatusAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.UpdateRequired, status.State);
        AssertEx.NotNull(status.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Status_WhenTheInterpreterIsGone_IsRepairRequired()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        var bin = Path.Combine(VenvRoot, "bin");
        File.SetUnixFileMode(bin, File.GetUnixFileMode(bin) | UnixFileMode.UserWrite);
        File.Delete(Path.Combine(bin, "python"));
        using var environment = Create(SucceedingRunner());

        var status = await environment.ReadStatusAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.RepairRequired, status.State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_OnAVenvWithoutARecord_IsRepairRequired()
    {
        WriteVenv(Path.Combine(CacheRoot, "venv"), Toolchain.PythonInstallDirectory);
        using var environment = Create(SucceedingRunner());

        AssertEx.Equal(ManagedPythonEnvironmentState.RepairRequired, (await environment.ReadStatusAsync(CancellationToken.None)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_AfterAFailedSync_IsFailed_WithTheModelSafeMessage()
    {
        using var environment = Create(new FakeProcessRunner((_, _, _) => 1));
        _ = await AssertEx.ThrowsAsync<ComputeEnvironmentException>(() => environment.GetRuntimeAsync());

        var status = await environment.ReadStatusAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.Failed, status.State);
        AssertEx.Equal("Installing the pinned compute runtime packages failed.", status.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Remove_WhileAProvisionIsInFlight_IsRefusedAsBusy_AndStatusIsProvisioning()
    {
        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var environment = Create(new GatedRunner(async (uvEnvironment, projectDirectory) =>
        {
            syncEntered.TrySetResult();
            await releaseSync.Task;
            WriteVenv(projectDirectory, uvEnvironment["UV_PYTHON_INSTALL_DIR"]);
        }));
        var provision = environment.GetRuntimeAsync();
        await syncEntered.Task;

        AssertEx.Equal(ManagedPythonEnvironmentState.Provisioning, (await environment.ReadStatusAsync(CancellationToken.None)).State);
        var remove = await environment.RemoveAsync(CancellationToken.None);
        var repair = await environment.RepairAsync(CancellationToken.None);

        releaseSync.SetResult();
        _ = await provision;
        AssertEx.Equal(ManagedPythonActionOutcome.Busy, remove.Outcome);
        AssertEx.Equal(ManagedPythonActionOutcome.Busy, repair.Outcome);
        AssertEx.True(File.Exists(Path.Combine(VenvRoot, "bin", "python")), "a refused remove must leave the provision it raced intact");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Remove_DeletesTheVenvAndItsRecord_KeepsTheStore_AndTheNextCallProvisionsAgain()
    {
        var runner = SucceedingRunner();
        using var environment = Create(runner);
        _ = await environment.GetRuntimeAsync();

        var result = await environment.RemoveAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonActionOutcome.Completed, result.Outcome);
        AssertEx.False(Directory.Exists(Path.Combine(CacheRoot, "venv")));
        AssertEx.False(File.Exists(Path.Combine(CacheRoot, "installed-compute-runtime.json")));
        AssertEx.True(File.Exists(Toolchain.PinnedUvExecutable), "the shared store is never this environment's to delete");
        AssertEx.Equal(ManagedPythonEnvironmentState.NotProvisioned, (await environment.ReadStatusAsync(CancellationToken.None)).State);

        _ = await environment.GetRuntimeAsync();
        AssertEx.Equal(2, UvSyncs(runner), "the cached runtime went with the venv");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Repair_HoldsTheGateUntilTheFreshProvisionLands_SoAConcurrentCallWaitsForIt()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new GatedRunner(async (uvEnvironment, projectDirectory) =>
        {
            syncEntered.TrySetResult();
            await releaseSync.Task;
            WriteVenv(projectDirectory, uvEnvironment["UV_PYTHON_INSTALL_DIR"]);
        });
        using var environment = Create(runner);

        var result = await environment.RepairAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonActionOutcome.Started, result.Outcome);
        AssertEx.Equal(ManagedPythonEnvironmentState.Provisioning, (await environment.ReadStatusAsync(CancellationToken.None)).State);
        await syncEntered.Task;
        var concurrent = environment.GetRuntimeAsync();
        await AssertEx.StaysIncompleteAsync(concurrent, "run_python must wait for the repair's provision, not race it");

        releaseSync.SetResult();
        await environment.PendingRepair;
        _ = await concurrent;
        AssertEx.Equal(1, runner.SyncCount, "the waiting call is handed the repaired runtime");
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, (await environment.ReadStatusAsync(CancellationToken.None)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Repair_WhenTheFreshSyncFails_EndsFailed()
    {
        using var environment = Create(new FakeProcessRunner((_, _, _) => 1));

        AssertEx.Equal(ManagedPythonActionOutcome.Started, (await environment.RepairAsync(CancellationToken.None)).Outcome);
        await environment.PendingRepair;

        var status = await environment.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.Failed, status.State);
        AssertEx.Equal("Installing the pinned compute runtime packages failed.", status.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_OnAnUnreadableRecord_IsRepairRequired_NotAnOlderBuild()
    {
        WriteVenv(Path.Combine(CacheRoot, "venv"), Toolchain.PythonInstallDirectory);
        await File.WriteAllTextAsync(Path.Combine(CacheRoot, "installed-compute-runtime.json"), "{ truncated");
        using var environment = Create(SucceedingRunner());

        var status = await environment.ReadStatusAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.RepairRequired, status.State);
        AssertEx.Equal("The compute runtime's state record is unreadable.", status.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_WhileAnotherInstanceSyncs_IsProvisioning_NotRepairRequired()
    {
        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Mid-sync the disk holds a staging tree and no record, which reads like an absent or interrupted provision.
        using var other = Create(new GatedRunner(async (uvEnvironment, projectDirectory) =>
        {
            WriteVenv(projectDirectory, uvEnvironment["UV_PYTHON_INSTALL_DIR"]);
            syncEntered.TrySetResult();
            await releaseSync.Task;
        }));
        using var observer = Create(SucceedingRunner());
        var provision = other.GetRuntimeAsync();
        await syncEntered.Task;

        var status = await observer.ReadStatusAsync(CancellationToken.None);

        releaseSync.SetResult();
        _ = await provision;
        AssertEx.Equal(ManagedPythonEnvironmentState.Provisioning, status.State);
        AssertEx.Equal("Another process is provisioning the compute runtime.", status.Reason);
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, (await observer.ReadStatusAsync(CancellationToken.None)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Status_WhileARemoveHoldsTheGate_SaysItIsBeingRemoved()
    {
        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSync = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var other = Create(new GatedRunner(async (uvEnvironment, projectDirectory) =>
        {
            syncEntered.TrySetResult();
            await releaseSync.Task;
            WriteVenv(projectDirectory, uvEnvironment["UV_PYTHON_INSTALL_DIR"]);
        }));
        using var environment = Create(SucceedingRunner());
        var provision = other.GetRuntimeAsync();
        await syncEntered.Task;

        // The remove takes this instance's gate synchronously, then queues on the other one's provision lock.
        var remove = environment.RemoveAsync(CancellationToken.None);
        await AssertEx.StaysIncompleteAsync(remove, "the remove must wait for the other instance's provision lock");
        var during = await environment.ReadStatusAsync(CancellationToken.None);

        releaseSync.SetResult();
        _ = await provision;
        AssertEx.Equal(ManagedPythonActionOutcome.Completed, (await remove).Outcome);
        AssertEx.Equal(ManagedPythonEnvironmentState.Provisioning, during.State);
        AssertEx.Equal("The compute runtime is being removed.", during.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task RemoveAndRepair_WhileARunPythonCallHoldsALease_AreBusy_AndSucceedOnceItIsReleased()
    {
        using var environment = Create(SucceedingRunner());
        _ = await environment.GetRuntimeAsync();
        var lease = environment.AcquireExecutionLease();

        var remove = await environment.RemoveAsync(CancellationToken.None);
        var repair = await environment.RepairAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonActionOutcome.Busy, remove.Outcome);
        AssertEx.Equal(ManagedPythonActionOutcome.Busy, repair.Outcome);
        AssertEx.True(File.Exists(Path.Combine(VenvRoot, "bin", "python")), "a busy refusal deletes nothing");

        lease.Dispose();
        lease.Dispose();
        AssertEx.Equal(ManagedPythonActionOutcome.Completed, (await environment.RemoveAsync(CancellationToken.None)).Outcome);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task GetRuntime_WhenTheCachedInterpreterWasDeletedUnderIt_ProvisionsAgain()
    {
        var runner = SucceedingRunner();
        using var environment = Create(runner);
        _ = await environment.GetRuntimeAsync();

        // Another host's remove, or an operator's rm: nothing in this process was told.
        var bin = Path.Combine(VenvRoot, "bin");
        File.SetUnixFileMode(bin, File.GetUnixFileMode(bin) | UnixFileMode.UserWrite);
        File.Delete(Path.Combine(bin, "python"));

        var runtime = await environment.GetRuntimeAsync();

        AssertEx.Equal(2, UvSyncs(runner), "a cached runtime whose interpreter is gone is not handed out");
        AssertEx.True(File.Exists(runtime.InterpreterPath));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Rebuild_SwapsTheStagedVenvIn_ReplacesTheRecord_AndLocksTheNewTreeDown()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        File.SetUnixFileMode(VenvRoot, File.GetUnixFileMode(VenvRoot) | UnixFileMode.UserWrite);
        var oldMarker = Path.Combine(VenvRoot, "old.marker");
        await File.WriteAllTextAsync(oldMarker, "the previous build");
        await File.WriteAllTextAsync(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 2\n");
        using var upgraded = Create(SucceedingRunner());

        var runtime = await upgraded.GetRuntimeAsync();

        AssertEx.Equal(Path.Combine(VenvRoot, "bin", "python"), runtime.InterpreterPath, "the adopted path does not move with the staging tree");
        AssertEx.False(File.Exists(oldMarker));
        AssertEx.False(Directory.Exists(StagingDirectory));
        AssertEx.False(Directory.Exists(BackupDirectory));
        AssertEx.Equal(UnixFileMode.None, File.GetUnixFileMode(VenvRoot) & UnixFileMode.UserWrite);
        var status = await upgraded.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, status.State);
        AssertEx.Null(status.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Rebuild_WhenTheStagedSyncFails_KeepsServingThePreviousVenv_AndReadsReadyWithTheFailure()
    {
        using (var first = Create(StoreLinkedRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        // An offline node after an upgrade: the lockfile moved on and uv cannot fetch the new closure.
        await File.WriteAllTextAsync(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 2\n");
        var runner = new FakeProcessRunner((_, _, _) => 1);
        using var upgraded = Create(runner);

        var runtime = await upgraded.GetRuntimeAsync();
        var again = await upgraded.GetRuntimeAsync();

        AssertEx.Equal(Path.Combine(VenvRoot, "bin", "python"), runtime.InterpreterPath);
        AssertEx.True(File.Exists(runtime.InterpreterPath), "the previous venv was never touched");
        AssertEx.Equal(Toolchain.PythonInstallDirectory, runtime.ReadOnlyTrees[1]);
        AssertEx.True(ReferenceEquals(runtime, again), "the same cached fallback");
        AssertEx.Equal(1, runner.Invocations.Count, "the fallback is cached; only a restart or a repair retries the rebuild");
        AssertEx.False(Directory.Exists(StagingDirectory), "the failed staging tree is discarded");
        var status = await upgraded.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, status.State);
        AssertEx.Equal("Installing the pinned compute runtime packages failed. The previous compute runtime stays in service.", status.Reason);
        AssertEx.Equal("lockfile", string.Join(",", status.Mismatches));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Rebuild_WhenTheImportCheckFails_NeverAdoptsTheStagedVenv()
    {
        using (var first = Create(StoreLinkedRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        await File.WriteAllTextAsync(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 2\n");
        using var upgraded = Create(new FakeProcessRunner((_, args, environment, _) =>
        {
            if (args[0] != "sync")
            {
                return 1;
            }

            WriteVenv(args[^1], environment["UV_PYTHON_INSTALL_DIR"]);
            File.WriteAllText(Path.Combine(args[^1], ".venv", "staged.marker"), "never adopted");
            return 0;
        }));

        var runtime = await upgraded.GetRuntimeAsync();

        AssertEx.False(File.Exists(Path.Combine(VenvRoot, "staged.marker")));
        AssertEx.True(File.Exists(runtime.InterpreterPath));
        AssertEx.Equal("The provisioned compute runtime failed its import check. The previous compute runtime stays in service.",
            (await upgraded.ReadStatusAsync(CancellationToken.None)).Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Provision_AfterACrashBetweenTheSwapRenames_RestoresTheParkedVenv_AndIsWarm()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        // The first rename landed, the second never ran; a staging tree is left too.
        Directory.Move(Path.Combine(CacheRoot, "venv"), BackupDirectory);
        WriteVenv(StagingDirectory, Toolchain.PythonInstallDirectory);
        var runner = SucceedingRunner();
        using var restarted = Create(runner);

        var runtime = await restarted.GetRuntimeAsync();

        AssertEx.Empty(runner.Invocations);
        AssertEx.True(File.Exists(runtime.InterpreterPath));
        AssertEx.False(Directory.Exists(BackupDirectory));
        AssertEx.False(Directory.Exists(StagingDirectory));
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, (await restarted.ReadStatusAsync(CancellationToken.None)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Repair_WhenTheRebuildFails_KeepsThePreviousVenvInService()
    {
        using (var first = Create(StoreLinkedRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        var runner = new FakeProcessRunner((_, _, _) => 1);
        using var environment = Create(runner);

        AssertEx.Equal(ManagedPythonActionOutcome.Started, (await environment.RepairAsync(CancellationToken.None)).Outcome);
        await environment.PendingRepair;

        var status = await environment.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, status.State);
        AssertEx.Equal("Installing the pinned compute runtime packages failed. The previous compute runtime stays in service.", status.Reason);
        AssertEx.Empty(status.Mismatches);
        var runtime = await environment.GetRuntimeAsync();
        AssertEx.True(File.Exists(runtime.InterpreterPath));
        AssertEx.Equal(1, runner.Invocations.Count, "the repair's one failed sync; the call after it is served the kept venv");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Dispose_DuringABackgroundRepair_ReleasesTheGateQuietly_AndKeepsThePreviousVenv()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        var syncEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var environment = Create(new GatedRunner(async (_, _, ct) =>
        {
            syncEntered.TrySetResult();
            await neverReleased.Task.WaitAsync(ct);
        }));
        AssertEx.Equal(ManagedPythonActionOutcome.Started, (await environment.RepairAsync(CancellationToken.None)).Outcome);
        await syncEntered.Task;

        environment.Dispose();
        await environment.PendingRepair;

        AssertEx.True(environment.PendingRepair.IsCompletedSuccessfully, "a shutdown under a repair must not leave a faulted task behind");
        AssertEx.True(File.Exists(Path.Combine(VenvRoot, "bin", "python")));
        AssertEx.False(Directory.Exists(StagingDirectory));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Rebuild_WhenThePreviousInterpreterDangles_IsNotServed_AndReadsFailed()
    {
        using (var first = Create(StoreLinkedRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        // The store the old venv links into lost its CPython: bin/python is now a dangling symlink.
        File.Delete(StoreInterpreter);
        await File.WriteAllTextAsync(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 2\n");
        using var upgraded = Create(new FakeProcessRunner((_, _, _) => 1));

        _ = await AssertEx.ThrowsAsync<ComputeEnvironmentException>(() => upgraded.GetRuntimeAsync());

        var status = await upgraded.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.Failed, status.State);
        AssertEx.Equal("Installing the pinned compute runtime packages failed.", status.Reason);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Rebuild_WhenThePreviousInterpreterIsNotOnTheStore_IsNotServed()
    {
        // SucceedingRunner writes bin/python as a plain file: nothing the jail's store bind could run.
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        await File.WriteAllTextAsync(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 2\n");
        using var upgraded = Create(new FakeProcessRunner((_, _, _) => 1));

        _ = await AssertEx.ThrowsAsync<ComputeEnvironmentException>(() => upgraded.GetRuntimeAsync());
        AssertEx.Equal(ManagedPythonEnvironmentState.Failed, (await upgraded.ReadStatusAsync(CancellationToken.None)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Rebuild_WhenTheRecordCannotBeReplaced_RollsTheSwapBack_AndTheOldVenvIsLive()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        File.SetUnixFileMode(VenvRoot, File.GetUnixFileMode(VenvRoot) | UnixFileMode.UserWrite);
        var oldMarker = Path.Combine(VenvRoot, "old.marker");
        await File.WriteAllTextAsync(oldMarker, "the previous build");
        // A directory where the record goes: the swap's renames succeed, then the atomic record replace fails.
        var statePath = Path.Combine(CacheRoot, "installed-compute-runtime.json");
        File.Delete(statePath);
        _ = Directory.CreateDirectory(statePath);
        var runner = SucceedingRunner();
        using var environment = Create(runner);

        _ = await AssertEx.ThrowsAsync<ComputeEnvironmentException>(() => environment.GetRuntimeAsync());

        AssertEx.Equal(1, UvSyncs(runner), "the rebuild reached the swap");
        AssertEx.True(File.Exists(oldMarker), "the old venv was renamed back into place");
        AssertEx.False(Directory.Exists(BackupDirectory));
        AssertEx.False(File.Exists(Path.Combine(StagingDirectory, ".venv", "old.marker")), "the rolled-back tree in staging is the new one");
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Provision_WithAParkedVenvBesideALiveOne_DropsTheParkedOne_AndIsWarm()
    {
        using (var first = Create(SucceedingRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        // A swap that finished but died before its cleanup: the parked tree is still locked down.
        WriteVenv(BackupDirectory, Toolchain.PythonInstallDirectory);
        File.SetUnixFileMode(Path.Combine(BackupDirectory, ".venv"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(CacheRoot, "installed-compute-runtime.json.tmp"), "{ half");
        var runner = SucceedingRunner();
        using var restarted = Create(runner);

        var runtime = await restarted.GetRuntimeAsync();

        AssertEx.Empty(runner.Invocations);
        AssertEx.True(File.Exists(runtime.InterpreterPath));
        AssertEx.False(Directory.Exists(BackupDirectory));
        AssertEx.False(File.Exists(Path.Combine(CacheRoot, "installed-compute-runtime.json.tmp")));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Remove_AlsoDeletesTheStagingAndParkedTrees()
    {
        using var environment = Create(SucceedingRunner());
        _ = await environment.GetRuntimeAsync();
        WriteVenv(StagingDirectory, Toolchain.PythonInstallDirectory);
        WriteVenv(BackupDirectory, Toolchain.PythonInstallDirectory);
        File.SetUnixFileMode(Path.Combine(BackupDirectory, ".venv"), UnixFileMode.UserRead | UnixFileMode.UserExecute);

        AssertEx.Equal(ManagedPythonActionOutcome.Completed, (await environment.RemoveAsync(CancellationToken.None)).Outcome);

        AssertEx.False(Directory.Exists(StagingDirectory));
        AssertEx.False(Directory.Exists(BackupDirectory));
        AssertEx.False(Directory.Exists(Path.Combine(CacheRoot, "venv")));
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Rebuild_WhenTheShippedLockfileVanishes_WhileServingThePreviousVenv_StillReadsReady()
    {
        using (var first = Create(StoreLinkedRunner()))
        {
            _ = await first.GetRuntimeAsync();
        }

        File.Delete(Path.Combine(ScriptsDirectory, "uv.lock"));
        using var environment = Create(SucceedingRunner());

        var runtime = await environment.GetRuntimeAsync();

        AssertEx.True(File.Exists(runtime.InterpreterPath));
        var status = await environment.ReadStatusAsync(CancellationToken.None);
        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, status.State, "status matches what run_python is handed");
        AssertEx.Equal("The pinned compute runtime lockfile is missing from this installation. The previous compute runtime stays in service.", status.Reason);
    }

    private string StoreInterpreter => Path.Combine(Toolchain.PythonInstallDirectory, "cpython-3.13.15-linux-x86_64-gnu", "bin", "python3.13");

    /// <summary>Like <see cref="SucceedingRunner" />, but bin/python is a real symlink into the store, as uv writes it.</summary>
    private FakeProcessRunner StoreLinkedRunner()
    {
        return new FakeProcessRunner((_, args, environment, _) =>
        {
            if (args[0] != "sync")
            {
                return 0;
            }

            WriteVenv(args[^1], environment["UV_PYTHON_INSTALL_DIR"]);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(StoreInterpreter)!);
            File.WriteAllText(StoreInterpreter, "#!/bin/sh\n");
            var python = Path.Combine(args[^1], ".venv", "bin", "python");
            File.Delete(python);
            _ = File.CreateSymbolicLink(python, StoreInterpreter);
            return 0;
        });
    }

    private ComputePythonEnvironment Create(IPythonToolRunner runner)
    {
        return new ComputePythonEnvironment(new UvBinaryAcquirer(_http),
            runner,
            NullLogger<ComputePythonEnvironment>.Instance,
            CacheRoot,
            ScriptsDirectory,
            Toolchain);
    }

    private static int UvSyncs(FakeProcessRunner runner)
    {
        return runner.Invocations.Count(static invocation => invocation.Args[0] == "sync");
    }

    /// <summary>A uv whose sync completes only when the test says so; the import check passes at once.</summary>
    private sealed class GatedRunner : IPythonToolRunner
    {
        private readonly Func<IReadOnlyDictionary<string, string>, string, CancellationToken, Task> _sync;
        private int _syncCount;

        public GatedRunner(Func<IReadOnlyDictionary<string, string>, string, Task> sync)
            : this((environment, projectDirectory, _) => sync(environment, projectDirectory))
        {
        }

        public GatedRunner(Func<IReadOnlyDictionary<string, string>, string, CancellationToken, Task> sync)
        {
            _sync = sync;
        }

        public int SyncCount => Volatile.Read(ref _syncCount);

        public async Task<int> RunAsync(string file,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string> environment,
            string workingDirectory,
            Action<string> logSink,
            TimeSpan timeout,
            CancellationToken ct)
        {
            if (args[0] != "sync")
            {
                return 0;
            }

            _ = Interlocked.Increment(ref _syncCount);
            await _sync(environment, args[^1], ct);
            return 0;
        }
    }
}
