namespace XE_Local_AI_Engine.Installer.Tests.StateMachine;

using NSubstitute;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Installer.Driver;
using XE_Local_AI_Engine.Installer.State;
using XE_Local_AI_Engine.Installer.StateMachine;
using XE_Local_AI_Engine.Installer.Tests.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InstallerOrchestratorTests
{
    private static InstallManifest InstalledManifest() => new()
    {
        InstallerVersion = "0.1.0-rc.1",
        BundleSha256 = "abc",
        DistroName = DriverFactory.DistroName,
        AppImageId = "expected-image-id",
        PulledModel = DriverFactory.BootstrapModel,
        CreatedPaths = [],
        InstalledAtUtc = DateTimeOffset.UtcNow
    };

    private static InstallerArguments Args(InstallerVerb verb, bool assumeYes = false, bool dryRun = false) => new()
    {
        Verb = verb,
        BundlePath = "/fixture/bundle",
        AssumeYes = assumeYes,
        DryRun = dryRun
    };

    private static InstallerOrchestrator Build(
        IInstallerEnvironmentDriver driver,
        InMemoryInstallStateStore store,
        RecordingInstallerConsole console) =>
        new(driver, store, console, DriverFactory.CreateContext());

    [Test]
    public async Task StateMachine_WhenManifestPresent_InstallAborts()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.AlreadyInstalled, exit);
        AssertEx.True(console.ContainsLine("already installed"), "should advise using reset.");
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().VerifyPayloadChecksumAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StateMachine_WhenRemoveMissingInstall_FailsGracefully()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        var console = new RecordingInstallerConsole();

        var removeExit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Remove));
        var resetExit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Reset));

        AssertEx.Equal(InstallerExitCode.NotInstalled, removeExit);
        AssertEx.Equal(InstallerExitCode.NotInstalled, resetExit);
        await driver.DidNotReceive().TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StateMachine_WhenRebootPending_ResumesFromState()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        // Post-reboot relaunch: state was persisted at Probe with reboot-pending, WSL is now present.
        store.SeedState(new InstallState { Phase = InstallPhase.Probe, RebootPending = true });
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        // wsl-enable self-skips because the feature is now present (EnableWsl never called).
        await driver.DidNotReceive().EnableWslAsync(Arg.Any<CancellationToken>());
        // Full run completed: image loaded, model pulled, manifest written, state cleared.
        await driver.Received(1).ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        AssertEx.NotNull(store.Manifest);
        AssertEx.Null(store.State);
    }

    [Test]
    public async Task StateMachine_EachPhaseIdempotent()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        // Resume from a phase past distro-import + image-load: those completed phases must NOT re-run.
        store.SeedState(new InstallState { Phase = InstallPhase.ConfigWrite });
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().LoadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Remaining phases run exactly once.
        await driver.Received(1).WriteConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.Received(1).InstallHostAgentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.Received(1).PullModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Probe_WhenDistroExistsButNoManifest_Aborts()
    {
        var driver = DriverFactory.CreateHappyPath();
        driver.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WslProbeResult
            {
                WslFeaturePresent = true,
                Wsl2Capable = true,
                DistroPresent = true,
                FreeDiskBytes = DriverFactory.RequiredDiskBytes * 2,
                RequiredFreeDiskBytes = DriverFactory.RequiredDiskBytes
            });
        var store = new InMemoryInstallStateStore();
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.PreflightFailed, exit);
        AssertEx.True(console.ContainsLine("already exists"), "must explain the distro-collision abort.");
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Probe_WhenInsufficientDisk_Aborts()
    {
        var driver = DriverFactory.CreateHappyPath();
        driver.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WslProbeResult
            {
                WslFeaturePresent = true,
                Wsl2Capable = true,
                DistroPresent = false,
                FreeDiskBytes = 1024,
                RequiredFreeDiskBytes = DriverFactory.RequiredDiskBytes
            });
        var store = new InMemoryInstallStateStore();
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.PreflightFailed, exit);
        AssertEx.True(console.ContainsLine("Insufficient free disk"), "must report the disk shortfall.");
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Reset_WhenTeardownPartial_Aborts()
    {
        var driver = DriverFactory.CreateHappyPath();
        driver.TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(false), Arg.Any<CancellationToken>())
            .Returns(new TeardownResult
            {
                DistroRemoved = false,
                ProgramDataRemoved = true,
                ManifestRemoved = true,
                Residuals = ["xe-engine-runtime distro"]
            });
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole(confirmationAnswer: true);

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Reset));

        AssertEx.Equal(InstallerExitCode.TeardownIncomplete, exit);
        AssertEx.True(console.ContainsLine("Residual"), "must list residuals.");
        // Aborts before reinstalling: no distro import after a partial teardown.
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Teardown_MapsDryRunToWhatIf_AndConfirmedToForce()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole(confirmationAnswer: true);

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Remove, assumeYes: true));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        // Step 1 = dry-run (WhatIf) inventory; step 3 = the real deletion (Force). Exactly one of each.
        await driver.Received(1).TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(true), Arg.Any<CancellationToken>());
        await driver.Received(1).TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(false), Arg.Any<CancellationToken>());
        // --yes skips the typed gate.
        AssertEx.Equal(0, console.ConfirmCallCount);
    }

    [Test]
    public async Task Teardown_WhenDryRun_OnlyInventories()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Remove, dryRun: true));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        // --dry-run runs ONLY the WhatIf inventory, never the Force deletion, never the gate.
        await driver.Received(1).TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(true), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(false), Arg.Any<CancellationToken>());
        AssertEx.Equal(0, console.ConfirmCallCount);
    }

    [Test]
    public async Task Reset_WhenDryRun_OnlyInventories_NeverTearsDownOrReinstalls()
    {
        // sec#2: reset --dry-run mirrors remove — inventory only, no Force teardown, no reinstall.
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Reset, dryRun: true));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        await driver.Received(1).TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(true), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(false), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(0, console.ConfirmCallCount);
    }

    [Test]
    public async Task Install_WhenPhaseAlreadySatisfied_SkipsThatPhase()
    {
        // code#5: a satisfied idempotency probe skips the phase action even within a fresh run.
        var driver = DriverFactory.CreateHappyPath();
        driver.IsPhaseSatisfiedAsync(InstallerPhaseProbe.DistroImport, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        driver.IsPhaseSatisfiedAsync(InstallerPhaseProbe.ImageLoad, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var store = new InMemoryInstallStateStore();
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().LoadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        // The rest still runs.
        await driver.Received(1).WriteConfigAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Remove_PrintsManifestOwnedInventory_BeforeConfirm()
    {
        // code#6: the ownership-derived inventory is surfaced to the operator before the typed gate.
        var driver = DriverFactory.CreateHappyPath();
        driver.BuildTeardownInventoryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(["container `ollama` (manifest-owned)"]);
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole(confirmationAnswer: false);

        await Build(driver, store, console).RunAsync(Args(InstallerVerb.Remove));

        AssertEx.True(console.ContainsLine("ollama"), "the manifest-owned container must appear in the inventory.");
    }

    [Test]
    public async Task Confirm_WhenRemoveWithoutYes_AndDeclined_NoTeardown()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole(confirmationAnswer: false);

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Remove));

        AssertEx.Equal(InstallerExitCode.Aborted, exit);
        AssertEx.Equal(1, console.ConfirmCallCount);
        // Declined: the inventory (WhatIf) still ran, but the Force deletion never did.
        await driver.DidNotReceive().TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Is(false), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Checksum_WhenPayloadCorrupted_AbortsBeforeMutation()
    {
        var driver = DriverFactory.CreateHappyPath();
        driver.VerifyPayloadChecksumAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Checksum mismatch for payload/rootfs/ubuntu.tar.gz.")));
        var store = new InMemoryInstallStateStore();
        var console = new RecordingInstallerConsole();

        var thrown = await AssertEx.ThrowsAsync<InvalidOperationException>(
            () => Build(driver, store, console).RunAsync(Args(InstallerVerb.Install)));

        AssertEx.Contains(thrown.Message, "Checksum mismatch");
        // Checksum is verified first in probe; no mutating phase runs.
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().LoadImageAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        AssertEx.Null(store.Manifest);
    }

    [Test]
    public async Task Install_WhenWslAbsent_SignalsRebootAndPersistsState()
    {
        var driver = DriverFactory.CreateHappyPath();
        driver.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WslProbeResult
            {
                WslFeaturePresent = false,
                Wsl2Capable = true,
                DistroPresent = false,
                FreeDiskBytes = DriverFactory.RequiredDiskBytes * 2,
                RequiredFreeDiskBytes = DriverFactory.RequiredDiskBytes
            });
        var store = new InMemoryInstallStateStore();
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Install));

        AssertEx.Equal(InstallerExitCode.RebootRequired, exit);
        await driver.Received(1).EnableWslAsync(Arg.Any<CancellationToken>());
        var state = AssertEx.NotNull(store.State);
        AssertEx.True(state.RebootPending, "reboot-pending must be persisted for the resume.");
        AssertEx.Equal(InstallPhase.Probe, state.Phase);
    }

    [Test]
    public async Task Status_WhenInstalled_ReportsInstalledWithoutMutation()
    {
        var driver = DriverFactory.CreateHappyPath();
        var store = new InMemoryInstallStateStore();
        store.SeedManifest(InstalledManifest());
        var console = new RecordingInstallerConsole();

        var exit = await Build(driver, store, console).RunAsync(Args(InstallerVerb.Status));

        AssertEx.Equal(InstallerExitCode.Success, exit);
        AssertEx.True(console.ContainsLine("Installed"), "status must report installed.");
        await driver.DidNotReceive().ImportDistroAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await driver.DidNotReceive().TeardownAsync(Arg.Any<InstallerArguments>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }
}
