namespace XE_Local_AI_Engine.Installer.Tests.Driver;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Installer.Driver;
using XE_Local_AI_Engine.Installer.Driver.Windows;
using XE_Local_AI_Engine.Installer.Tests.Fakes;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class WindowsInstallerDriverTests
{
    private const long Disk = 12L * 1024 * 1024 * 1024;

    private static WindowsInstallerDriver Build(RecordingProcessRunner runner, IInstallerHostConfigWriter? config = null) =>
        new(runner, config ?? new FakeHostConfigWriter(), Disk);

    [Test]
    public async Task LoadImage_StagesThenLoads_OverBashStdinSeam_AsRoot()
    {
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();

        var imageId = await Build(runner).LoadImageAsync(bundle.BundlePath);

        AssertEx.Equal(bundle.ExpectedImageId, imageId);
        // Two bash -s invocations: stage (step 1) then load (step 2), both as the root user.
        var bashCalls = runner.Invocations
            .Where(i => i.FileName == "wsl.exe" && i.ArgsContainSequence("--", "bash", "-s"))
            .ToList();
        AssertEx.Equal(2, bashCalls.Count);
        foreach (var call in bashCalls)
        {
            AssertEx.True(call.ArgsContainSequence("--distribution", "xe-engine-runtime"), "must target the runtime distro.");
            AssertEx.True(call.ArgsContainSequence("--user", "root"), "image load runs as root (docker socket).");
        }

        // Stage step stdin = script body followed by the image-tar source path as a SECOND line
        // (outside the hashed body). The path is the translated mount path for the image tar.
        var stage = bashCalls[0];
        AssertEx.NotNull(stage.StandardInput);
        AssertEx.Contains(stage.StandardInput, "read -r SRC_PATH");
        AssertEx.Contains(stage.StandardInput, "xe-node-web-server.tar.gz");
        // The path rides AFTER the script body (a later index than the `read` line).
        var stdin = stage.StandardInput!;
        AssertEx.True(
            stdin.IndexOf("xe-node-web-server.tar.gz", StringComparison.Ordinal) > stdin.IndexOf("read -r SRC_PATH", StringComparison.Ordinal),
            "the source path must follow the script body on stdin.");

        // Load step stdin = the static load body only (no per-machine path appended).
        var load = bashCalls[1];
        AssertEx.NotNull(load.StandardInput);
        AssertEx.Contains(load.StandardInput, "docker load");
        AssertEx.False(
            load.StandardInput!.Contains("xe-node-web-server.tar.gz", StringComparison.Ordinal),
            "load body carries no per-machine source path.");
    }

    [Test]
    public async Task LoadImage_WhenScriptShaMismatch_AbortsBeforeAnyProcessCall()
    {
        using var bundle = new BundleFixture();
        bundle.TamperLoadImageSha();
        var runner = new RecordingProcessRunner();

        var thrown = await AssertEx.ThrowsAsync<InvalidOperationException>(
            () => Build(runner).LoadImageAsync(bundle.BundlePath));

        AssertEx.Contains(thrown.Message, "hash verification failed");
        // The stage step ran (its SHA is valid), but the load step must abort BEFORE its bash -s call.
        var loadBashCalls = runner.Invocations.Count(i =>
            i.FileName == "wsl.exe"
            && i.ArgsContainSequence("--", "bash", "-s")
            && i.StandardInput is not null
            && i.StandardInput.Contains("docker load", StringComparison.Ordinal));
        AssertEx.Equal(0, loadBashCalls);
    }

    [Test]
    public async Task PullModel_RunsPullScript_AsRuntimeUser()
    {
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();

        var model = await Build(runner).PullModelAsync(bundle.BundlePath);

        AssertEx.Equal(bundle.BootstrapModel, model);
        var pull = runner.Invocations.Single(i =>
            i.FileName == "wsl.exe"
            && i.ArgsContainSequence("--", "bash", "-s")
            && i.StandardInput is not null
            && i.StandardInput.Contains("ollama pull", StringComparison.Ordinal));
        AssertEx.True(pull.ArgsContainSequence("--user", "xe-engine"), "model pull runs as the xe-engine runtime user.");
    }

    [Test]
    public async Task InstallHostAgent_UnblocksThenInvokesPs1_WithWindowsPowerShellContract()
    {
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();

        await Build(runner).InstallHostAgentAsync(bundle.BundlePath);

        var psCalls = runner.Invocations.Where(i => i.FileName == "powershell.exe").ToList();
        // sec#1: the MOTW unblock is a direct NTFS ADS deletion (no PowerShell), so the ONLY powershell.exe
        // call is the -File invocation — there is no `Unblock-File -Command` string to inject into.
        AssertEx.False(
            psCalls.Any(i => i.ArgumentLine.Contains("Unblock-File", StringComparison.Ordinal)),
            "unblock must NOT be a PowerShell -Command (injection surface removed).");
        var invoke = psCalls.Single(i => i.ArgsContainSequence("-File"));
        AssertEx.True(invoke.ArgsContainSequence("-NoProfile", "-ExecutionPolicy", "Bypass", "-File"), "exact HIGH-4 contract.");
        AssertEx.Contains(invoke.ArgumentLine, "install-host-agent.ps1");
        AssertEx.True(invoke.ArgsContainSequence("-SourceDirectory"), "passes -SourceDirectory.");
    }

    [Test]
    public async Task Teardown_WhenDryRun_PassesWhatIf_NotForce()
    {
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();
        var args = new InstallerArguments { Verb = InstallerVerb.Remove, BundlePath = bundle.BundlePath };

        await Build(runner).TeardownAsync(args, dryRun: true);

        var invoke = runner.Invocations.Single(i => i.FileName == "powershell.exe" && i.ArgsContainSequence("-File"));
        AssertEx.True(invoke.ArgsContainSequence("-Mode", "managed"), "always -Mode managed (RC1).");
        AssertEx.True(invoke.ArgsContainSequence("-WhatIf"), "dry-run maps to -WhatIf.");
        AssertEx.False(invoke.ArgsContainSequence("-Force"), "dry-run must NOT pass -Force.");
    }

    [Test]
    public async Task Teardown_WhenConfirmed_PassesForce_AndKeepModels()
    {
        using var bundle = new BundleFixture();
        // No real distro/ProgramData, so the completeness assertion sees them as removed.
        var runner = new RecordingProcessRunner();
        var args = new InstallerArguments { Verb = InstallerVerb.Remove, BundlePath = bundle.BundlePath, KeepModels = true };

        var result = await Build(runner).TeardownAsync(args, dryRun: false);

        var invoke = runner.Invocations.Single(i => i.FileName == "powershell.exe" && i.ArgsContainSequence("-File"));
        AssertEx.True(invoke.ArgsContainSequence("-Force"), "confirmed run passes -Force (installer owns the gate).");
        AssertEx.False(invoke.ArgsContainSequence("-WhatIf"), "confirmed run must NOT pass -WhatIf.");
        AssertEx.True(invoke.ArgsContainSequence("-KeepModels"), "--keep-models maps to -KeepModels.");
        AssertEx.True(result.DistroRemoved, "no distro present ⇒ reported removed.");
    }

    [Test]
    public async Task Teardown_WhenUninstallScriptFails_ReportsResidual()
    {
        using var bundle = new BundleFixture();
        // Fail the uninstall -File invocation (the one carrying -Force); everything else succeeds.
        var runner = new RecordingProcessRunner(invocation =>
            invocation.FileName == "powershell.exe" && invocation.ArgsContainSequence("-File") && invocation.ArgsContainSequence("-Force")
                ? RecordingProcessRunner.Failure(3, "uninstall blew up")
                : RecordingProcessRunner.Success());
        var args = new InstallerArguments { Verb = InstallerVerb.Remove, BundlePath = bundle.BundlePath };

        var result = await Build(runner).TeardownAsync(args, dryRun: false);

        AssertEx.False(result.IsComplete, "a failed uninstall script must NOT report a clean teardown.");
        AssertEx.NotEmpty(result.Residuals);
    }

    [Test]
    public async Task Verify_WhenStatusNonZero_SurfacesDiagnostic()
    {
        var runner = new RecordingProcessRunner(_ => RecordingProcessRunner.Failure(1, "address already in use"));

        var thrown = await AssertEx.ThrowsAsync<InvalidOperationException>(() => Build(runner).VerifyAsync());

        AssertEx.Contains(thrown.Message, "verification failed");
        AssertEx.Contains(thrown.Message, "port");
    }

    [Test]
    public async Task Probe_QueriesWslStatusAndList()
    {
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner(invocation =>
            invocation.ArgsContainSequence("--list", "--quiet")
                ? RecordingProcessRunner.Success("Ubuntu\nxe-engine-runtime\n")
                : RecordingProcessRunner.Success());

        var probe = await Build(runner).ProbeAsync(bundle.BundlePath);

        AssertEx.True(probe.WslFeaturePresent, "exit 0 from --status ⇒ WSL present.");
        AssertEx.True(probe.DistroPresent, "distro appears in --list --quiet output.");
        AssertEx.True(runner.Invocations.Any(i => i.ArgsContainSequence("--status")), "must probe --status.");
    }

    [Test]
    public async Task Probe_ReadsRequiredFreeDiskFromBundleMetadata()
    {
        // code#2: the disk requirement comes from bundle-metadata.json, not a hardcoded constant.
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();

        var probe = await Build(runner).ProbeAsync(bundle.BundlePath);

        AssertEx.Equal(12L * 1024 * 1024 * 1024, probe.RequiredFreeDiskBytes);
    }

    [Test]
    public async Task LoadImage_WhenExpectedImageIdMalformed_FailsClosedBeforeExec()
    {
        // code#4: a non-sha256 expected Id must abort before any in-distro execution.
        using var bundle = new BundleFixture();
        bundle.TamperExpectedImageId("not-a-digest");
        var runner = new RecordingProcessRunner();

        var thrown = await AssertEx.ThrowsAsync<InvalidOperationException>(
            () => Build(runner).LoadImageAsync(bundle.BundlePath));

        AssertEx.Contains(thrown.Message, "XE_EXPECTED_IMAGE_ID");
        AssertEx.Equal(0, runner.Invocations.Count);
    }

    [Test]
    public async Task IsPhaseSatisfied_DistroImport_TrueWhenDistroListed()
    {
        // code#5: the distro-import phase is a no-op when the runtime distro is already registered.
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner(invocation =>
            invocation.ArgsContainSequence("--list", "--quiet")
                ? RecordingProcessRunner.Success("xe-engine-runtime\n")
                : RecordingProcessRunner.Success());

        var satisfied = await Build(runner).IsPhaseSatisfiedAsync(InstallerPhaseProbe.DistroImport, bundle.BundlePath);

        AssertEx.True(satisfied, "a listed distro satisfies the import phase.");
    }

    [Test]
    public async Task IsPhaseSatisfied_ImageLoad_TrueWhenInspectSucceeds()
    {
        // code#5: the image-load phase is a no-op when the expected config Id already exists in the daemon.
        using var bundle = new BundleFixture();
        // docker image inspect exits 0 (image present) — and so does everything else in this probe path.
        var runner = new RecordingProcessRunner(_ => RecordingProcessRunner.Success());

        var satisfied = await Build(runner).IsPhaseSatisfiedAsync(InstallerPhaseProbe.ImageLoad, bundle.BundlePath);

        AssertEx.True(satisfied, "a present image Id satisfies the load phase.");
    }

    [Test]
    public async Task IsPhaseSatisfied_ImageLoad_FalseWhenInspectFails()
    {
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner(invocation =>
            invocation.ArgsContainSequence("docker", "image", "inspect")
                ? RecordingProcessRunner.Failure(1, "No such image")
                : RecordingProcessRunner.Success());

        var satisfied = await Build(runner).IsPhaseSatisfiedAsync(InstallerPhaseProbe.ImageLoad, bundle.BundlePath);

        AssertEx.False(satisfied, "an absent image Id means the load phase must run.");
    }

    [Test]
    public async Task BuildTeardownInventory_IncludesFixedPaths_AndManifestOwnedContainers()
    {
        // code#6 / sec LOW-3: InstallerContainerOwnership.Owns is wired into the pre-confirm inventory.
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();

        var inventory = await Build(runner).BuildTeardownInventoryAsync(bundle.BundlePath);

        AssertEx.Contains(inventory, item => item.Contains("xe-engine-runtime", StringComparison.Ordinal));
        AssertEx.Contains(inventory, item => item.Contains("ollama", StringComparison.Ordinal));
        AssertEx.Contains(inventory, item => item.Contains("xe-node-web-server", StringComparison.Ordinal));
    }

    [Test]
    public async Task WriteConfig_DeliversManifestToDistro_OverRootBashSeam()
    {
        // HIGH-1: the manifest is delivered into the distro (NOT written to the Windows runtime.json).
        using var bundle = new BundleFixture();
        var runner = new RecordingProcessRunner();

        await Build(runner).WriteConfigAsync(bundle.BundlePath);

        var deliver = runner.Invocations.Single(i =>
            i.FileName == "wsl.exe"
            && i.ArgsContainSequence("--", "bash", "-s")
            && i.StandardInput is not null
            && i.StandardInput.Contains("manifest.yaml", StringComparison.Ordinal));
        AssertEx.True(deliver.ArgsContainSequence("--user", "root"), "manifest delivery runs as root.");
        // The manifest content rides stdin AFTER the script body (outside the hashed region).
        AssertEx.Contains(deliver.StandardInput, "schemaVersion: 1");
    }

    [Test]
    public async Task WriteConfig_WhenWriteManifestScriptMissing_FailsLoud()
    {
        // HIGH-1: until the packaging lane vendors write-manifest.sh, manifest delivery must fail loudly.
        using var bundle = new BundleFixture();
        bundle.RemoveWriteManifestScript();
        var runner = new RecordingProcessRunner();

        var thrown = await AssertEx.ThrowsAsync<FileNotFoundException>(
            () => Build(runner).WriteConfigAsync(bundle.BundlePath));

        AssertEx.Contains(thrown.Message, "write-manifest.sh");
    }
}
