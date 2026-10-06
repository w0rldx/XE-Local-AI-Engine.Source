namespace XE_Local_AI_Engine.Client.Services.ManagedPython;

using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     The Managed Python status surface (ADR 0016 §4): the shared toolchain store plus one row per feature environment,
///     and the Compute repair and remove actions.
/// </summary>
/// <remarks>
///     Every read is files and in-memory state only; nothing here provisions or spawns uv. Toolchain removal is not offered:
///     the store is machine-global, and this host cannot know which environments of other checkouts still link into it.
/// </remarks>
public sealed class ManagedPythonStatusService
{
    internal const string TrainingProfileId = "training";

    /// <summary>
    ///     <c>installed-training-runtime.json</c> does not record a revision, so both sides report this one.
    /// </summary>
    /// <remarks>Never mismatches today; record it in the state file the day the Training profile changes shape without its lockfile.</remarks>
    private const int TrainingProfileRevision = 1;

    private readonly ComputePythonEnvironment _compute;
    private readonly Func<bool> _isLinux;
    private readonly Func<bool> _isPlatformSupported;
    private readonly INodeRuntimeSettings _runtimeSettings;
    private readonly IAgentSandboxRuntimeProvider _sandbox;
    private readonly ManagedPythonToolchain _toolchain;
    private readonly ITrainingRuntimeService _training;

    internal ManagedPythonStatusService(ComputePythonEnvironment compute,
        ITrainingRuntimeService training,
        INodeRuntimeSettings runtimeSettings,
        IAgentSandboxRuntimeProvider sandbox,
        ManagedPythonToolchain toolchain,
        Func<bool>? isLinux = null,
        Func<bool>? isPlatformSupported = null)
    {
        _compute = compute ?? throw new ArgumentNullException(nameof(compute));
        _training = training ?? throw new ArgumentNullException(nameof(training));
        _runtimeSettings = runtimeSettings ?? throw new ArgumentNullException(nameof(runtimeSettings));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _toolchain = toolchain ?? throw new ArgumentNullException(nameof(toolchain));
        _isLinux = isLinux ?? OperatingSystem.IsLinux;
        _isPlatformSupported = isPlatformSupported ?? (static () => ManagedPythonPins.IsCurrentPlatformSupported);
    }

    public async Task<ManagedPythonStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        return new ManagedPythonStatus
        {
            Toolchain = new ManagedPythonToolchainStatus
            {
                UvVersion = ManagedPythonPins.UvVersion,
                UvPresent = _isPlatformSupported() && File.Exists(_toolchain.PinnedUvExecutable),
                PythonInstalls = _toolchain.ListPythonInstalls()
            },
            Environments = [GetTrainingStatus(), await GetComputeStatusAsync(cancellationToken)]
        };
    }

    /// <summary>Removes the Compute venv and its state; refused while Compute is unsupported or provisioning.</summary>
    public async Task<ManagedPythonActionResult> RemoveComputeAsync(CancellationToken cancellationToken)
    {
        return await ComputeUnsupportedReasonAsync(cancellationToken) is { } reason ? Unsupported(reason) : await _compute.RemoveAsync(cancellationToken);
    }

    /// <summary>Removes the Compute venv and provisions it again in the background; refused while unsupported or provisioning.</summary>
    public async Task<ManagedPythonActionResult> RepairComputeAsync(CancellationToken cancellationToken)
    {
        return await ComputeUnsupportedReasonAsync(cancellationToken) is { } reason ? Unsupported(reason) : await _compute.RepairAsync(cancellationToken);
    }

    private async Task<ManagedPythonEnvironmentStatus> GetComputeStatusAsync(CancellationToken cancellationToken)
    {
        return await ComputeUnsupportedReasonAsync(cancellationToken) is { } reason
            ? new ManagedPythonEnvironmentStatus
            {
                ProfileId = ComputePythonEnvironment.ProfileId,
                State = ManagedPythonEnvironmentState.Unsupported,
                Reason = reason
            }
            : await _compute.ReadStatusAsync(cancellationToken);
    }

    /// <summary>The same three gates <c>ComputeToolGateway</c> and the environment apply before a script can run, in the same order.</summary>
    private async Task<string?> ComputeUnsupportedReasonAsync(CancellationToken cancellationToken)
    {
        // An unpinned architecture (linux-arm64) must answer here: ManagedPythonPins.Current would throw further in.
        if (!_isLinux() || !_isPlatformSupported())
        {
            return "The Python compute tool is available on Linux x64 only.";
        }

        if (!await _runtimeSettings.GetComputeEnabledAsync(cancellationToken))
        {
            return "The Python compute tool is disabled on this node (Compute:Enabled=false).";
        }

        return _sandbox.Capabilities.HasFlag(SandboxProviderCapabilities.SupportsFilesystemIsolation)
            ? null
            : "This node cannot isolate the compute sandbox filesystem; install bubblewrap with user-namespace support.";
    }

    private ManagedPythonEnvironmentStatus GetTrainingStatus()
    {
        if (!_isLinux() || !_isPlatformSupported())
        {
            return TrainingStatus(ManagedPythonEnvironmentState.Unsupported, "Training is available on Linux x64 only.");
        }

        var status = _training.GetStatus();
        if (status.IsRunning)
        {
            return TrainingStatus(ManagedPythonEnvironmentState.Provisioning,
                status.Phase == TrainingRuntimePhase.Removing ? "The training runtime is being removed." : "The training runtime is being installed.");
        }

        if (status.Phase == TrainingRuntimePhase.Failed)
        {
            return TrainingStatus(ManagedPythonEnvironmentState.Failed, status.SanitizedError ?? "The last training runtime install failed.");
        }

        if (status.Installed is not { } state)
        {
            return TrainingStatus(ManagedPythonEnvironmentState.NotProvisioned, reason: null);
        }

        var installed = TrainingIdentity(state.PythonVersion, state.LockfileSha256, state.ContractVersion, state.UvVersion);
        if (_training.ResolveInterpreterPath() is null)
        {
            return TrainingStatus(ManagedPythonEnvironmentState.RepairRequired,
                "The training runtime's Python interpreter is missing. Reinstall it from the Training page.",
                installed);
        }

        // A build that lost its lockfile cannot judge the installed one against it, so the lockfile is not compared then.
        var expected = TrainingIdentity(ManagedPythonPins.PythonMinor,
            status.ShippedLockfileSha256 ?? state.LockfileSha256,
            TrainingRuntimePins.ProbeContractVersion,
            ManagedPythonPins.UvVersion);
        var mismatches = installed.MismatchesAgainst(expected);
        if (mismatches.Count > 0)
        {
            return TrainingStatus(ManagedPythonEnvironmentState.UpdateRequired,
                "The training runtime is out of date. Reinstall it from the Training page.",
                installed,
                mismatches);
        }

        // Ready with an error: a reinstall failed and the previous runtime stayed in service.
        return TrainingStatus(ManagedPythonEnvironmentState.Ready, status.SanitizedError, installed);
    }

    private static ManagedPythonEnvironmentIdentity TrainingIdentity(string pythonVersion, string lockfileSha256, int contractVersion, string uvVersion)
    {
        return new ManagedPythonEnvironmentIdentity
        {
            ProfileId = TrainingProfileId,
            PythonMinor = MinorOf(pythonVersion),
            LockfileSha256 = lockfileSha256,
            ProfileRevision = TrainingProfileRevision,
            Rid = ManagedPythonPins.Current.Rid,
            ProbeContractVersion = contractVersion,
            UvVersion = uvVersion
        };
    }

    /// <summary><c>3.13.5</c> → <c>3.13</c>; anything without two components is returned unchanged and so mismatches.</summary>
    internal static string MinorOf(string pythonVersion)
    {
        var parts = pythonVersion.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : pythonVersion;
    }

    private static ManagedPythonEnvironmentStatus TrainingStatus(ManagedPythonEnvironmentState state,
        string? reason,
        ManagedPythonEnvironmentIdentity? installed = null,
        IReadOnlyList<string>? mismatches = null)
    {
        return new ManagedPythonEnvironmentStatus
        {
            ProfileId = TrainingProfileId,
            State = state,
            Reason = reason,
            Installed = installed,
            Mismatches = mismatches ?? []
        };
    }

    private static ManagedPythonActionResult Unsupported(string reason)
    {
        return new ManagedPythonActionResult
        {
            Outcome = ManagedPythonActionOutcome.Unsupported,
            Message = reason
        };
    }
}
