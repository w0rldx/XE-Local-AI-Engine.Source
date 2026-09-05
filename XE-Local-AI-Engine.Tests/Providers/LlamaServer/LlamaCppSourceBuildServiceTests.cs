namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

[NotInParallel]
public sealed class LlamaCppSourceBuildServiceTests
{
    [Test]
    [ExcludeOn(OS.Linux)]
    public async Task Start_NonLinux_FailsBeforeProbeOrActivityReservation()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var probe = new CountingReadyProbe();
        var activity = new LlamaCppSourceBuildActivity();
        using var service = new LlamaCppSourceBuildService(probe, new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), activity, new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() =>
            service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official),
                CancellationToken.None));

        // Arguments were reversed here — AssertEx.Contains takes (actual, expectedSubstring), so this asserted that the
        // literal "Linux only" contains the whole exception message. The early return above means this body runs only
        // off Linux, so the mistake never executed until this suite was first run on Windows.
        AssertEx.Contains(exception.Message, "Linux only");
        AssertEx.Equal(0, probe.CallCount);
        AssertEx.Null(activity.ActiveBuildId);
        AssertEx.False(service.GetStatus().IsRunning);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_ConcurrentCaller_WaitsForStartTransactionAndCannotRecoverWinnerWorkTree()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        WriteScript(Path.Combine(stubs, "git"), "#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then sleep 30; fi\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var probe = new GatedReadyProbe();
        using var service = new LlamaCppSourceBuildService(probe, new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new LlamaCppSourceBuildActivity(), new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);
        var request = new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official);

        var winner = service.StartAsync(request, CancellationToken.None);
        await probe.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loser = service.StartAsync(request, CancellationToken.None);

        probe.Release.SetResult();
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, (await winner).Outcome);
        var marker = Path.Combine(temp.Path, "llama.cpp", "source-build", ".work", ".build-in-progress");
        await AssertEx.EventuallyAsync(() => File.Exists(marker), TimeSpan.FromSeconds(5));
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.AlreadyRunning, (await loser).Outcome);
        AssertEx.Equal(1, probe.CallCount);
        AssertEx.True(File.Exists(marker));

        await service.ShutdownAsync(CancellationToken.None);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task StartupStop_BlockingBuild_CancelsAndAwaitsProcessTree()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var pidFile = Path.Combine(temp.Path, "git.pid");
        WriteScript(Path.Combine(stubs, "git"), $"#!/bin/sh\necho $$ > '{pidFile}'\nwhile true; do sleep 1; done\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new LlamaCppSourceBuildActivity(), new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);
        var startup = new CudaBuildStartupService(service, store, signal, NullLogger<CudaBuildStartupService>.Instance);

        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started,
            (await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official),
                CancellationToken.None)).Outcome);
        await AssertEx.EventuallyAsync(() => File.Exists(pidFile), TimeSpan.FromSeconds(5));
        var pid = int.Parse(await File.ReadAllTextAsync(pidFile));

        await startup.StopAsync(CancellationToken.None);

        AssertEx.Equal(LlamaCppSourceBuildPhase.Cancelled, service.GetStatus().Phase);
        AssertEx.False(Directory.Exists($"/proc/{pid}"));
    }

    [Test]
    public async Task AppendLog_ConcurrentCallbacks_PersistAndPublishInIdenticalOrder()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var publisher = new RecordingPublisher();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new LlamaCppSourceBuildActivity(), publisher, NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(() => service.AppendLog($"line-{index}"))));
        await service.FlushPublisherAsync();

        var persisted = service.GetStatus().LogLines;
        var published = publisher.Events.SelectMany(statusEvent => statusEvent.AppendedLogLines).ToArray();
        AssertEx.True(persisted.SequenceEqual(published));
    }

    [Test]
    public async Task AppendLog_PastServerRing_ReportsMonotonicSequences()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var publisher = new RecordingPublisher();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new LlamaCppSourceBuildActivity(), publisher, NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        for (var index = 0; index < 450; index++)
        {
            service.AppendLog($"line-{index}");
        }

        await service.FlushPublisherAsync();

        var status = service.GetStatus();
        AssertEx.Equal(expected: 400, status.LogLines.Count);
        AssertEx.Equal(expected: 50L, status.LogStartSequence);
        AssertEx.Equal("line-50", status.LogLines[0]);
        AssertEx.True(publisher.Events.Select(static statusEvent => statusEvent.AppendedLogStartSequence)
                               .SequenceEqual(Enumerable.Range(0, 450).Select(static value => (long)value)));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_WhenRuntimeMutationLeaseUnavailable_ReturnsBusyAfterSingleProbe()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var probe = new CountingReadyProbe();
        using var service = new LlamaCppSourceBuildService(probe, new CapturingBinaryManager(store, signal), store, signal,
            new BusySupervisor(), new LlamaCppSourceBuildActivity(), new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        var outcome = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official),
            CancellationToken.None);

        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.ProcessesRunning, outcome.Outcome);
        AssertEx.Equal(1, outcome.RunningProcessCount);
        AssertEx.Equal(1, probe.CallCount);
        AssertEx.False(service.GetStatus().IsRunning);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_WhenDiskInsufficient_ReturnsProbeDetailsWithoutRuntimeAdmission()
    {
        var report = new LlamaCppSourceBuildPrerequisiteReport(false,
            [new LlamaCppSourceBuildPrerequisiteItem("free-disk", false, "insufficient")]);
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new FixedReportProbe(report),
            new CapturingBinaryManager(store, signal),
            store,
            signal,
            new LeaseOnlySupervisor(),
            new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        var result = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official),
            CancellationToken.None);

        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.InsufficientDisk, result.Outcome);
        AssertEx.Equal(report, result.Prerequisites);
        AssertEx.False(service.GetStatus().IsRunning);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_WhenToolMissing_ReturnsProbeDetailsWithoutRuntimeAdmission()
    {
        var report = new LlamaCppSourceBuildPrerequisiteReport(false,
            [new LlamaCppSourceBuildPrerequisiteItem("cmake", false, "missing")]);
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new FixedReportProbe(report),
            new CapturingBinaryManager(store, signal),
            store,
            signal,
            new LeaseOnlySupervisor(),
            new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        var result = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official),
            CancellationToken.None);

        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.MissingPrerequisites, result.Outcome);
        AssertEx.Equal(report, result.Prerequisites);
        AssertEx.False(service.GetStatus().IsRunning);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_WhenAnotherBuildOwnsReservation_ReturnsBusyWithoutReplacingOwner()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        ILlamaCppSourceBuildActivity activity = new LlamaCppSourceBuildActivity();
        var owner = Guid.NewGuid();
        AssertEx.True(activity.TryReserve(owner));
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), activity, new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        var outcome = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official),
            CancellationToken.None);

        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.RuntimeBusy, outcome.Outcome);
        AssertEx.Equal(owner, activity.ActiveBuildId);
        AssertEx.False(service.GetStatus().IsRunning);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Cancel_CustomCpu_IsRejectedByLegacyPredicateAndGenericCancelRetainsActiveRuntime()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var (previousState, previousServer) = await SeedActiveRuntimeAsync(temp.Path, store);
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        WriteScript(Path.Combine(stubs, "git"), "#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then sleep 5; for last; do :; done; mkdir -p \"$last\"; exit 0; fi\n");
        using var path = new PathScope(stubs);
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new LlamaCppSourceBuildActivity(), new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        var start = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork", AcknowledgeCustomSourceRisk: true), CancellationToken.None);
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, start.Outcome);
        var firstBuildId = service.GetStatus().CurrentBuild!.BuildId;
        AssertEx.False(service.CancelLegacyPinnedCuda());
        AssertEx.True(service.Cancel());
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Cancelled, service.GetStatus().Phase);
        AssertEx.True(File.Exists(previousServer));
        AssertEx.Equal(previousState, await store.ReadAsync(CancellationToken.None));

        var repeated = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Custom,
            "https://github.com/example/fork", AcknowledgeCustomSourceRisk: true), CancellationToken.None);
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, repeated.Outcome);
        AssertEx.True(service.GetStatus().CurrentBuild!.BuildId != firstBuildId);
        AssertEx.Equal(expected: 0, service.GetStatus().LogLines.Count);
        AssertEx.True(service.Cancel());
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Recover_ActiveRuntimeWithoutFitHelper_DiscardsTreeRecordAndSignal()
    {
        using var temp = new TempDirectory();
        using var store = new InstalledRuntimeStore(temp.Path);
        var (_, server) = await SeedActiveRuntimeAsync(temp.Path, store);
        var tree = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(server)!, "..", ".."));
        File.Delete(Path.Combine(Path.GetDirectoryName(server)!, "llama-fit-params"));
        var signal = new CudaManagedBuildSignal();
        signal.SetActive(GpuVariant.Cpu);
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
            new CapturingBinaryManager(store, signal),
            store,
            signal,
            new LeaseOnlySupervisor(),
            new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        await service.RecoverAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(tree));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
        AssertEx.Null(signal.ActiveVariant);
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_WhenResolvedCommitMismatches_StopsBeforeCmakeAndRecordsNothing()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var cmakeMarker = Path.Combine(temp.Path, "cmake-ran");
        WriteScript(Path.Combine(stubs, "git"),
            "#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '0000000000000000000000000000000000000000'; exit 0; fi\n");
        WriteScript(Path.Combine(stubs, "cmake"), $"#!/bin/sh\ntouch '{cmakeMarker}'\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(), new CapturingBinaryManager(store, signal), store, signal,
            new LeaseOnlySupervisor(), new LlamaCppSourceBuildActivity(), new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance, temp.Path);

        await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Failed, service.GetStatus().Phase);
        AssertEx.False(File.Exists(cmakeMarker));
        AssertEx.Null(await store.ReadAsync(CancellationToken.None));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_WhenUnexpectedAdoptionFailure_UsesGenericSourceBuildMessage()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        WriteScript(Path.Combine(stubs, "git"),
            $"#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '{LlamaCppReleasePins.PinnedSourceCommitSha}'; exit 0; fi\n");
        WriteScript(Path.Combine(stubs, "cmake"),
            "#!/bin/sh\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-server\"; chmod 755 \"$2/bin/llama-server\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-fit-params\"; chmod 755 \"$2/bin/llama-fit-params\"; exit 0; fi\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var publisher = new OrderedFailingPublisher();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
            new CapturingBinaryManager(store, signal, failAdoption: true),
            store,
            signal,
            new LeaseOnlySupervisor(),
            new LlamaCppSourceBuildActivity(),
            publisher,
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Failed, service.GetStatus().Phase);
        AssertEx.Equal("The source build failed unexpectedly.", service.GetStatus().SanitizedError);
        await service.FlushPublisherAsync();
        AssertEx.Equal(1, publisher.MaxConcurrent);
        AssertEx.True(publisher.Events.Count > 0);
        AssertEx.True(publisher.Events[^1].Terminal);
        AssertEx.True(publisher.Events.All(statusEvent => statusEvent.CurrentBuild?.BuildId == service.GetStatus().CurrentBuild?.BuildId));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Quantizer_BuiltAndAdopted_OnSourceBuild()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var cmakeArgs = Path.Combine(temp.Path, "cmake.txt");
        WriteScript(Path.Combine(stubs, "git"),
            $"#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '{LlamaCppReleasePins.PinnedSourceCommitSha}'; exit 0; fi\nexit 0\n");

        // The stub emits all three targets, so the assertion below proves the quantizer travelled through the staged →
        // adopted swap with the server, not merely that cmake was asked for it.
        WriteScript(Path.Combine(stubs, "cmake"),
            $"#!/bin/sh\necho \"$@\" >> '{cmakeArgs}'\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; for tool in llama-server llama-fit-params llama-quantize llama-perplexity; do printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/$tool\"; chmod 755 \"$2/bin/$tool\"; done; exit 0; fi\nexit 0\n");
        using var path = new PathScope(stubs);

        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var manager = new CapturingBinaryManager(store, signal);
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
            manager,
            store,
            signal,
            new LeaseOnlySupervisor(),
            new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        var outcome = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, outcome.Outcome);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));
        AssertEx.Equal(LlamaCppSourceBuildPhase.Completed, service.GetStatus().Phase);

        var args = await File.ReadAllTextAsync(cmakeArgs);
        AssertEx.True(args.Contains("--target llama-server llama-fit-params llama-quantize llama-perplexity", StringComparison.Ordinal),
            "The quantizer and the perplexity tool must be cmake build targets.");

        var adoptedBin = Path.Combine(temp.Path, "llama.cpp", "source-build", "active", "build", "bin");
        AssertEx.True(File.Exists(Path.Combine(adoptedBin, "llama-quantize")),
            "The built quantizer must be adopted alongside the server.");
        AssertEx.Equal(Path.Combine(adoptedBin, "llama-quantize"), LlamaCppToolBinaries.TryResolveQuantizer(adoptedBin));
        AssertEx.Equal(Path.Combine(adoptedBin, "llama-perplexity"), LlamaCppToolBinaries.TryResolvePerplexity(adoptedBin));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Quantizer_Missing_DoesNotFailAdoption()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        WriteScript(Path.Combine(stubs, "git"),
            $"#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '{LlamaCppReleasePins.PinnedSourceCommitSha}'; exit 0; fi\nexit 0\n");

        // Server + fit-params only: the quantizer is off the inference path, so a runtime without one must still adopt.
        WriteScript(Path.Combine(stubs, "cmake"),
            "#!/bin/sh\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; for tool in llama-server llama-fit-params; do printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/$tool\"; chmod 755 \"$2/bin/$tool\"; done; exit 0; fi\nexit 0\n");
        using var path = new PathScope(stubs);

        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
            new CapturingBinaryManager(store, signal),
            store,
            signal,
            new LeaseOnlySupervisor(),
            new LlamaCppSourceBuildActivity(),
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        _ = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Completed, service.GetStatus().Phase);
        var adoptedBin = Path.Combine(temp.Path, "llama.cpp", "source-build", "active", "build", "bin");
        AssertEx.Null(LlamaCppToolBinaries.TryResolveQuantizer(adoptedBin));
        AssertEx.Null(LlamaCppToolBinaries.TryResolvePerplexity(adoptedBin));
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_OfficialCpu_UsesPinnedCommitScrubbedGitAndCpuMatrix()
    {
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var envDump = Path.Combine(temp.Path, "env.txt");
        var cmakeArgs = Path.Combine(temp.Path, "cmake.txt");
        WriteScript(Path.Combine(stubs, "git"),
            $"#!/bin/sh\nif [ \"$1\" = \"clone\" ]; then env > '{envDump}'; for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ]; then echo '{LlamaCppReleasePins.PinnedSourceCommitSha}'; exit 0; fi\nexit 0\n");
        WriteScript(Path.Combine(stubs, "cmake"),
            $"#!/bin/sh\necho \"$@\" >> '{cmakeArgs}'\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-server\"; chmod 755 \"$2/bin/llama-server\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-fit-params\"; chmod 755 \"$2/bin/llama-fit-params\"; exit 0; fi\nexit 0\n");
        using var path = new PathScope(stubs);
        Environment.SetEnvironmentVariable("XE_NODE_SQLITE_KEY", "must-not-leak");
        try
        {
            using var store = new InstalledRuntimeStore(temp.Path);
            var signal = new CudaManagedBuildSignal();
            var manager = new CapturingBinaryManager(store, signal);
            ILlamaCppSourceBuildActivity activity = new LlamaCppSourceBuildActivity();
            using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
                manager,
                store,
                signal,
                new LeaseOnlySupervisor(),
                activity,
                new NullLlamaCppSourceBuildEventPublisher(),
                NullLogger<LlamaCppSourceBuildService>.Instance,
                temp.Path);

            var outcome = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official), CancellationToken.None);
            AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, outcome.Outcome);
            await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

            AssertEx.Equal(LlamaCppSourceBuildPhase.Completed, service.GetStatus().Phase);
            AssertEx.Equal(GpuVariant.Cpu, manager.AdoptedVariant);
            var args = await File.ReadAllTextAsync(cmakeArgs);
            AssertEx.True(args.Contains("-DCMAKE_BUILD_RPATH_USE_ORIGIN=ON", StringComparison.Ordinal));
            AssertEx.True(args.Contains("-DGGML_CUDA=OFF", StringComparison.Ordinal));
            AssertEx.True(args.Contains("-DGGML_VULKAN=OFF", StringComparison.Ordinal));
            AssertEx.False(args.Contains("CMAKE_CUDA_ARCHITECTURES", StringComparison.Ordinal));
            AssertEx.True(args.Contains("--target llama-server llama-fit-params llama-quantize llama-perplexity", StringComparison.Ordinal));
            AssertEx.True(File.Exists(Path.Combine(temp.Path,
                "llama.cpp",
                "source-build",
                "active",
                "build",
                "bin",
                "llama-fit-params")));
            var environment = await File.ReadAllTextAsync(envDump);
            AssertEx.False(environment.Contains("must-not-leak", StringComparison.Ordinal));
            AssertEx.True(environment.Contains("GIT_CONFIG_NOSYSTEM=1", StringComparison.Ordinal));
            AssertEx.True(environment.Contains($"HOME={Path.Combine(temp.Path, "llama.cpp", "source-build", ".work", ".home")}", StringComparison.Ordinal));

            // The reservation is released in the build task's finally, AFTER the phase goes terminal, so the
            // Terminal wait above does not imply it has happened yet. Asserting it directly races that release
            // and loses whenever the build task is descheduled between the two.
            await AssertEx.EventuallyAsync(() => activity.ActiveBuildId is null,
                TestBudgets.Contended,
                "The source build stayed reserved after reaching a terminal phase.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("XE_NODE_SQLITE_KEY", null);
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    [UnsupportedOSPlatform("windows")]
    public async Task Start_OfficialExplicitCommit_BuildsAndRecordsExplicitRevision()
    {
        var commit = "abcdefabcdefabcdefabcdefabcdefabcdefabcd";
        using var temp = new TempDirectory();
        var stubs = Path.Combine(temp.Path, "stubs");
        Directory.CreateDirectory(stubs);
        var gitArgs = Path.Combine(temp.Path, "git.txt");
        WriteScript(Path.Combine(stubs, "git"),
            $"#!/bin/sh\necho \"$@\" >> '{gitArgs}'\nif [ \"$1\" = \"clone\" ]; then for last; do :; done; mkdir -p \"$last\"; exit 0; fi\nif [ \"$1\" = \"-C\" ] && [ \"$3\" = \"rev-parse\" ]; then echo '{commit}'; exit 0; fi\nexit 0\n");
        WriteScript(Path.Combine(stubs, "cmake"),
            "#!/bin/sh\nif [ \"$1\" = \"-B\" ]; then mkdir -p \"$2\"; exit 0; fi\nif [ \"$1\" = \"--build\" ]; then mkdir -p \"$2/bin\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-server\"; chmod 755 \"$2/bin/llama-server\"; printf '#!/bin/sh\\nexit 0\\n' > \"$2/bin/llama-fit-params\"; chmod 755 \"$2/bin/llama-fit-params\"; exit 0; fi\nexit 0\n");
        using var path = new PathScope(stubs);
        using var store = new InstalledRuntimeStore(temp.Path);
        var signal = new CudaManagedBuildSignal();
        var manager = new CapturingBinaryManager(store, signal);
        ILlamaCppSourceBuildActivity activity = new LlamaCppSourceBuildActivity();
        using var service = new LlamaCppSourceBuildService(new AlwaysReadyProbe(),
            manager,
            store,
            signal,
            new LeaseOnlySupervisor(),
            activity,
            new NullLlamaCppSourceBuildEventPublisher(),
            NullLogger<LlamaCppSourceBuildService>.Instance,
            temp.Path);

        var outcome = await service.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cpu, LlamaCppSourceSelection.Official, Commit: commit.ToUpperInvariant()),
            CancellationToken.None);
        AssertEx.Equal(LlamaCppSourceBuildStartOutcome.Started, outcome.Outcome);
        await AssertEx.EventuallyAsync(() => service.GetStatus().Terminal, TimeSpan.FromSeconds(10));

        AssertEx.Equal(LlamaCppSourceBuildPhase.Completed, service.GetStatus().Phase);
        var recorded = await store.ReadAsync(CancellationToken.None);
        AssertEx.NotNull(recorded);
        AssertEx.Equal(LlamaCppSourceBuildRequestValidation.OfficialRepository, recorded!.SourceRepository);
        AssertEx.Equal(LlamaCppSourceRevisionMode.ExplicitCommit, recorded.SourceRevisionMode);
        AssertEx.Equal(commit, recorded.SourceRequestedCommit);
        AssertEx.Equal(commit, recorded.SourceCommit);
        var invocations = await File.ReadAllTextAsync(gitArgs);
        AssertEx.True(invocations.Contains($"fetch --depth 1 --no-tags origin {commit}", StringComparison.Ordinal));
        AssertEx.True(invocations.Contains($"checkout --detach {commit}", StringComparison.Ordinal));
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteScript(string path, string content)
    {
        File.WriteAllText(path, content);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class AlwaysReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe
    {
        public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct) =>
            Task.FromResult(new LlamaCppSourceBuildPrerequisiteReport(true, []));
    }

    private sealed class CountingReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new LlamaCppSourceBuildPrerequisiteReport(true, []));
        }
    }

    private sealed class FixedReportProbe(LlamaCppSourceBuildPrerequisiteReport report) : ILlamaCppSourceBuildPrerequisiteProbe
    {
        public Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct) =>
            Task.FromResult(report);
    }

    private sealed class GatedReadyProbe : ILlamaCppSourceBuildPrerequisiteProbe
    {
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlamaCppSourceBuildPrerequisiteReport> ProbeAsync(LlamaCppSourceBackend backend, CancellationToken ct)
        {
            Interlocked.Increment(ref _callCount);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return new LlamaCppSourceBuildPrerequisiteReport(true, []);
        }
    }

    private sealed class RecordingPublisher : ILlamaCppSourceBuildEventPublisher
    {
        private readonly Lock _lock = new();
        private readonly List<LlamaCppSourceBuildStatusHubEvent> _events = [];

        public IReadOnlyList<LlamaCppSourceBuildStatusHubEvent> Events
        {
            get
            {
                lock (_lock)
                {
                    return [.. _events];
                }
            }
        }

        public Task PublishStatusAsync(LlamaCppSourceBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _events.Add(statusEvent);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class CapturingBinaryManager(IInstalledRuntimeStore store, IActiveSourceBuildSignal signal, bool failAdoption = false) : ILlamaCppBinaryManager
    {
        public GpuVariant? AdoptedVariant { get; private set; }

        public Task<LlamaBinary> EnsureBinaryAsync(GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LlamaBinary> InstallTagAsync(string tag, string assetName, string digestSha256, long expectedSize, GpuVariant variant, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<InstalledRuntimeState> AdoptCudaSourceBuildAsync(string buildBinDir, string tag, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RemoveCudaSourceBuildAsync(CancellationToken ct) =>
            Task.CompletedTask;

        public async Task<InstalledRuntimeState> AdoptSourceBuildAsync(string buildBinDir, string tag, GpuVariant variant, string sourceRepository,
            string sourceCommit, LlamaCppSourceRevisionMode revisionMode, string? requestedCommit, CancellationToken ct)
        {
            if (failAdoption)
            {
                throw new InvalidOperationException("unexpected adoption failure");
            }

            AdoptedVariant = variant;
            var state = new InstalledRuntimeState(tag, "source", new string('a', 64), variant, DateTimeOffset.UtcNow, buildBinDir,
                sourceRepository, sourceCommit, revisionMode, requestedCommit);
            await store.WriteAsync(state, ct);
            signal.SetActive(variant);
            return state;
        }
    }

    private sealed class LeaseOnlySupervisor : ILlamaServerProcessSupervisor
    {
        public Task<ILlamaServerRuntimeMutationLease?> TryAcquireRuntimeMutationLeaseAsync(CancellationToken ct) =>
            Task.FromResult<ILlamaServerRuntimeMutationLease?>(new Lease());

        public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct) =>
            throw new NotSupportedException();

        public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role) =>
            throw new NotSupportedException();

        public Task<T> RunExclusiveProfilingAsync<T>(string modelName, ModelRole role, ResolvedLaunchArguments launchArgs, bool enableMetrics,
            Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body, CancellationToken ct,
            Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public int CountRunningProcesses() =>
            0;

        public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role) =>
            null;

        private sealed class Lease : ILlamaServerRuntimeMutationLease
        {
            public ValueTask DisposeAsync() =>
                ValueTask.CompletedTask;
        }
    }

    private sealed class BusySupervisor : ILlamaServerProcessSupervisor
    {
        public Task<ILlamaServerRuntimeMutationLease?> TryAcquireRuntimeMutationLeaseAsync(CancellationToken ct) =>
            Task.FromResult<ILlamaServerRuntimeMutationLease?>(null);

        public Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EvictAsync(string modelName, ModelRole role, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct) =>
            throw new NotSupportedException();

        public LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role) =>
            throw new NotSupportedException();

        public Task<T> RunExclusiveProfilingAsync<T>(string modelName, ModelRole role, ResolvedLaunchArguments launchArgs, bool enableMetrics,
            Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body, CancellationToken ct,
            Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public int CountRunningProcesses() =>
            1;

        public LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role) =>
            null;
    }

    private sealed class OrderedFailingPublisher : ILlamaCppSourceBuildEventPublisher
    {
        private int _active;
        private int _calls;
        private int _maxConcurrent;

        public List<LlamaCppSourceBuildStatusHubEvent> Events { get; } = [];
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public async Task PublishStatusAsync(LlamaCppSourceBuildStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            var observed = Volatile.Read(ref _maxConcurrent);
            while (active > observed)
            {
                var prior = Interlocked.CompareExchange(ref _maxConcurrent, active, observed);
                if (prior == observed)
                {
                    break;
                }

                observed = prior;
            }

            try
            {
                // real-timer: this is not a wait for something to happen, it is the window that gives a BROKEN
                // serializer a chance to overlap two publishes. A gate the test releases cannot express it: the
                // passing case is exactly the one where a second publish never arrives, so the gate would deadlock.
                await Task.Delay(5, cancellationToken);
                if (Interlocked.Increment(ref _calls) == 1)
                {
                    throw new InvalidOperationException("first publish fails");
                }

                Events.Add(statusEvent);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class PathScope : IDisposable
    {
        private readonly string _original = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        public PathScope(string path) =>
            Environment.SetEnvironmentVariable("PATH", path + Path.PathSeparator + _original);

        public void Dispose() =>
            Environment.SetEnvironmentVariable("PATH", _original);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-source-build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort test cleanup.
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task<(InstalledRuntimeState State, string Server)> SeedActiveRuntimeAsync(string root, IInstalledRuntimeStore store)
    {
        var tree = Path.Combine(root, "llama.cpp", "source-build", "active");
        var bin = Path.Combine(tree, "build", "bin");
        Directory.CreateDirectory(bin);
        var server = Path.Combine(bin, "llama-server");
        await File.WriteAllTextAsync(server, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var helper = Path.Combine(bin, "llama-fit-params");
        await File.WriteAllTextAsync(helper, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(helper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var sha = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(server)));
        var state = new InstalledRuntimeState(LlamaCppReleasePins.PinnedTag, "source", sha, GpuVariant.Cpu, DateTimeOffset.UtcNow, bin,
            LlamaCppSourceBuildRequestValidation.OfficialRepository, LlamaCppReleasePins.PinnedSourceCommitSha,
            LlamaCppSourceRevisionMode.EnginePinned);
        await store.WriteAsync(state, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(tree, ".source-build-manifest.json"), JsonSerializer.Serialize(new
        {
            Tag = state.Tag,
            Variant = state.Variant,
            Source = LlamaCppSourceSelection.Official,
            Repository = state.SourceRepository,
            RevisionMode = state.SourceRevisionMode,
            RequestedCommit = state.SourceRequestedCommit,
            ResolvedCommit = state.SourceCommit,
            BinarySha256 = state.Sha256
        }));
        return (state, server);
    }
}
