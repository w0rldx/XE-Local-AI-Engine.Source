namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

public sealed class StableDiffusionSourceRuntimeFoundationTests
{
    [Test]
    public void ResolveExact_LinuxCuda_ReturnsNullInsteadOfCpuFallback()
    {
        AssertEx.Null(StableDiffusionReleasePins.ResolveExact(OSPlatform.Linux, Architecture.X64, SdGpuBackend.Cuda));
    }

    [Test]
    public async Task BinaryManager_LinuxCudaWithoutManagedRuntime_FailsBeforeHttpInsteadOfDownloadingCpuPin()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var manager = new StableDiffusionCppBinaryManager(http,
            temp.Path,
            StableDiffusionReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            installedRuntimeStore: store,
            managedSourceSignal: new StableDiffusionManagedSourceBuildSignal());

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => manager.EnsureBinaryAsync(SdGpuBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task BinaryManager_InvalidManagedRuntime_FailsClosedWithoutPrebuiltDownload()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        await store.WriteAsync(State(temp.Path) with
        {
            Validity = StableDiffusionInstalledRuntimeValidity.Invalid,
            InvalidReason = "corrupt"
        }, CancellationToken.None);
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var manager = new StableDiffusionCppBinaryManager(http,
            temp.Path,
            StableDiffusionReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            installedRuntimeStore: store,
            managedSourceSignal: new StableDiffusionManagedSourceBuildSignal());

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => manager.EnsureBinaryAsync(SdGpuBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task BinaryManager_ActiveManagedRuntimeShaMismatch_WritesTombstoneAndNeverFallsBack()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var state = State(temp.Path);
        Directory.CreateDirectory(state.SourceBuildPath!);
        var serverPath = Path.Combine(state.SourceBuildPath!, "sd-server");
        await File.WriteAllTextAsync(serverPath, "tampered");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(serverPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await store.WriteAsync(state, CancellationToken.None);
        using var handler = new CountingHandler();
        using var http = new HttpClient(handler);
        var signal = new StableDiffusionManagedSourceBuildSignal();
        signal.SetActive(SdGpuBackend.Cuda);
        var signalVersion = signal.Version;
        var manager = new StableDiffusionCppBinaryManager(http,
            temp.Path,
            StableDiffusionReleasePins.PinnedTag,
            OSPlatform.Linux,
            Architecture.X64,
            installedRuntimeStore: store,
            managedSourceSignal: signal);

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => manager.EnsureBinaryAsync(SdGpuBackend.Cuda, CancellationToken.None));

        AssertEx.Equal(StableDiffusionInstalledRuntimeValidity.Invalid,
            AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)).Validity);
        AssertEx.Null(signal.ActiveBackend);
        AssertEx.True(signal.Version > signalVersion);
        AssertEx.Equal(expected: 0, handler.CallCount);
    }

    [Test]
    public async Task Selector_ManagedSelectionPrecedesHardwareProbe()
    {
        var profiler = Substitute.For<IHardwareProfiler>();
        var signal = new StableDiffusionManagedSourceBuildSignal();
        signal.SetActive(SdGpuBackend.Cuda);
        var selector = new SdGpuBackendSelector(profiler,
            isWindows: false,
            Substitute.For<IVulkanDeviceProbe>(),
            managedSourceSignal: signal);

        var backend = await selector.SelectBackendAsync(CancellationToken.None);

        AssertEx.Equal(SdGpuBackend.Cuda, backend);
        await profiler.DidNotReceive().GetProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InstalledRuntimeStore_CorruptActiveRecord_ReturnsInvalidDesiredSelectionTombstone()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        await store.WriteAsync(State(temp.Path), CancellationToken.None);
        var statePath = Path.Combine(temp.Path, "stable-diffusion.cpp", "installed-runtime.json");
        await File.WriteAllTextAsync(statePath, "{broken");

        var recovered = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(StableDiffusionInstalledRuntimeValidity.Invalid, recovered.Validity);
        AssertEx.Equal(SdGpuBackend.Cuda, recovered.DesiredBackend);
        AssertEx.Equal(StableDiffusionCppSourceSelection.Official, recovered.SourceSelection);
    }

    [Test]
    public async Task InstalledRuntimeStore_SemanticallyInvalidEnums_FallBackToDesiredSelectionTombstone()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        await store.WriteAsync(State(temp.Path), CancellationToken.None);
        var statePath = Path.Combine(temp.Path, "stable-diffusion.cpp", "installed-runtime.json");
        await File.WriteAllTextAsync(statePath,
            $$"""
              {
                "validity": 99,
                "desiredBackend": 99,
                "sourceRepository": null,
                "sourceCommit": "{{StableDiffusionReleasePins.PinnedSourceCommitSha}}",
                "sourceSelection": 99,
                "sourceRevisionMode": 99,
                "installedAtUtc": "{{DateTimeOffset.UtcNow:O}}"
              }
              """);

        var recovered = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));

        AssertEx.Equal(StableDiffusionInstalledRuntimeValidity.Invalid, recovered.Validity);
        AssertEx.Equal(SdGpuBackend.Cuda, recovered.DesiredBackend);
        AssertEx.Equal(StableDiffusionCppSourceSelection.Official, recovered.SourceSelection);
    }

    [Test]
    public void ActivityGate_MutationAndEvictionAdmissionsAreAtomic()
    {
        var gate = new ImageRuntimeActivityGate();
        using var job = AssertEx.NotNull(gate.TryAcquireJobLease());

        AssertEx.Null(gate.TryAcquireMutationReservation());
        AssertEx.Null(gate.TryAcquireEvictionReservation());
        AssertEx.Equal(expected: 1, gate.GetSnapshot().ActiveJobCount);

        job.Dispose();
        using var mutation = AssertEx.NotNull(gate.TryAcquireMutationReservation());
        AssertEx.Null(gate.TryAcquireJobLease());
        AssertEx.Null(gate.TryAcquireSpawnReadinessLease());
    }

    [Test]
    public void SourceRequest_OfficialNormalizationIsIdempotentAndPinsCanonicalProvenance()
    {
        var request = new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cuda, StableDiffusionCppSourceSelection.Official);

        var once = StableDiffusionCppSourceBuildRequestValidation.Normalize(request);
        var twice = StableDiffusionCppSourceBuildRequestValidation.Normalize(once);

        AssertEx.Equal(once, twice);
        AssertEx.Equal(StableDiffusionCppSourceBuildRequestValidation.OfficialRepository, twice.Repository);
        AssertEx.Equal("1a13107bac236b0cd6fadbf5c264f3644874ba4f", StableDiffusionReleasePins.PinnedSourceCommitSha);
    }

    [Test]
    public void BuildCMakeConfigureArguments_CudaForcesCudaOnAndVulkanOff()
    {
        var arguments = StableDiffusionCppSourceBuildService.BuildCMakeConfigureArguments("/src", "/build", SdGpuBackend.Cuda);

        AssertEx.Contains(arguments, "-DSD_CUDA=ON");
        AssertEx.Contains(arguments, "-DSD_VULKAN=OFF");
        AssertEx.False(arguments.Contains("-DSD_VULKAN=ON", StringComparer.Ordinal));
    }

    [Test]
    public async Task StartAsync_ActiveJob_ReturnsRuntimeBusyWithoutStartingCommand()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var gate = new ImageRuntimeActivityGate();
        using var job = AssertEx.NotNull(gate.TryAcquireJobLease());
        var runner = new BlockingRunner();
        using var service = CreateService(temp.Path, store, gate, runner);

        var result = await service.StartAsync(new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cpu, StableDiffusionCppSourceSelection.Official),
            CancellationToken.None);

        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.RuntimeBusy, result.Outcome);
        AssertEx.Equal(expected: 0, runner.CallCount);
    }

    [Test]
    public async Task StartAsync_DetachedBuildIsSingleFlightAndCancellationCompletes()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var gate = new ImageRuntimeActivityGate();
        var runner = new BlockingRunner();
        using var service = CreateService(temp.Path, store, gate, runner);
        var request = new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cpu, StableDiffusionCppSourceSelection.Official);

        var first = await service.StartAsync(request, CancellationToken.None);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = await service.StartAsync(request, CancellationToken.None);

        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.Started, first.Outcome);
        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.AlreadyRunning, second.Outcome);
        AssertEx.True(service.Cancel());
        await service.ShutdownAsync(CancellationToken.None);
        AssertEx.Equal(StableDiffusionCppSourceBuildPhase.Cancelled, service.GetStatus().Phase);
    }

    [Test]
    public async Task SourceBuild_OfficialBuildChecksOutExactCommitBeforeInitializingSubmodulesAndAdoptsVerifiedState()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var gate = new ImageRuntimeActivityGate();
        var runner = new SuccessfulRunner();
        using var service = CreateService(temp.Path, store, gate, runner);

        var result = await service.StartAsync(new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cuda, StableDiffusionCppSourceSelection.Official),
            CancellationToken.None);
        await WaitForTerminalAsync(service);

        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.Started, result.Outcome);
        var gitCommands = runner.Commands.Where(static command => command.FileName == "git").ToArray();
        AssertEx.False(gitCommands.Any(static command => GitVerb(command.Arguments) == "clone"));
        AssertEx.True(gitCommands.All(static command =>
            ContainsPair(command.Arguments, "protocol.allow=never")
            && ContainsPair(command.Arguments, "protocol.https.allow=always")
            && ContainsPair(command.Arguments, "credential.helper=")
            && ContainsPair(command.Arguments, "core.askPass=")));
        var (_, fetchArguments) = gitCommands.Single(static command => GitVerb(command.Arguments) == "fetch");
        AssertEx.True(fetchArguments.Contains("--depth=1", StringComparer.Ordinal));
        AssertEx.True(fetchArguments.Contains("--no-tags", StringComparer.Ordinal));
        AssertEx.True(fetchArguments.Contains("--no-recurse-submodules", StringComparer.Ordinal));
        AssertEx.Equal(StableDiffusionReleasePins.PinnedSourceCommitSha, fetchArguments[^1]);
        var (_, checkoutArguments) = gitCommands.Single(static command => GitVerb(command.Arguments) == "checkout");
        AssertEx.True(checkoutArguments.TakeLast(3).SequenceEqual(["checkout", "--detach", "FETCH_HEAD"]));
        var (_, submoduleArguments) = gitCommands.Single(static command => GitVerb(command.Arguments) == "submodule");
        AssertEx.True(submoduleArguments.TakeLast(4).SequenceEqual(["submodule", "update", "--init", "--recursive"]));
        var installed = AssertEx.NotNull(await store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(StableDiffusionInstalledRuntimeValidity.Active, installed.Validity);
        AssertEx.Equal(StableDiffusionReleasePins.PinnedSourceCommitSha, installed.SourceCommit);
        AssertEx.Equal(SdGpuBackend.Cuda, installed.DesiredBackend);
        AssertEx.False(Directory.EnumerateDirectories(Path.GetDirectoryName(installed.SourceBuildPath!)!,
                                    ".process-*",
                                    SearchOption.AllDirectories)
                                .Any());
        if (!OperatingSystem.IsWindows())
        {
            var installRoot = Path.Combine(temp.Path,
                "stable-diffusion.cpp",
                "managed",
                "cuda",
                StableDiffusionReleasePins.PinnedSourceCommitSha);
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(installRoot));
            var installedServer = Path.Combine(installed.SourceBuildPath!, "sd-server");
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(installedServer));
        }

        AssertEx.False(gate.GetSnapshot().MutationReserved);
    }

    [Test]
    public async Task SourceBuild_CustomExactCommitUsesShallowFetchAndFetchHeadCheckout()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var customCommit = new string('c', 40);
        var runner = new SuccessfulRunner(customCommit);
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), runner);

        var result = await service.StartAsync(new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cpu,
                StableDiffusionCppSourceSelection.Custom,
                "https://github.com/example/stable-diffusion.cpp",
                customCommit,
                AcknowledgeCustomSourceRisk: true),
            CancellationToken.None);
        await WaitForTerminalAsync(service);

        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.Started, result.Outcome);
        AssertEx.Equal(StableDiffusionCppSourceBuildPhase.Completed, service.GetStatus().Phase);
        var gitCommands = runner.Commands.Where(static command => command.FileName == "git").ToArray();
        var (_, fetchArguments) = gitCommands.Single(static command => GitVerb(command.Arguments) == "fetch");
        AssertEx.Equal(customCommit, fetchArguments[^1]);
        AssertEx.True(fetchArguments.Contains("--depth=1", StringComparer.Ordinal));
        var (_, checkoutArguments) = gitCommands.Single(static command => GitVerb(command.Arguments) == "checkout");
        AssertEx.True(checkoutArguments.TakeLast(3).SequenceEqual(["checkout", "--detach", "FETCH_HEAD"]));
        AssertEx.Equal(customCommit, AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)).SourceCommit);
    }

    [Test]
    public async Task SourceBuild_AdoptionStoreFailure_RestoresPreviousRuntimeDirectoryAndRecord()
    {
        using var temp = new TempDirectory();
        using var innerStore = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var previous = State(temp.Path);
        var commitRoot = Path.Combine(temp.Path,
            "stable-diffusion.cpp",
            "managed",
            "cuda",
            StableDiffusionReleasePins.PinnedSourceCommitSha);
        var previousServerDirectory = Path.Combine(commitRoot, "bin");
        Directory.CreateDirectory(previousServerDirectory);
        var previousServer = Path.Combine(previousServerDirectory, "sd-server");
        await File.WriteAllTextAsync(previousServer, "previous");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(previousServer, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        previous = previous with
        {
            SourceBuildPath = previousServerDirectory
        };
        await innerStore.WriteAsync(previous, CancellationToken.None);
        var failingStore = new FailingWriteStore(innerStore);
        using var service = CreateService(temp.Path, failingStore, new ImageRuntimeActivityGate(), new SuccessfulRunner());

        var result = await service.StartAsync(new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cuda, StableDiffusionCppSourceSelection.Official),
            CancellationToken.None);
        await WaitForTerminalAsync(service);

        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.Started, result.Outcome);
        AssertEx.Equal(StableDiffusionCppSourceBuildPhase.Failed, service.GetStatus().Phase);
        AssertEx.Equal("previous", await File.ReadAllTextAsync(previousServer));
        AssertEx.Equal(previous, AssertEx.NotNull(await innerStore.ReadAsync(CancellationToken.None)));
    }

    [Test]
    public async Task SourceBuild_SuccessfulReplacementRemovesPreviousCommitRoot()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var previousCommit = new string('b', 40);
        var previousRoot = Path.Combine(temp.Path, "stable-diffusion.cpp", "managed", "cpu", previousCommit);
        var previousServerDirectory = Path.Combine(previousRoot, "bin");
        Directory.CreateDirectory(previousServerDirectory);
        await File.WriteAllTextAsync(Path.Combine(previousServerDirectory, "sd-server"), "previous");
        var previous = State(temp.Path) with
        {
            DesiredBackend = SdGpuBackend.Cpu,
            SourceRepository = "https://github.com/example/stable-diffusion.cpp",
            SourceCommit = previousCommit,
            SourceSelection = StableDiffusionCppSourceSelection.Custom,
            SourceRevisionMode = StableDiffusionCppSourceRevisionMode.ExplicitCommit,
            SourceRequestedCommit = previousCommit,
            SourceBuildPath = previousServerDirectory
        };
        await store.WriteAsync(previous, CancellationToken.None);
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new SuccessfulRunner());

        var result = await service.StartAsync(new StableDiffusionCppSourceBuildRequest(SdGpuBackend.Cuda, StableDiffusionCppSourceSelection.Official),
            CancellationToken.None);
        await WaitForTerminalAsync(service);

        AssertEx.Equal(StableDiffusionCppSourceBuildStartOutcome.Started, result.Outcome);
        AssertEx.Equal(StableDiffusionCppSourceBuildPhase.Completed, service.GetStatus().Phase);
        AssertEx.False(Directory.Exists(previousRoot));
        AssertEx.Equal(StableDiffusionReleasePins.PinnedSourceCommitSha,
            AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)).SourceCommit);
    }

    [Test]
    public async Task RecoverAsync_InterruptedSameCommitAdoptionRestoresBackupAndPreviousRecord()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var previous = State(temp.Path);
        var destination = Path.Combine(temp.Path,
            "stable-diffusion.cpp",
            "managed",
            "cuda",
            previous.SourceCommit);
        var buildId = Guid.NewGuid();
        var backup = Path.Combine(Path.GetDirectoryName(destination)!,
            $".backup-{previous.SourceCommit}-{buildId:N}");
        Directory.CreateDirectory(Path.Combine(destination, "bin"));
        Directory.CreateDirectory(Path.Combine(backup, "bin"));
        await File.WriteAllTextAsync(Path.Combine(destination, "bin", "sd-server"), "new");
        await File.WriteAllTextAsync(Path.Combine(backup, "bin", "sd-server"), "previous");
        previous = previous with
        {
            SourceBuildPath = Path.Combine(destination, "bin")
        };
        var replacement = previous with
        {
            ServerSha256 = new string('b', 64),
            InstalledAtUtc = previous.InstalledAtUtc.AddMinutes(1)
        };
        await store.WriteAsync(previous, CancellationToken.None);
        var buildRoot = Path.Combine(temp.Path, "stable-diffusion.cpp", "source-build");
        Directory.CreateDirectory(buildRoot);
        await File.WriteAllTextAsync(Path.Combine(buildRoot, "adoption-journal.json"),
            JsonSerializer.Serialize(new StableDiffusionCppAdoptionJournal(buildId,
                SdGpuBackend.Cuda,
                previous.SourceCommit,
                HadPreviousDestination: true,
                previous,
                replacement)));
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new BlockingRunner());

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.Equal("previous", await File.ReadAllTextAsync(Path.Combine(destination, "bin", "sd-server")));
        AssertEx.False(Directory.Exists(backup));
        AssertEx.False(File.Exists(Path.Combine(buildRoot, "adoption-journal.json")));
        AssertEx.Equal(previous, AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)));
    }

    [Test]
    public async Task RecoverAsync_PreSwapJournalValidatesPreviousBytesAndRetriesFailedTreeCleanup()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var previous = State(temp.Path);
        var destination = Path.Combine(temp.Path,
            "stable-diffusion.cpp",
            "managed",
            "cuda",
            previous.SourceCommit);
        var serverDirectory = Path.Combine(destination, "bin");
        Directory.CreateDirectory(serverDirectory);
        var serverPath = Path.Combine(serverDirectory, "sd-server");
        await File.WriteAllTextAsync(serverPath, "previous");
        previous = previous with
        {
            SourceBuildPath = serverDirectory,
            ServerSha256 = await Sha256Async(serverPath)
        };
        await store.WriteAsync(previous, CancellationToken.None);
        var buildId = Guid.NewGuid();
        var failed = Path.Combine(Path.GetDirectoryName(destination)!,
            $".failed-{previous.SourceCommit}-{buildId:N}");
        Directory.CreateDirectory(failed);
        await File.WriteAllTextAsync(Path.Combine(failed, "orphan"), "replacement");
        var journalPath = await WriteJournalAsync(temp.Path, new StableDiffusionCppAdoptionJournal(buildId,
            SdGpuBackend.Cuda,
            previous.SourceCommit,
            HadPreviousDestination: true,
            previous,
            previous with
            {
                InstalledAtUtc = previous.InstalledAtUtc.AddMinutes(1)
            }));
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new BlockingRunner());

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(failed));
        AssertEx.False(File.Exists(journalPath));
        AssertEx.Equal("previous", await File.ReadAllTextAsync(serverPath));
    }

    [Test]
    public async Task RecoverAsync_ReplacementWithoutBackupRetainsJournalAndFailsClosed()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var previous = State(temp.Path);
        var destination = Path.Combine(temp.Path,
            "stable-diffusion.cpp",
            "managed",
            "cuda",
            previous.SourceCommit);
        var serverDirectory = Path.Combine(destination, "bin");
        Directory.CreateDirectory(serverDirectory);
        var serverPath = Path.Combine(serverDirectory, "sd-server");
        await File.WriteAllTextAsync(serverPath, "replacement");
        previous = previous with
        {
            SourceBuildPath = serverDirectory,
            ServerSha256 = Convert.ToHexStringLower(SHA256.HashData("previous"u8))
        };
        await store.WriteAsync(previous, CancellationToken.None);
        var buildId = Guid.NewGuid();
        var journalPath = await WriteJournalAsync(temp.Path, new StableDiffusionCppAdoptionJournal(buildId,
            SdGpuBackend.Cuda,
            previous.SourceCommit,
            HadPreviousDestination: true,
            previous,
            previous with
            {
                ServerSha256 = await Sha256Async(serverPath),
                InstalledAtUtc = previous.InstalledAtUtc.AddMinutes(1)
            }));
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new BlockingRunner());

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => service.RecoverAsync(CancellationToken.None));

        AssertEx.True(File.Exists(journalPath));
        AssertEx.Equal("replacement", await File.ReadAllTextAsync(serverPath));
        AssertEx.Equal(previous, AssertEx.NotNull(await store.ReadAsync(CancellationToken.None)));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task CommandRunner_StreamsOutputBeforeProcessCompletes()
    {
        using var temp = new TempDirectory();
        var runner = new StableDiffusionSourceCommandRunner();
        var firstLine = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = runner.RunAsync("/bin/sh",
            ["-c", "printf 'first\\n'; sleep 0.2; printf 'second\\n'"],
            temp.Path,
            line =>
            {
                if (line == "first")
                {
                    firstLine.TrySetResult();
                }
            },
            TimeSpan.FromSeconds(5),
            captureOutput: true,
            CancellationToken.None);

        await firstLine.Task.WaitAsync(TimeSpan.FromSeconds(2));

        AssertEx.False(run.IsCompleted);
        var result = await run;
        AssertEx.Contains(result.StandardOutput, "first", StringComparison.Ordinal);
        AssertEx.Contains(result.StandardOutput, "second", StringComparison.Ordinal);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task CommandRunner_TimeoutTerminatesHungCommand()
    {
        using var temp = new TempDirectory();
        var runner = new StableDiffusionSourceCommandRunner();

        await AssertEx.ThrowsAsync<TimeoutException>(() => runner.RunAsync("/bin/sh",
            ["-c", "sleep 30"],
            temp.Path,
            _ => { },
            TimeSpan.FromMilliseconds(100),
            captureOutput: false,
            CancellationToken.None));
    }

    [Test]
    [NotInParallel]
    [ExcludeOn(OS.Windows)]
    public async Task CommandRunner_ScrubsInheritedEnvironmentAndClosesStandardInput()
    {
        var previousCudaHome = Environment.GetEnvironmentVariable("CUDA_HOME");
        var previousCudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        var previousPoison = Environment.GetEnvironmentVariable("XE_SOURCE_BUILD_POISON");
        try
        {
            Environment.SetEnvironmentVariable("CUDA_HOME", "/opt/test-cuda-home");
            Environment.SetEnvironmentVariable("CUDA_PATH", "/opt/test-cuda-path");
            Environment.SetEnvironmentVariable("XE_SOURCE_BUILD_POISON", "secret");
            using var temp = new TempDirectory();
            var runner = new StableDiffusionSourceCommandRunner();

            var result = await runner.RunAsync("/bin/sh",
                [
                    "-c",
                    "test \"$CUDA_HOME\" = /opt/test-cuda-home || exit 40; test \"$CUDA_PATH\" = /opt/test-cuda-path || exit 41; test -z \"${XE_SOURCE_BUILD_POISON+x}\" || exit 42; test \"$HOME\" = \"$PWD/.process-home\" || exit 43; test \"$TMPDIR\" = \"$PWD/.process-tmp\" || exit 44; if IFS= read -r value; then exit 45; fi; printf 'hardened\\n'"
                ],
                temp.Path,
                _ => { },
                TimeSpan.FromSeconds(5),
                captureOutput: true,
                CancellationToken.None);

            AssertEx.Equal(expected: 0, result.ExitCode);
            AssertEx.Contains(result.StandardOutput, "hardened", StringComparison.Ordinal);
            AssertOwnerOnlyDirectory(Path.Combine(temp.Path, ".process-home"));
            AssertOwnerOnlyDirectory(Path.Combine(temp.Path, ".process-tmp"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CUDA_HOME", previousCudaHome);
            Environment.SetEnvironmentVariable("CUDA_PATH", previousCudaPath);
            Environment.SetEnvironmentVariable("XE_SOURCE_BUILD_POISON", previousPoison);
        }
    }

    [Test]
    [NotInParallel]
    [ExcludeOn(OS.Windows)]
    public async Task PrerequisiteProbe_ScrubsInheritedEnvironmentAndClosesStandardInput()
    {
        var previousCudaHome = Environment.GetEnvironmentVariable("CUDA_HOME");
        var previousCudaPath = Environment.GetEnvironmentVariable("CUDA_PATH");
        var previousPoison = Environment.GetEnvironmentVariable("XE_SOURCE_BUILD_POISON");
        try
        {
            Environment.SetEnvironmentVariable("CUDA_HOME", "/opt/test-cuda-home");
            Environment.SetEnvironmentVariable("CUDA_PATH", "/opt/test-cuda-path");
            Environment.SetEnvironmentVariable("XE_SOURCE_BUILD_POISON", "secret");
            using var temp = new TempDirectory();
            var home = Path.Combine(temp.Path, ".process-home");
            var processTemp = Path.Combine(temp.Path, ".process-tmp");

            var result = await StableDiffusionCppSourceBuildPrerequisiteProbe.ProbeToolForTestsAsync("/bin/sh",
                [
                    "-c",
                    "test \"$CUDA_HOME\" = /opt/test-cuda-home || exit 40; test \"$CUDA_PATH\" = /opt/test-cuda-path || exit 41; test -z \"${XE_SOURCE_BUILD_POISON+x}\" || exit 42; test \"$HOME\" = \"$1\" || exit 43; test \"$TMPDIR\" = \"$2\" || exit 44; if IFS= read -r value; then exit 45; fi; printf 'hardened\\n'",
                    "probe", home, processTemp
                ],
                "hardened probe",
                CancellationToken.None,
                temp.Path);

            AssertEx.True(result.Satisfied);
            AssertEx.Equal("hardened", result.Detail);
            AssertOwnerOnlyDirectory(home);
            AssertOwnerOnlyDirectory(processTemp);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CUDA_HOME", previousCudaHome);
            Environment.SetEnvironmentVariable("CUDA_PATH", previousCudaPath);
            Environment.SetEnvironmentVariable("XE_SOURCE_BUILD_POISON", previousPoison);
        }
    }

    [Test]
    public void SourceProcessHardening_RemovesPoisonAndPreservesCudaAllowlist()
    {
        using var temp = new TempDirectory();
        var startInfo = new ProcessStartInfo();
        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = "/toolchain/bin";
        startInfo.Environment["LANG"] = "en_US.UTF-8";
        startInfo.Environment["LC_ALL"] = "en_US.UTF-8";
        startInfo.Environment["CUDA_HOME"] = "/opt/cuda-home";
        startInfo.Environment["CUDA_PATH"] = "/opt/cuda-path";
        startInfo.Environment["GITHUB_TOKEN"] = "poison";
        startInfo.Environment["GIT_CONFIG_COUNT"] = "1";

        StableDiffusionSourceProcessHardening.Configure(startInfo, temp.Path);

        AssertEx.Equal("/toolchain/bin", startInfo.Environment["PATH"]);
        AssertEx.Equal("en_US.UTF-8", startInfo.Environment["LANG"]);
        AssertEx.Equal("en_US.UTF-8", startInfo.Environment["LC_ALL"]);
        AssertEx.Equal("/opt/cuda-home", startInfo.Environment["CUDA_HOME"]);
        AssertEx.Equal("/opt/cuda-path", startInfo.Environment["CUDA_PATH"]);
        AssertEx.False(startInfo.Environment.ContainsKey("GITHUB_TOKEN"));
        AssertEx.False(startInfo.Environment.ContainsKey("GIT_CONFIG_COUNT"));
        AssertEx.Equal("0", startInfo.Environment["GIT_TERMINAL_PROMPT"]);
        AssertEx.Equal("1", startInfo.Environment["GIT_CONFIG_NOSYSTEM"]);
        AssertEx.True(startInfo.RedirectStandardInput);
        AssertEx.True(Directory.Exists(startInfo.Environment["HOME"]));
        AssertEx.True(Directory.Exists(startInfo.Environment["TMPDIR"]));
    }

    [Test]
    public async Task Lifecycle_RecoveryFailureIsLoggedAndRethrown()
    {
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        var logger = new RecordingLogger<StableDiffusionCppSourceBuildLifecycle>();
        service.RecoverAsync(Arg.Any<CancellationToken>())
               .Returns<Task>(_ => throw new StableDiffusionRuntimeException("ambiguous adoption"));
        var lifecycle = new StableDiffusionCppSourceBuildLifecycle(service, logger);

        await AssertEx.ThrowsAsync<StableDiffusionRuntimeException>(() => lifecycle.StartAsync(CancellationToken.None));

        AssertEx.Equal(expected: 1, logger.ErrorCount);
    }

    [Test]
    public async Task Lifecycle_StartupCancellationIsRethrownWithoutErrorLogging()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        var logger = new RecordingLogger<StableDiffusionCppSourceBuildLifecycle>();
        service.RecoverAsync(cancellation.Token)
               .Returns<Task>(_ => throw new OperationCanceledException(cancellation.Token));
        var lifecycle = new StableDiffusionCppSourceBuildLifecycle(service, logger);

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => lifecycle.StartAsync(cancellation.Token));

        AssertEx.Equal(expected: 0, logger.ErrorCount);
    }

    /// <summary>
    ///     The mirror of the startup contract above: on the way DOWN, a cancelled token is the host saying the
    ///     shutdown budget is spent, not an error to propagate. Host.StopAsync aggregates and rethrows anything a
    ///     hosted service throws, so an escaping cancellation here ends the process with an unhandled exception
    ///     instead of a clean exit — which is what a desktop user gets when closing the app after a slow drain.
    /// </summary>
    [Test]
    public async Task Lifecycle_ShutdownCancellationIsAbsorbedSoTheHostExitsCleanly()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var service = Substitute.For<IStableDiffusionCppSourceBuildService>();
        var logger = new RecordingLogger<StableDiffusionCppSourceBuildLifecycle>();
        service.ShutdownAsync(cancellation.Token)
               .Returns<Task>(_ => throw new OperationCanceledException(cancellation.Token));
        var lifecycle = new StableDiffusionCppSourceBuildLifecycle(service, logger);

        await lifecycle.StopAsync(cancellation.Token);

        AssertEx.Equal(expected: 0, logger.ErrorCount);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task PrerequisiteProbe_CallerCancellationTerminatesSpawnedProcess()
    {
        using var temp = new TempDirectory();
        var pidPath = Path.Combine(temp.Path, "probe.pid");
        using var cancellation = new CancellationTokenSource();
        var probe = StableDiffusionCppSourceBuildPrerequisiteProbe.ProbeToolForTestsAsync("/bin/sh",
            ["-c", $"printf '%s' $$ > '{pidPath}'; sleep 30"],
            "test probe",
            cancellation.Token,
            temp.Path);
        using var waitForPid = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!File.Exists(pidPath))
        {
            await Task.Delay(10, waitForPid.Token);
        }

        await cancellation.CancelAsync();
        await AssertEx.ThrowsAsync<OperationCanceledException>(() => probe);
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));

        AssertEx.False(IsProcessRunning(pid));
    }

    [Test]
    public async Task RemoveAsync_DeletesExactCommitInstallRootAndPreservesSibling()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var state = State(temp.Path);
        var commitRoot = Path.Combine(temp.Path, "stable-diffusion.cpp", "managed", "cuda", state.SourceCommit);
        var serverDirectory = Path.Combine(commitRoot, "build", "bin");
        var sibling = Path.Combine(temp.Path, "stable-diffusion.cpp", "managed", "cuda", "sibling");
        Directory.CreateDirectory(serverDirectory);
        Directory.CreateDirectory(sibling);
        await File.WriteAllTextAsync(Path.Combine(serverDirectory, "sd-server"), "server");
        await store.WriteAsync(state with
        {
            SourceBuildPath = serverDirectory
        }, CancellationToken.None);
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new BlockingRunner());

        var result = await service.RemoveAsync(CancellationToken.None);

        AssertEx.Equal(StableDiffusionCppSourceBuildRemoveOutcome.Removed, result.Outcome);
        AssertEx.False(Directory.Exists(commitRoot));
        AssertEx.True(Directory.Exists(sibling));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task RemoveAsync_InvalidTombstoneWithoutRecordedPath_StillDeletesExactCommitRoot()
    {
        using var temp = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var state = State(temp.Path);
        var commitRoot = Path.Combine(temp.Path, "stable-diffusion.cpp", "managed", "cuda", state.SourceCommit);
        Directory.CreateDirectory(commitRoot);
        await File.WriteAllTextAsync(Path.Combine(commitRoot, "orphan"), "runtime");
        await store.WriteAsync(state with
        {
            Validity = StableDiffusionInstalledRuntimeValidity.Invalid,
            SourceBuildPath = null,
            ServerSha256 = null,
            InvalidReason = "corrupt"
        }, CancellationToken.None);
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new BlockingRunner());

        var result = await service.RemoveAsync(CancellationToken.None);

        AssertEx.Equal(StableDiffusionCppSourceBuildRemoveOutcome.Removed, result.Outcome);
        AssertEx.False(Directory.Exists(commitRoot));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    public async Task RemoveAsync_InvalidTombstoneWithUntrustedRecordedPath_DeletesOnlyDerivedManagedRoot()
    {
        using var temp = new TempDirectory();
        using var outside = new TempDirectory();
        using var store = new StableDiffusionInstalledRuntimeStore(temp.Path);
        var state = State(temp.Path);
        var commitRoot = Path.Combine(temp.Path, "stable-diffusion.cpp", "managed", "cuda", state.SourceCommit);
        Directory.CreateDirectory(commitRoot);
        await File.WriteAllTextAsync(Path.Combine(commitRoot, "orphan"), "runtime");
        var outsideFile = Path.Combine(outside.Path, "preserve");
        await File.WriteAllTextAsync(outsideFile, "outside");
        await store.WriteAsync(state with
        {
            Validity = StableDiffusionInstalledRuntimeValidity.Invalid,
            SourceBuildPath = outside.Path,
            ServerSha256 = null,
            InvalidReason = "unsafe path"
        }, CancellationToken.None);
        using var service = CreateService(temp.Path, store, new ImageRuntimeActivityGate(), new BlockingRunner());

        var result = await service.RemoveAsync(CancellationToken.None);

        AssertEx.Equal(StableDiffusionCppSourceBuildRemoveOutcome.Removed, result.Outcome);
        AssertEx.False(Directory.Exists(commitRoot));
        AssertEx.True(File.Exists(outsideFile));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    private static StableDiffusionCppSourceBuildService CreateService(string cacheRoot,
        IStableDiffusionInstalledRuntimeStore store,
        IImageRuntimeActivityGate gate,
        IStableDiffusionSourceCommandRunner runner)
    {
        var prerequisites = Substitute.For<IStableDiffusionCppSourceBuildPrerequisiteProbe>();
        prerequisites.ProbeAsync(Arg.Any<SdGpuBackend>(), Arg.Any<CancellationToken>())
                     .Returns(new StableDiffusionCppSourceBuildPrerequisiteReport(true, []));
        return new StableDiffusionCppSourceBuildService(prerequisites,
            store,
            new StableDiffusionManagedSourceBuildSignal(),
            gate,
            new NullPublisher(),
            NullLogger<StableDiffusionCppSourceBuildService>.Instance,
            cacheRoot,
            runner,
            isLinux: true);
    }

    private static async Task WaitForTerminalAsync(IStableDiffusionCppSourceBuildService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!service.GetStatus().Terminal)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private static StableDiffusionInstalledRuntimeState State(string cacheRoot)
    {
        return new StableDiffusionInstalledRuntimeState(StableDiffusionInstalledRuntimeValidity.Active,
            SdGpuBackend.Cuda,
            StableDiffusionCppSourceBuildRequestValidation.OfficialRepository,
            StableDiffusionReleasePins.PinnedSourceCommitSha,
            StableDiffusionCppSourceSelection.Official,
            StableDiffusionCppSourceRevisionMode.EnginePinned,
            SourceRequestedCommit: null,
            Path.Combine(cacheRoot, "stable-diffusion.cpp", "managed", "cuda", StableDiffusionReleasePins.PinnedSourceCommitSha, "build", "bin"),
            new string('a', 64),
            DateTimeOffset.UtcNow);
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<string> WriteJournalAsync(string cacheRoot, StableDiffusionCppAdoptionJournal journal)
    {
        var buildRoot = Path.Combine(cacheRoot, "stable-diffusion.cpp", "source-build");
        Directory.CreateDirectory(buildRoot);
        var journalPath = Path.Combine(buildRoot, "adoption-journal.json");
        await File.WriteAllTextAsync(journalPath, JsonSerializer.Serialize(journal));
        return journalPath;
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }

    private static string? GitVerb(IReadOnlyList<string> arguments)
    {
        var index = 0;
        while (index + 1 < arguments.Count && arguments[index] == "-c")
        {
            index += 2;
        }

        return index < arguments.Count ? arguments[index] : null;
    }

    private static bool ContainsPair(IReadOnlyList<string> arguments, string configuration)
    {
        for (var index = 0; index + 1 < arguments.Count; index++)
        {
            if (arguments[index] == "-c" && arguments[index + 1] == configuration)
            {
                return true;
            }
        }

        return false;
    }

    private static void AssertOwnerOnlyDirectory(string path)
    {
        AssertEx.True(Directory.Exists(path));
        if (!OperatingSystem.IsWindows())
        {
            AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(path));
        }
    }

    private sealed class NullPublisher : IStableDiffusionCppSourceBuildEventPublisher
    {
        public Task PublishStatusAsync(StableDiffusionCppSourceBuildStatusEvent statusEvent, CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public int ErrorCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                ErrorCount++;
            }
        }
    }

    private sealed class BlockingRunner : IStableDiffusionSourceCommandRunner
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }

        public async Task<StableDiffusionSourceCommandResult> RunAsync(string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string> onOutput,
            TimeSpan timeout,
            bool captureOutput,
            CancellationToken ct)
        {
            CallCount++;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new StableDiffusionSourceCommandResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class SuccessfulRunner(string? resolvedCommit = null) : IStableDiffusionSourceCommandRunner
    {
        private readonly string _resolvedCommit = resolvedCommit ?? StableDiffusionReleasePins.PinnedSourceCommitSha;

        public List<(string FileName, IReadOnlyList<string> Arguments)> Commands { get; } = [];

        public async Task<StableDiffusionSourceCommandResult> RunAsync(string fileName,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            Action<string> onOutput,
            TimeSpan timeout,
            bool captureOutput,
            CancellationToken ct)
        {
            Commands.Add((fileName, [.. arguments]));
            if (fileName == "git" && GitVerb(arguments) == "init")
            {
                Directory.CreateDirectory(arguments[^1]);
            }

            if (fileName == "git" && GitVerb(arguments) == "rev-parse")
            {
                return new StableDiffusionSourceCommandResult(0,
                    _resolvedCommit + Environment.NewLine,
                    string.Empty);
            }

            if (fileName == "cmake" && arguments.Count > 0 && arguments[0] == "-S")
            {
                var buildFlagIndex = arguments.ToList().IndexOf("-B");
                var buildDirectory = arguments[buildFlagIndex + 1];
                Directory.CreateDirectory(buildDirectory);
                var cuda = arguments.Contains("-DSD_CUDA=ON", StringComparer.Ordinal);
                var vulkan = arguments.Contains("-DSD_VULKAN=ON", StringComparer.Ordinal);
                await File.WriteAllLinesAsync(Path.Combine(buildDirectory, "CMakeCache.txt"),
                    [
                        $"SD_CUDA:BOOL={(cuda ? "ON" : "OFF")}",
                        $"SD_VULKAN:BOOL={(vulkan ? "ON" : "OFF")}"
                    ],
                    ct);
                if (cuda || vulkan)
                {
                    var backendArtifact = Path.Combine(buildDirectory,
                        "ggml",
                        cuda ? "libggml-cuda.a" : "libggml-vulkan.a");
                    Directory.CreateDirectory(Path.GetDirectoryName(backendArtifact)!);
                    await File.WriteAllTextAsync(backendArtifact, "backend", ct);
                }

                var serverDirectory = Path.Combine(buildDirectory, "bin");
                Directory.CreateDirectory(serverDirectory);
                var serverPath = Path.Combine(serverDirectory, "sd-server");
                await File.WriteAllTextAsync(serverPath, "server", ct);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(serverPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
            }

            return new StableDiffusionSourceCommandResult(0, string.Empty, string.Empty);
        }
    }

    private sealed class FailingWriteStore(IStableDiffusionInstalledRuntimeStore inner) : IStableDiffusionInstalledRuntimeStore
    {
        private int _writeCount;

        public Task<StableDiffusionInstalledRuntimeState?> ReadAsync(CancellationToken ct)
        {
            return inner.ReadAsync(ct);
        }

        public async Task WriteAsync(StableDiffusionInstalledRuntimeState state, CancellationToken ct)
        {
            await inner.WriteAsync(state, ct);
            if (Interlocked.Increment(ref _writeCount) == 1)
            {
                throw new IOException("simulated partial state-store failure");
            }
        }

        public Task DeleteAsync(CancellationToken ct)
        {
            return inner.DeleteAsync(ct);
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-sdcpp-source-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
