namespace XE_Local_AI_Engine.Tests.ManagedPython;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TUnit.Core.Enums;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;
using XE_Local_AI_Engine.Client.Services.ManagedPython;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using static Providers.Training.TrainingRuntimeTestInfrastructure;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     State derivation of the Managed Python status surface over temp directories and a substituted Training service:
///     nothing provisions, nothing spawns. The Compute disk states are <c>ComputePythonEnvironmentTests</c>'.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ManagedPythonStatusServiceTests : IDisposable
{
    private const string InstalledLockfileSha = "aaaa";

    private readonly HttpClient _http = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-managed-python-" + Guid.NewGuid().ToString("N"));
    private readonly ITrainingRuntimeService _training = Substitute.For<ITrainingRuntimeService>();

    public ManagedPythonStatusServiceTests()
    {
        ScriptsDirectory = Path.Combine(_root, "scripts");
        Toolchain = new ManagedPythonToolchain(Path.Combine(_root, "python"));
        _ = Directory.CreateDirectory(ScriptsDirectory);
        File.WriteAllText(Path.Combine(ScriptsDirectory, "pyproject.toml"), "[project]\nname = \"xe-compute-runtime\"\n");
        File.WriteAllText(Path.Combine(ScriptsDirectory, "uv.lock"), "version = 1\n");
        _training.ResolveInterpreterPath().Returns("/venv/bin/python");
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Idle));
    }

    private string ScriptsDirectory { get; }

    private ManagedPythonToolchain Toolchain { get; }

    public void Dispose()
    {
        _http.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Training_WhileAnInstallOrRemoveRuns_IsProvisioning()
    {
        foreach (var phase in new[]
                 {
                     TrainingRuntimePhase.AcquiringUv,
                     TrainingRuntimePhase.InstallingPackages,
                     TrainingRuntimePhase.Removing
                 })
        {
            _training.GetStatus().Returns(TrainingStatus(phase, isRunning: true));

            AssertEx.Equal(ManagedPythonEnvironmentState.Provisioning, (await TrainingRowAsync()).State, phase.ToString());
        }
    }

    [Test]
    public async Task Training_AfterAFailedInstall_IsFailed_WithTheSanitizedError()
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Failed, error: "The training runtime install was cancelled."));

        var row = await TrainingRowAsync();

        AssertEx.Equal(ManagedPythonEnvironmentState.Failed, row.State);
        AssertEx.Equal("The training runtime install was cancelled.", row.Reason);
    }

    [Test]
    public async Task Training_WhenIdleWithNothingInstalled_IsNotProvisioned()
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Idle));

        AssertEx.Equal(ManagedPythonEnvironmentState.NotProvisioned, (await TrainingRowAsync()).State);
    }

    [Test]
    public async Task Training_WhenTheInstalledRecordMatchesTheShippedOne_IsReady_EvenAcrossAUvBump()
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Ready, Installed(uvVersion: "0.1.0")));

        var row = await TrainingRowAsync();

        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, row.State, "the uv version is informational only");
        AssertEx.Null(row.Reason);
        AssertEx.Equal("0.1.0", AssertEx.NotNull(row.Installed).UvVersion);
    }

    [Test]
    public async Task Training_WhenAReinstallFailedButTheOldRuntimeSurvived_IsReady_AndCarriesTheError()
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Ready, Installed(), error: "Verifying the provisioned training runtime failed."));

        var row = await TrainingRowAsync();

        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, row.State);
        AssertEx.Equal("Verifying the provisioned training runtime failed.", row.Reason);
    }

    [Test]
    [Arguments("bbbb", "3.13.15", TrainingRuntimePins.ProbeContractVersion, "lockfile")]
    [Arguments(InstalledLockfileSha, "3.12.9", TrainingRuntimePins.ProbeContractVersion, "pythonMinor")]
    [Arguments(InstalledLockfileSha, "3.13.15", TrainingRuntimePins.ProbeContractVersion + 1, "probeContract")]
    public async Task Training_WhenTheIdentityDiffers_IsUpdateRequired_AndNamesTheField(string shippedSha,
        string pythonVersion,
        int contractVersion,
        string expectedMismatch)
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Ready,
            Installed(pythonVersion: pythonVersion, contractVersion: contractVersion),
            shippedSha: shippedSha));

        var row = await TrainingRowAsync();

        AssertEx.Equal(ManagedPythonEnvironmentState.UpdateRequired, row.State);
        AssertEx.Equal(expectedMismatch, string.Join(",", row.Mismatches));
    }

    [Test]
    public async Task Training_WhenTheBuildShipsNoLockfile_DoesNotFlagTheInstalledOne()
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Ready, Installed(), shippedSha: null));

        AssertEx.Equal(ManagedPythonEnvironmentState.Ready, (await TrainingRowAsync()).State);
    }

    [Test]
    public async Task Training_WhenTheInterpreterIsMissing_IsRepairRequired()
    {
        _training.GetStatus().Returns(TrainingStatus(TrainingRuntimePhase.Ready, Installed()));
        _training.ResolveInterpreterPath().Returns((string?)null);

        AssertEx.Equal(ManagedPythonEnvironmentState.RepairRequired, (await TrainingRowAsync()).State);
    }

    [Test]
    public async Task BothEnvironments_OffLinux_AreUnsupported_WithoutAskingTheTrainingService()
    {
        using var compute = CreateCompute();
        var service = CreateService(compute, isLinux: false);

        var status = await service.GetStatusAsync(CancellationToken.None);

        AssertEx.True(status.Environments.All(static row => row.State == ManagedPythonEnvironmentState.Unsupported));
        AssertEx.True(status.Environments.All(static row => row.Reason?.Contains("Linux", StringComparison.Ordinal) == true));
        _ = _training.DidNotReceive().GetStatus();
    }

    [Test]
    public async Task BothEnvironments_OnAnUnpinnedArchitecture_AreUnsupported_AndUvIsNotLookedUp()
    {
        using var compute = CreateCompute();
        var service = CreateService(compute, platformSupported: false);

        var status = await service.GetStatusAsync(CancellationToken.None);

        AssertEx.True(status.Environments.All(static row => row.State == ManagedPythonEnvironmentState.Unsupported));
        AssertEx.True(status.Environments.All(static row => row.Reason?.Contains("Linux x64", StringComparison.Ordinal) == true));
        AssertEx.False(status.Toolchain.UvPresent);
        AssertEx.Equal(ManagedPythonActionOutcome.Unsupported, (await service.RepairComputeAsync(CancellationToken.None)).Outcome);
        _ = _training.DidNotReceive().GetStatus();
    }

    [Test]
    public async Task Compute_WhenDisabled_IsUnsupported_AndRepairAndRemoveAreRefused()
    {
        using var compute = CreateCompute();
        var service = CreateService(compute, enabled: false);

        var row = await ComputeRowAsync(service);
        var repair = await service.RepairComputeAsync(CancellationToken.None);
        var remove = await service.RemoveComputeAsync(CancellationToken.None);

        AssertEx.Equal(ManagedPythonEnvironmentState.Unsupported, row.State);
        AssertEx.Contains(row.Reason, "Compute:Enabled=false");
        AssertEx.Equal(ManagedPythonActionOutcome.Unsupported, repair.Outcome);
        AssertEx.Equal(ManagedPythonActionOutcome.Unsupported, remove.Outcome);
        AssertEx.Equal(AssertEx.NotNull(row.Reason), repair.Message);
    }

    [Test]
    public async Task Compute_WithoutFilesystemIsolation_IsUnsupported()
    {
        using var compute = CreateCompute();
        var service = CreateService(compute, isolation: false);

        var row = await ComputeRowAsync(service);

        AssertEx.Equal(ManagedPythonEnvironmentState.Unsupported, row.State);
        AssertEx.Contains(row.Reason, "isolate");
    }

    [Test]
    public async Task Compute_WhenSupported_ReportsTheEnvironmentsOwnState()
    {
        using var compute = CreateCompute();
        var service = CreateService(compute);

        AssertEx.Equal(ManagedPythonEnvironmentState.NotProvisioned, (await ComputeRowAsync(service)).State);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Toolchain_ReportsThePinnedUvAndTheRealCPythonInstalls_SkippingAliasesAndLocks()
    {
        using var compute = CreateCompute();
        var service = CreateService(compute);
        AssertEx.False((await service.GetStatusAsync(CancellationToken.None)).Toolchain.UvPresent);

        SeedCachedUv(Toolchain.Root);
        var install = Directory.CreateDirectory(Path.Combine(Toolchain.PythonInstallDirectory, "cpython-3.13.15-linux-x86_64-gnu"));
        _ = Directory.CreateSymbolicLink(Path.Combine(Toolchain.PythonInstallDirectory, "cpython-3.13-linux-x86_64-gnu"), install.FullName);
        _ = Directory.CreateDirectory(Path.Combine(Toolchain.PythonInstallDirectory, ".temp"));
        await File.WriteAllTextAsync(Path.Combine(Toolchain.PythonInstallDirectory, ".lock"), string.Empty);

        var toolchain = (await service.GetStatusAsync(CancellationToken.None)).Toolchain;

        AssertEx.True(toolchain.UvPresent);
        AssertEx.Equal(ManagedPythonPins.UvVersion, toolchain.UvVersion);
        AssertEx.Equal("cpython-3.13.15-linux-x86_64-gnu", string.Join(",", toolchain.PythonInstalls));
    }

    [Test]
    public async Task BothShippedProfiles_RequireExactlyThePinnedPythonMinor()
    {
        var minor = Version.Parse(ManagedPythonPins.PythonMinor);
        var expected = $"requires-python = \">={minor.Major}.{minor.Minor},<{minor.Major}.{minor.Minor + 1}\"";
        foreach (var profile in new[]
                 {
                     "tools/training/pyproject.toml",
                     "tools/compute/pyproject.toml"
                 })
        {
            AssertEx.Contains(await File.ReadAllTextAsync(RepositoryPaths.Combine(profile)), expected, message: profile);
        }
    }

    private async Task<ManagedPythonEnvironmentStatus> TrainingRowAsync()
    {
        using var compute = CreateCompute();
        var status = await CreateService(compute).GetStatusAsync(CancellationToken.None);
        return status.Environments.Single(static row => row.ProfileId == ManagedPythonStatusService.TrainingProfileId);
    }

    private static async Task<ManagedPythonEnvironmentStatus> ComputeRowAsync(ManagedPythonStatusService service)
    {
        var status = await service.GetStatusAsync(CancellationToken.None);
        return status.Environments.Single(static row => row.ProfileId == ComputePythonEnvironment.ProfileId);
    }

    private ComputePythonEnvironment CreateCompute()
    {
        return new ComputePythonEnvironment(new UvBinaryAcquirer(_http),
            SucceedingRunner(),
            NullLogger<ComputePythonEnvironment>.Instance,
            Path.Combine(_root, "compute-runtime"),
            ScriptsDirectory,
            Toolchain);
    }

    private ManagedPythonStatusService CreateService(ComputePythonEnvironment compute,
        bool isLinux = true,
        bool enabled = true,
        bool isolation = true,
        bool platformSupported = true)
    {
        var sandbox = Substitute.For<IAgentSandboxRuntimeProvider>();
        sandbox.Capabilities.Returns(isolation ? SandboxProviderCapabilities.SupportsFilesystemIsolation : SandboxProviderCapabilities.None);
        return new ManagedPythonStatusService(compute,
            _training,
            SeededNodeRuntimeSettings.FromSeed("Compute:Enabled", enabled),
            sandbox,
            Toolchain,
            () => isLinux,
            () => platformSupported);
    }

    private static InstalledTrainingRuntimeState Installed(string pythonVersion = "3.13.15",
        int contractVersion = TrainingRuntimePins.ProbeContractVersion,
        string uvVersion = ManagedPythonPins.UvVersion)
    {
        return new InstalledTrainingRuntimeState(uvVersion, "digest", pythonVersion, InstalledLockfileSha, contractVersion, DateTimeOffset.UnixEpoch);
    }

    private static TrainingRuntimeStatus TrainingStatus(TrainingRuntimePhase phase,
        InstalledTrainingRuntimeState? installed = null,
        bool isRunning = false,
        string? error = null,
        string? shippedSha = InstalledLockfileSha)
    {
        return new TrainingRuntimeStatus
        {
            Phase = phase,
            IsRunning = isRunning,
            Terminal = !isRunning,
            LogLines = [],
            LogStartSequence = 0,
            SanitizedError = error,
            Installed = installed,
            StartedAtUtc = null,
            CompletedAtUtc = null,
            ShippedLockfileSha256 = shippedSha
        };
    }
}
