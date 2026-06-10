namespace XE_Local_AI_Engine.Installer.StateMachine;

using XE_Local_AI_Engine.Installer.Cli;
using XE_Local_AI_Engine.Installer.Driver;
using XE_Local_AI_Engine.Installer.State;

/// <summary>
///     Platform-agnostic orchestration (plan §5): resolves the verb, drives the install state machine,
///     and composes the irreversible-action confirm gate. All OS actions go through
///     <see cref="IInstallerEnvironmentDriver" /> so this type is fully unit-testable with a mock driver.
/// </summary>
public sealed class InstallerOrchestrator
{
    private const string ConfirmationToken = "yes";

    private readonly IInstallerEnvironmentDriver _driver;
    private readonly IInstallStateStore _stateStore;
    private readonly IInstallerConsole _console;
    private readonly InstallContext _context;

    public InstallerOrchestrator(
        IInstallerEnvironmentDriver driver,
        IInstallStateStore stateStore,
        IInstallerConsole console,
        InstallContext context)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<int> RunAsync(InstallerArguments arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return arguments.Verb switch
        {
            InstallerVerb.Install => await RunInstallAsync(cancellationToken).ConfigureAwait(false),
            InstallerVerb.Status => await RunStatusAsync(cancellationToken).ConfigureAwait(false),
            InstallerVerb.Remove => await RunRemoveAsync(arguments, cancellationToken).ConfigureAwait(false),
            InstallerVerb.Reset => await RunResetAsync(arguments, cancellationToken).ConfigureAwait(false),
            _ => InstallerExitCode.UsageError
        };
    }

    private async Task<int> RunInstallAsync(CancellationToken cancellationToken)
    {
        var existing = await _stateStore.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            _console.WriteError($"XE Local AI Engine is already installed (version {existing.InstallerVersion}). Use `xe-installer reset` to reinstall.");
            return InstallerExitCode.AlreadyInstalled;
        }

        var state = await _stateStore.ReadStateAsync(cancellationToken).ConfigureAwait(false)
                    ?? new InstallState { Phase = InstallPhase.Probe };

        return await RunStateMachineAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunStateMachineAsync(InstallState state, CancellationToken cancellationToken)
    {
        var phase = state.Phase;
        string? appImageId = null;
        string? pulledModel = null;

        while (phase != InstallPhase.Completed)
        {
            switch (phase)
            {
                case InstallPhase.Probe:
                    var probeExit = await RunProbeAsync(cancellationToken).ConfigureAwait(false);
                    if (probeExit is not null)
                    {
                        return probeExit.Value;
                    }

                    break;

                case InstallPhase.WslEnable:
                    var probe = await _driver.ProbeAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);
                    if (!probe.WslFeaturePresent)
                    {
                        await _driver.EnableWslAsync(cancellationToken).ConfigureAwait(false);
                        await _stateStore.WriteStateAsync(
                            new InstallState { Phase = InstallPhase.Probe, RebootPending = true },
                            cancellationToken).ConfigureAwait(false);
                        _console.WriteLine(
                            "WSL2 was just enabled. A reboot may be required. After rebooting, re-run `xe-installer install` " +
                            "(it resumes from here). Keep this bundle folder in place across the reboot.");
                        return InstallerExitCode.RebootRequired;
                    }

                    break;

                case InstallPhase.DistroImport:
                    if (!await _driver.IsPhaseSatisfiedAsync(InstallerPhaseProbe.DistroImport, _context.BundlePath, cancellationToken).ConfigureAwait(false))
                    {
                        await _driver.ImportDistroAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case InstallPhase.ImageLoad:
                    appImageId = await _driver.IsPhaseSatisfiedAsync(InstallerPhaseProbe.ImageLoad, _context.BundlePath, cancellationToken).ConfigureAwait(false)
                        ? null
                        : await _driver.LoadImageAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);
                    break;

                case InstallPhase.ConfigWrite:
                    await _driver.WriteConfigAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);
                    break;

                case InstallPhase.HostAgentInstall:
                    await _driver.InstallHostAgentAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);
                    break;

                case InstallPhase.ModelPull:
                    pulledModel = await _driver.PullModelAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);
                    break;

                case InstallPhase.Verify:
                    await _driver.VerifyAsync(cancellationToken).ConfigureAwait(false);
                    break;

                case InstallPhase.Finalize:
                    await FinalizeAsync(appImageId, pulledModel, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    throw new InvalidOperationException($"Unhandled install phase '{phase}'.");
            }

            phase = NextPhase(phase);
            if (phase != InstallPhase.Completed)
            {
                await _stateStore.WriteStateAsync(new InstallState { Phase = phase }, cancellationToken).ConfigureAwait(false);
            }
        }

        _console.WriteLine("Install complete.");
        return InstallerExitCode.Success;
    }

    private async Task<int?> RunProbeAsync(CancellationToken cancellationToken)
    {
        await _driver.VerifyPayloadChecksumAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);

        var probe = await _driver.ProbeAsync(_context.BundlePath, cancellationToken).ConfigureAwait(false);

        if (!probe.Wsl2Capable)
        {
            _console.WriteError("This machine does not meet the WSL2 capability requirement (Windows build or virtualization). Aborting.");
            return InstallerExitCode.PreflightFailed;
        }

        if (probe.FreeDiskBytes < probe.RequiredFreeDiskBytes)
        {
            _console.WriteError(
                $"Insufficient free disk: {probe.FreeDiskBytes} bytes available, {probe.RequiredFreeDiskBytes} required. Aborting.");
            return InstallerExitCode.PreflightFailed;
        }

        // MED-7b: never import-over a distro this installer did not create + record. A manifest is absent
        // here (RunInstall aborts earlier when present), so a pre-existing distro means foreign ownership.
        if (probe.DistroPresent)
        {
            _console.WriteError(
                $"The `{_context.DistroName}` distro already exists but there is no install manifest. Refusing to install over it. " +
                "Run `xe-installer remove` first, or remove the distro manually.");
            return InstallerExitCode.PreflightFailed;
        }

        return null;
    }

    private async Task FinalizeAsync(string? appImageId, string? pulledModel, CancellationToken cancellationToken)
    {
        var manifest = new InstallManifest
        {
            InstallerVersion = _context.InstallerVersion,
            BundleSha256 = string.Empty,
            DistroName = _context.DistroName,
            AppImageId = appImageId ?? string.Empty,
            PulledModel = pulledModel ?? _context.BootstrapModel,
            CreatedPaths = [],
            InstalledAtUtc = DateTimeOffset.UtcNow
        };

        await _stateStore.WriteManifestAsync(manifest, cancellationToken).ConfigureAwait(false);
        await _stateStore.DeleteStateAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunStatusAsync(CancellationToken cancellationToken)
    {
        var manifest = await _stateStore.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is not null)
        {
            _console.WriteLine($"Installed (version {manifest.InstallerVersion}, model {manifest.PulledModel}, distro {manifest.DistroName}).");
            return InstallerExitCode.Success;
        }

        var state = await _stateStore.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is not null)
        {
            var rebootNote = state.RebootPending ? " (reboot pending — re-run install after rebooting)" : string.Empty;
            _console.WriteLine($"Install in progress: phase {state.Phase}{rebootNote}.");
            return InstallerExitCode.Success;
        }

        _console.WriteLine("Not installed.");
        return InstallerExitCode.Success;
    }

    private async Task<int> RunRemoveAsync(InstallerArguments arguments, CancellationToken cancellationToken)
    {
        var manifest = await _stateStore.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var state = await _stateStore.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null && state is null)
        {
            _console.WriteError("Nothing to remove — XE Local AI Engine is not installed.");
            return InstallerExitCode.NotInstalled;
        }

        // Step 1: show the manifest-attributable inventory (ownership-derived) + the dry-run ps1 -WhatIf.
        await PrintTeardownInventoryAsync(arguments.BundlePath, cancellationToken).ConfigureAwait(false);
        await _driver.TeardownAsync(arguments, dryRun: true, cancellationToken).ConfigureAwait(false);

        if (arguments.DryRun)
        {
            return InstallerExitCode.Success;
        }

        // Step 2: the installer owns the single typed gate (D6); skipped only with --yes.
        if (!arguments.AssumeYes &&
            !_console.ConfirmDestructiveAction(
                "This will permanently remove XE Local AI Engine. This cannot be undone. Type `yes` to continue: ",
                ConfirmationToken))
        {
            _console.WriteLine("Aborted — nothing was removed.");
            return InstallerExitCode.Aborted;
        }

        // Step 3: perform the deletion unattended (ps1 -Force skips its own internal gate).
        await _driver.TeardownAsync(arguments, dryRun: false, cancellationToken).ConfigureAwait(false);
        _console.WriteLine("Removed.");
        return InstallerExitCode.Success;
    }

    private async Task<int> RunResetAsync(InstallerArguments arguments, CancellationToken cancellationToken)
    {
        var manifest = await _stateStore.ReadManifestAsync(cancellationToken).ConfigureAwait(false);
        var state = await _stateStore.ReadStateAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null && state is null)
        {
            _console.WriteError("Nothing to reset — XE Local AI Engine is not installed.");
            return InstallerExitCode.NotInstalled;
        }

        // reset = full teardown (with -Force) then fresh install (D4). Honor the same confirm gate.
        await PrintTeardownInventoryAsync(arguments.BundlePath, cancellationToken).ConfigureAwait(false);
        await _driver.TeardownAsync(arguments, dryRun: true, cancellationToken).ConfigureAwait(false);

        // LOW (sec#2): --dry-run inventories only — mirror `remove`, never tear down or reinstall.
        if (arguments.DryRun)
        {
            return InstallerExitCode.Success;
        }

        if (!arguments.AssumeYes &&
            !_console.ConfirmDestructiveAction(
                "Reset will fully tear down and reinstall XE Local AI Engine. This cannot be undone. Type `yes` to continue: ",
                ConfirmationToken))
        {
            _console.WriteLine("Aborted — nothing was changed.");
            return InstallerExitCode.Aborted;
        }

        var teardown = await _driver.TeardownAsync(arguments, dryRun: false, cancellationToken).ConfigureAwait(false);

        // MED-6: ASSERT teardown completeness before reinstalling; never install over a half-removed env.
        if (!teardown.IsComplete)
        {
            _console.WriteError(
                "Reset aborted — teardown did not complete cleanly. Residual artifacts remain: " +
                string.Join(", ", teardown.Residuals) +
                ". Manual cleanup required; re-run `xe-installer remove` before installing.");
            return InstallerExitCode.TeardownIncomplete;
        }

        // Manifest + state are now gone; re-enter at probe so the same collision/preflight guards run.
        await _stateStore.DeleteManifestAsync(cancellationToken).ConfigureAwait(false);
        await _stateStore.DeleteStateAsync(cancellationToken).ConfigureAwait(false);
        return await RunStateMachineAsync(new InstallState { Phase = InstallPhase.Probe }, cancellationToken).ConfigureAwait(false);
    }

    private async Task PrintTeardownInventoryAsync(string? bundlePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            // No bundle supplied (remove/reset don't require --bundle): the ps1 -WhatIf output below still
            // lists what will be removed; the manifest-derived names just can't be previewed here.
            return;
        }

        var inventory = await _driver.BuildTeardownInventoryAsync(bundlePath, cancellationToken).ConfigureAwait(false);
        if (inventory.Count == 0)
        {
            return;
        }

        _console.WriteLine("The following manifest-attributable artifacts will be removed:");
        foreach (var item in inventory)
        {
            _console.WriteLine($"  - {item}");
        }
    }

    private static InstallPhase NextPhase(InstallPhase phase) => phase switch
    {
        InstallPhase.Probe => InstallPhase.WslEnable,
        InstallPhase.WslEnable => InstallPhase.DistroImport,
        InstallPhase.DistroImport => InstallPhase.ImageLoad,
        InstallPhase.ImageLoad => InstallPhase.ConfigWrite,
        InstallPhase.ConfigWrite => InstallPhase.HostAgentInstall,
        InstallPhase.HostAgentInstall => InstallPhase.ModelPull,
        InstallPhase.ModelPull => InstallPhase.Verify,
        InstallPhase.Verify => InstallPhase.Finalize,
        InstallPhase.Finalize => InstallPhase.Completed,
        _ => InstallPhase.Completed
    };
}
