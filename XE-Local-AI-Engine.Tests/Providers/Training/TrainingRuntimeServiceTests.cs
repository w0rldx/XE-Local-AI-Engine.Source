namespace XE_Local_AI_Engine.Tests.Providers.Training;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static TrainingRuntimeTestInfrastructure;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Drives the runtime phase machine end to end against a fake subprocess runner and a pre-seeded uv cache. The real
///     install downloads roughly 7.5 GB of CUDA wheels, so every transition here — including the adopt rollback that
///     protects a working runtime from a failed reprovision — has to be exercisable without that.
/// </summary>
public sealed class TrainingRuntimeServiceTests
{
    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_HappyPath_WalksEveryPhaseAndRecordsTheInstalledState()
    {
        using var harness = new Harness(SucceedingRunner());

        var result = await harness.Service.InstallAsync(CancellationToken.None);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.Started, result.Outcome);
        await harness.Service.DrainAsync(CancellationToken.None);

        var status = harness.Service.GetStatus();
        AssertEx.Equal(TrainingRuntimePhase.Ready, status.Phase);
        AssertEx.Null(status.SanitizedError);

        var installed = AssertEx.NotNull(status.Installed, "A completed install must record its state.");
        AssertEx.Equal("3.13.15", installed.PythonVersion);
        AssertEx.Equal("2.11.0+cu128", installed.TorchVersion);
        AssertEx.Equal(TrainingRuntimePins.UvVersion, installed.UvVersion);
        AssertEx.Equal(TrainingRuntimePins.ProbeContractVersion, installed.ContractVersion);

        // The phases the UI narrates, in order, without asserting on interleaved log-line events.
        var phases = harness.Publisher.Events
                            .Select(static status => status.Phase)
                            .Where(static phase => phase != nameof(TrainingRuntimePhase.Idle))
                            .Distinct()
                            .ToArray();
        AssertEx.Contains(phases, nameof(TrainingRuntimePhase.AcquiringUv));
        AssertEx.Contains(phases, nameof(TrainingRuntimePhase.ProvisioningPython));
        AssertEx.Contains(phases, nameof(TrainingRuntimePhase.InstallingPackages));
        AssertEx.Contains(phases, nameof(TrainingRuntimePhase.Verifying));
        AssertEx.Contains(phases, nameof(TrainingRuntimePhase.Ready));

        // The state record must survive a restart: a fresh service reads it back and reports Ready without installing.
        using var restarted = harness.Restart();
        AssertEx.Equal(TrainingRuntimePhase.Ready, restarted.Service.GetStatus().Phase);
        AssertEx.NotNull(restarted.Service.ResolveInterpreterPath(), "The adopted venv interpreter must be resolvable.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_UsesTheLockfileStrictlyAndScrubsTheEnvironment()
    {
        Environment.SetEnvironmentVariable("XE_TRAINING_TEST_SECRET", "must-not-leak");
        try
        {
            using var harness = new Harness(SucceedingRunner());
            _ = await harness.Service.InstallAsync(CancellationToken.None);
            await harness.Service.DrainAsync(CancellationToken.None);

            var sync = AssertEx.NotNull(harness.Runner.Invocations
                                               .FirstOrDefault(invocation => invocation.Args.Contains("sync")),
                "The install must invoke uv sync.");

            // --locked is what makes this reproducible rather than merely repeatable: uv fails instead of re-resolving.
            AssertEx.Contains(sync.Args, "--locked");
            AssertEx.False(sync.Environment.ContainsKey("XE_TRAINING_TEST_SECRET"),
                "The subprocess environment is an allowlist; an inherited variable must never reach uv.");
            AssertEx.Equal("1", sync.Environment["UV_NO_CONFIG"]);
            AssertEx.Equal("only-managed", sync.Environment["UV_PYTHON_PREFERENCE"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XE_TRAINING_TEST_SECRET", null);
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenTheLockfileIsMissing_FailsWithoutTouchingTheActiveRuntime()
    {
        using var harness = new Harness(SucceedingRunner());
        File.Delete(Path.Combine(harness.ScriptsDirectory, "uv.lock"));

        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);

        var status = harness.Service.GetStatus();
        AssertEx.Equal(TrainingRuntimePhase.Failed, status.Phase);
        AssertEx.Contains(status.SanitizedError, "lockfile");
        AssertEx.Null(status.Installed);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenTheProbeReportsADifferentContract_RefusesToAdopt()
    {
        using var harness = new Harness(SucceedingRunner("""{"contractVersion":99,"ready":true,"cudaAvailable":true}"""));

        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);

        var status = harness.Service.GetStatus();
        AssertEx.Equal(TrainingRuntimePhase.Failed, status.Phase);
        AssertEx.Contains(status.SanitizedError, "different version");
        AssertEx.False(Directory.Exists(TrainingRuntimeLayout.ActiveVenv(harness.CacheRoot)),
            "A runtime that failed verification must never be adopted.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenTheProbeCannotReachTheGpu_RefusesToAdopt()
    {
        using var harness = new Harness(SucceedingRunner("""{"contractVersion":1,"ready":false,"cudaAvailable":false,"python":"3.13.15"}"""));

        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);

        AssertEx.Contains(harness.Service.GetStatus().SanitizedError, "GPU");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenTheProbeReportsImportFailures_NamesThePackagesWithoutTheTraceback()
    {
        const string handshake =
            """{"contractVersion":1,"ready":false,"cudaAvailable":true,"errors":{"unsloth":"RuntimeError: PyTorch and torchvision were compiled with different CUDA major versions"}}""";
        using var harness = new Harness(SucceedingRunner(handshake));

        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);

        var error = AssertEx.NotNull(harness.Service.GetStatus().SanitizedError, "A failed verification must record a reason.");
        AssertEx.Contains(error, "unsloth");
        AssertEx.False(error.Contains("RuntimeError", StringComparison.Ordinal),
            "The sanitized error names the failing package; the traceback stays in the streamed log.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenTheAdoptFails_RollsThePreviousRuntimeBack()
    {
        using var harness = new Harness(SucceedingRunner());

        // Land a working runtime first.
        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);
        var active = TrainingRuntimeLayout.ActiveVenv(harness.CacheRoot);
        var marker = Path.Combine(active, "previous-runtime.marker");
        await File.WriteAllTextAsync(marker, "the working runtime");

        // Now break the adopt: the staged venv is deleted the moment verification finishes, so the directory move that
        // swaps it into place throws after the previous runtime has already been parked in the backup.
        using var breaking = harness.Restart(new FakeProcessRunner((file, args, logSink) =>
        {
            if (file.EndsWith(TrainingRuntimePins.UvExecutableName, StringComparison.Ordinal))
            {
                var binDirectory = Path.Combine(TrainingRuntimeLayout.StagingVenv(harness.CacheRoot), ".venv", "bin");
                _ = Directory.CreateDirectory(binDirectory);
                File.WriteAllText(Path.Combine(binDirectory, "python"), "#!/bin/sh\n");
                return 0;
            }

            logSink(ValidHandshake);
            Directory.Delete(TrainingRuntimeLayout.StagingVenv(harness.CacheRoot), recursive: true);
            return 0;
        }));

        _ = await breaking.Service.InstallAsync(CancellationToken.None);
        await breaking.Service.DrainAsync(CancellationToken.None);

        var status = breaking.Service.GetStatus();
        // Ready, not Failed. The rollback put a working runtime back, and the training and export paths gate on the
        // phase — reporting Failed here would retire a runtime that still works until some later install succeeded.
        AssertEx.Equal(TrainingRuntimePhase.Ready, status.Phase);
        AssertEx.NotNull(status.SanitizedError, "The failed attempt still has to be reported; it rides on the error field.");
        AssertEx.True(File.Exists(marker), "A failed reprovision must restore the previous working runtime.");
        AssertEx.False(Directory.Exists(TrainingRuntimeLayout.BackupVenv(harness.CacheRoot)),
            "The backup must not be left behind after the rollback.");
        AssertEx.NotNull(breaking.Service.ResolveInterpreterPath(), "The restored runtime must still be usable.");
    }

    /// <summary>
    ///     The rollback boundary has to span the state write, not stop at the directory swap. A cancellation between
    ///     the two leaves the NEW venv active, the previous one parked in a backup that the next install's
    ///     <c>Recover()</c> deletes as garbage, and a state record describing neither — i.e. the working runtime is
    ///     gone even though nothing was ever adopted.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenTheStateWriteFailsAfterTheSwap_RestoresBothThePreviousRuntimeAndItsStateRecord()
    {
        using var harness = new Harness(SucceedingRunner());
        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);
        var marker = Path.Combine(TrainingRuntimeLayout.ActiveVenv(harness.CacheRoot), "previous-runtime.marker");
        await File.WriteAllTextAsync(marker, "the working runtime");

        // Cancel once verification has produced its report: the next thing the install touches is the state write, and
        // that is the only step between the directory swap and the point the backup would be deleted.
        TrainingRuntimeService? cancelling = null;
        using var breaking = harness.Restart(new FakeProcessRunner((file, args, logSink) =>
        {
            if (file.EndsWith(TrainingRuntimePins.UvExecutableName, StringComparison.Ordinal))
            {
                var binDirectory = Path.Combine(TrainingRuntimeLayout.StagingVenv(harness.CacheRoot), ".venv", "bin");
                _ = Directory.CreateDirectory(binDirectory);
                File.WriteAllText(Path.Combine(binDirectory, "python"), "#!/bin/sh\n");
                return 0;
            }

            // A different python version, so the restored state record is distinguishable from the one never adopted.
            logSink("""{"contractVersion":1,"ready":true,"cudaAvailable":true,"python":"9.9.9","torch":"2.11.0+cu128"}""");
            _ = cancelling!.Cancel();
            return 0;
        }));
        cancelling = breaking.Service;

        _ = await breaking.Service.InstallAsync(CancellationToken.None);
        await breaking.Service.DrainAsync(CancellationToken.None);

        var status = breaking.Service.GetStatus();
        AssertEx.Equal(TrainingRuntimePhase.Ready, status.Phase);
        AssertEx.Contains(status.SanitizedError, "cancelled");
        AssertEx.True(File.Exists(marker), "The previous runtime must be back in place, not left parked in the backup.");
        AssertEx.False(Directory.Exists(TrainingRuntimeLayout.BackupVenv(harness.CacheRoot)),
            "The backup is only consumed once BOTH the swap and the state write have succeeded.");
        AssertEx.Equal("3.13.15", AssertEx.NotNull(status.Installed, "A surviving runtime must still be reported.").PythonVersion);

        // And the record on disk, which is what a restart reads, describes the runtime that is actually there.
        using var restarted = harness.Restart();
        AssertEx.Equal("3.13.15", AssertEx.NotNull(restarted.Service.GetStatus().Installed).PythonVersion);
        AssertEx.NotNull(restarted.Service.ResolveInterpreterPath());
    }

    /// <summary>
    ///     The same rule one step earlier: a reprovision that fails BEFORE it touches the active directory has not
    ///     harmed anything, so the node keeps the runtime it had and only carries the failure as the error.
    /// </summary>
    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenAReprovisionFailsBeforeAdopting_KeepsThePreviousRuntimeUsable()
    {
        using var harness = new Harness(SucceedingRunner());
        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);

        using var breaking = harness.Restart(SucceedingRunner("""{"contractVersion":99,"ready":true,"cudaAvailable":true}"""));
        _ = await breaking.Service.InstallAsync(CancellationToken.None);
        await breaking.Service.DrainAsync(CancellationToken.None);

        var status = breaking.Service.GetStatus();
        AssertEx.Equal(TrainingRuntimePhase.Ready, status.Phase);
        AssertEx.Contains(status.SanitizedError, "different version");
        AssertEx.NotNull(status.Installed, "The previous runtime is untouched, so it must still be reported as installed.");
        AssertEx.NotNull(breaking.Service.ResolveInterpreterPath(), "Training and export must still be able to run.");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Remove_DeletesTheVenvAndTheStateRecord()
    {
        using var harness = new Harness(SucceedingRunner());
        _ = await harness.Service.InstallAsync(CancellationToken.None);
        await harness.Service.DrainAsync(CancellationToken.None);

        AssertEx.True(await harness.Service.RemoveAsync(CancellationToken.None));

        var status = harness.Service.GetStatus();
        AssertEx.Equal(TrainingRuntimePhase.Idle, status.Phase);
        AssertEx.Null(status.Installed);
        AssertEx.False(Directory.Exists(TrainingRuntimeLayout.VenvRoot(harness.CacheRoot)));
        AssertEx.False(File.Exists(TrainingRuntimeLayout.StatePath(harness.CacheRoot)));
        AssertEx.Null(harness.Service.ResolveInterpreterPath());
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Install_WhenPrerequisitesFail_RefusesAndDistinguishesDiskFromEverythingElse()
    {
        using var disk = new Harness(SucceedingRunner(), StubPrerequisiteProbe.Unsatisfied(TrainingRuntimePrerequisiteKeys.FreeDisk));
        var diskResult = await disk.Service.InstallAsync(CancellationToken.None);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.InsufficientDisk, diskResult.Outcome);
        AssertEx.NotNull(diskResult.Prerequisites, "A refusal must carry the report that explains it.");

        using var driver = new Harness(SucceedingRunner(), StubPrerequisiteProbe.Unsatisfied(TrainingRuntimePrerequisiteKeys.NvidiaDriver));
        var driverResult = await driver.Service.InstallAsync(CancellationToken.None);
        AssertEx.Equal(TrainingRuntimeInstallOutcome.MissingPrerequisites, driverResult.Outcome);

        // Nothing may be touched on a refused install.
        AssertEx.Empty(disk.Runner.Invocations);
        AssertEx.Empty(driver.Runner.Invocations);
    }

    [Test]
    [RunOn(OS.Linux)]
    public void Recover_RestoresABackupLeftBehindByAnInterruptedAdopt()
    {
        using var harness = new Harness(SucceedingRunner());
        var backup = TrainingRuntimeLayout.BackupVenv(harness.CacheRoot);
        _ = Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "marker"), "parked");
        var staging = TrainingRuntimeLayout.StagingVenv(harness.CacheRoot);
        _ = Directory.CreateDirectory(staging);

        harness.Service.Recover();

        // A backup with no active means the swap died between the two moves; the parked runtime is the good one.
        AssertEx.True(File.Exists(Path.Combine(TrainingRuntimeLayout.ActiveVenv(harness.CacheRoot), "marker")));
        AssertEx.False(Directory.Exists(staging), "An unadopted staging tree carries nothing worth keeping.");
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-training-" + Guid.NewGuid().ToString("N"));

        public Harness(FakeProcessRunner runner, ITrainingRuntimePrerequisiteProbe? probe = null)
        {
            CacheRoot = Path.Combine(_root, "cache");
            ScriptsDirectory = Path.Combine(_root, "scripts");
            WriteScripts(ScriptsDirectory);
            SeedCachedUv(CacheRoot);
            Runner = runner;
            Publisher = new RecordingPublisher();
            Service = Create(runner, probe ?? StubPrerequisiteProbe.Satisfied(), Publisher);
        }

        private Harness(Harness origin, FakeProcessRunner runner)
        {
            _root = origin._root;
            CacheRoot = origin.CacheRoot;
            ScriptsDirectory = origin.ScriptsDirectory;
            Runner = runner;
            Publisher = new RecordingPublisher();
            Service = Create(runner, StubPrerequisiteProbe.Satisfied(), Publisher);
        }

        public string CacheRoot { get; }

        public string ScriptsDirectory { get; }

        public FakeProcessRunner Runner { get; }

        public RecordingPublisher Publisher { get; }

        public TrainingRuntimeService Service { get; }

        /// <summary>A second service over the same cache root — how a restart sees the persisted state.</summary>
        public Harness Restart(FakeProcessRunner? runner = null)
        {
            return new Harness(this, runner ?? SucceedingRunner());
        }

        public void Dispose()
        {
            Service.Dispose();
            try
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }

        private TrainingRuntimeService Create(ITrainingProcessRunner runner,
            ITrainingRuntimePrerequisiteProbe probe,
            ITrainingRuntimeEventPublisher publisher)
        {
            // The acquirer takes the cache-hit path, so its HttpClient is never used.
            using var http = new HttpClient();
            return new TrainingRuntimeService(probe,
                publisher,
                new UvBinaryAcquirer(http),
                runner,
                NullLogger<TrainingRuntimeService>.Instance,
                CacheRoot,
                ScriptsDirectory);
        }
    }
}
