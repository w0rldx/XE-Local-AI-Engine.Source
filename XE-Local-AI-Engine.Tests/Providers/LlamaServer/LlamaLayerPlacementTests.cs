namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the measured-GPU-layer-placement path: the banner grammar, the node report that memoizes which
///     <c>(model, role, variant)</c> keys have already been answered, and the supervisor spawn that pays a raised log
///     verbosity exactly once per key to obtain the answer.
/// </summary>
/// <remarks>
///     The banner lines below are verbatim llama-server output captured from a CUDA build on this stack. They matter
///     as literals: at the server's DEFAULT log verbosity the whole startup is 11 lines and contains no placement
///     information at all, so the supervisor has to raise verbosity to see them, and the line format (a leading
///     timestamp and level marker) is what the grammar has to tolerate.
/// </remarks>
public sealed class LlamaLayerPlacementTests
{
    private const string FullOffloadLine = "0.00.408.714 I load_tensors: offloaded 25/25 layers to GPU";
    private const string PartialOffloadLine = "0.00.539.550 I load_tensors: offloaded 38/49 layers to GPU";

    [Test]
    public void TryParse_VerbatimFullOffloadBanner_ReadsBothCounts()
    {
        AssertEx.True(LlamaLayerOffloadBanner.TryParse(FullOffloadLine, out var offloaded, out var total));
        AssertEx.Equal(expected: 25, offloaded);
        AssertEx.Equal(expected: 25, total);
    }

    [Test]
    public void TryParse_VerbatimPartialOffloadBanner_ReadsBothCounts()
    {
        AssertEx.True(LlamaLayerOffloadBanner.TryParse(PartialOffloadLine, out var offloaded, out var total));
        AssertEx.Equal(expected: 38, offloaded);
        AssertEx.Equal(expected: 49, total);
    }

    [Test]
    public void TryParse_NeighbouringLoadTensorsLines_AreNotMistakenForTheBanner()
    {
        // These print immediately before/after the banner and both mention offloading and GPU buffers.
        AssertEx.False(LlamaLayerOffloadBanner.TryParse("0.00.408.696 I load_tensors: offloading output layer to GPU", out _, out _));
        AssertEx.False(LlamaLayerOffloadBanner.TryParse("0.00.408.714 I load_tensors: offloading 23 repeating layers to GPU", out _, out _));
        AssertEx.False(LlamaLayerOffloadBanner.TryParse("0.00.408.720 I load_tensors:        CUDA0 model buffer size =   373.73 MiB", out _, out _));
    }

    [Test]
    public void TryParse_BlankOrZeroTotal_IsRejected()
    {
        AssertEx.False(LlamaLayerOffloadBanner.TryParse(line: null, out _, out _));
        AssertEx.False(LlamaLayerOffloadBanner.TryParse("   ", out _, out _));
        AssertEx.False(LlamaLayerOffloadBanner.TryParse("load_tensors: offloaded 0/0 layers to GPU", out _, out _));
    }

    [Test]
    public void Report_ReloadingAModel_ReplacesItsEarlierReading()
    {
        // The staleness this prevents: model A loads alone and fits entirely, two more models load beside it, then A
        // is reloaded under VRAM pressure and now spills. Auto-fit decides placement against the free VRAM AT LOAD
        // TIME, so the first reading is simply no longer true and must not survive.
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 49, totalLayers: 49);

        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 30, totalLayers: 49);

        var current = AssertEx.NotNull(report.Current);
        AssertEx.Equal(expected: 30, current.OffloadedLayers);
        AssertEx.True(current.IsPartial);
    }

    [Test]
    public void Report_KeysAreIndependentAcrossRoleAndVariant()
    {
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3", offloadedLayers: 25, totalLayers: 25);
        report.Record(ModelRole.Embedding, GpuVariant.Cuda, "qwen3", offloadedLayers: 4, totalLayers: 13);

        // The embedding entry is partial, so it wins, and it did not overwrite the chat entry's key.
        var current = AssertEx.NotNull(report.Current);
        AssertEx.Equal(ModelRole.Embedding, current.Role);
        AssertEx.Equal(expected: 4, current.OffloadedLayers);
    }

    [Test]
    public void Report_PartialObservation_SurvivesALaterFullyOffloadedModel()
    {
        // The failure this prevents: a big chat model spills 11 layers into system RAM, then a small embedding model
        // loads and fits entirely. Reporting only the most recent observation would replace the actionable figure with
        // a reassuring one for a model nobody is waiting on.
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 38, totalLayers: 49);
        report.Record(ModelRole.Embedding, GpuVariant.Cuda, "nomic-embed", offloadedLayers: 13, totalLayers: 13);

        var current = AssertEx.NotNull(report.Current);
        AssertEx.Equal("qwen3-14b", current.ModelName);
        AssertEx.Equal(expected: 38, current.OffloadedLayers);
        AssertEx.Equal(expected: 49, current.TotalLayers);
        AssertEx.True(current.IsPartial);
    }

    [Test]
    public void Report_AllFullyOffloaded_ReportsTheMostRecent()
    {
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 49, totalLayers: 49);
        report.Record(ModelRole.Embedding, GpuVariant.Cuda, "nomic-embed", offloadedLayers: 13, totalLayers: 13);

        var current = AssertEx.NotNull(report.Current);
        AssertEx.Equal("nomic-embed", current.ModelName);
        AssertEx.False(current.IsPartial);
    }

    [Test]
    public void Report_NothingObserved_HasNoCurrentPlacement()
    {
        AssertEx.Null(new LlamaLayerPlacementReport().Current);
    }

    [Test]
    public void Report_RemovedReading_StopsBeingReported()
    {
        // The report had no removal at all, and a partial outranks every full reading, so one spilled load was
        // reported for the rest of the app's lifetime — after the model was ejected, and even beside a later fully
        // resident one.
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 38, totalLayers: 49);

        report.Remove(ModelRole.Chat, "qwen3-14b");

        AssertEx.Null(report.Current);
    }

    [Test]
    public void Report_Remove_DropsEveryVariantRecordedForThatModelAndRole()
    {
        // The caller tearing a process down does not know which llama.cpp build produced the reading, so a removal
        // that only matched the current variant would strand the previous one — reported forever as if resident.
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 38, totalLayers: 49);
        report.Record(ModelRole.Chat, GpuVariant.Vulkan, "qwen3-14b", offloadedLayers: 20, totalLayers: 49);

        report.Remove(ModelRole.Chat, "qwen3-14b");

        AssertEx.Null(report.Current);
    }

    [Test]
    public void Report_RemovingTheSpilledModel_LeavesEveryOtherModelsReadingIntact()
    {
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 38, totalLayers: 49);
        report.Record(ModelRole.Embedding, GpuVariant.Cuda, "nomic-embed", offloadedLayers: 13, totalLayers: 13);

        report.Remove(ModelRole.Chat, "qwen3-14b");

        var current = AssertEx.NotNull(report.Current);
        AssertEx.Equal("nomic-embed", current.ModelName);
        AssertEx.False(current.IsPartial);
    }

    [Test]
    public async Task Evict_RetiresTheMeasuredPlacementOfTheProcessItToreDown()
    {
        var report = new LlamaLayerPlacementReport();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [PartialOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);
        AssertEx.NotNull(report.Current);

        await supervisor.EvictAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.Null(report.Current);
    }

    /// <summary>
    ///     Both halves of the rule at once: while the spilled model is LOADED its partial reading must still win over a
    ///     fully resident model (the preference exists so a small model that fits cannot mask a large one that does
    ///     not), and the moment that model is torn down its reading must stop being reported rather than outranking the
    ///     model still on the GPU.
    /// </summary>
    [Test]
    public async Task Eject_OfASpilledModel_StopsItMaskingTheModelStillResident()
    {
        var report = new LlamaLayerPlacementReport();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [PartialOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);
        launcher.StartupLines = [FullOffloadLine];
        await supervisor.EnsureRunningAsync("nomic-embed", ModelRole.Embedding, CancellationToken.None);

        // Both are live: the spilled one is the actionable reading and must not be masked by the one that fits.
        var whileBothLoaded = AssertEx.NotNull(report.Current);
        AssertEx.Equal("qwen3-14b", whileBothLoaded.ModelName);
        AssertEx.True(whileBothLoaded.IsPartial);

        var outcome = await supervisor.EjectAsync("qwen3-14b", ModelRole.Chat, force: false, CancellationToken.None);
        AssertEx.Equal(LlamaServerEjectOutcome.Ejected, outcome);

        var afterEject = AssertEx.NotNull(report.Current);
        AssertEx.Equal("nomic-embed", afterEject.ModelName);
        AssertEx.False(afterEject.IsPartial, "the ejected model's spilled reading must not survive its process.");
    }

    [Test]
    public async Task EnsureRunning_FirstGpuSpawn_RaisesLogVerbosity_AndRecordsMeasuredPlacement()
    {
        var report = new LlamaLayerPlacementReport();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = [PartialOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report,
            loadTelemetry: telemetry);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "-lv");

        var placement = AssertEx.NotNull(report.Current);
        AssertEx.Equal("qwen3-14b", placement.ModelName);
        AssertEx.Equal(expected: 38, placement.OffloadedLayers);
        AssertEx.Equal(expected: 49, placement.TotalLayers);
        AssertEx.True(placement.IsPartial);

        AssertEx.True(telemetry.Observations.TryDequeue(out var observation));
        AssertEx.Equal(ModelRole.Chat, observation!.Role);
        AssertEx.Equal(GpuVariant.Cuda, observation.Variant);
        AssertEx.Equal(LlamaServerReadinessOutcome.Ready, observation.Outcome);
        AssertEx.Equal(LlamaServerPlacementOutcome.Partial, observation.Placement);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observation.AttemptKind);
        AssertEx.Equal(SpeculativeModeClass.Disabled, observation.SpeculativeModeClass);
        AssertEx.True(observation.ReadinessDurationMs >= 0d);
    }

    [Test]
    public async Task EnsureRunning_AlreadyMeasuredModel_StillRaisesVerbosity_SoTheReadingCannotGoStale()
    {
        // A prior reading must NOT suppress measurement: this spawn is a different process against different free
        // VRAM, so it gets its own reading.
        var report = new LlamaLayerPlacementReport();
        report.Record(ModelRole.Chat, GpuVariant.Cuda, "qwen3-14b", offloadedLayers: 49, totalLayers: 49);

        var launcher = new FakeProcessLauncher
        {
            StartupLines = [PartialOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Contains(spec!.Arguments, "-lv");
        var placement = AssertEx.NotNull(report.Current);
        AssertEx.Equal(expected: 38, placement.OffloadedLayers);
    }

    [Test]
    public async Task EnsureRunning_RaisedVerbositySpawn_LogsTheLoadAtInformation_ThenDemotesRequestChatter()
    {
        // The elevated verbosity exists for the sniffer, not for the log sink. The load window (where the banner and
        // any failure text live) must still reach Information; only steady-state chatter is demoted afterwards.
        var report = new LlamaLayerPlacementReport();
        var demoteDuringLoad = true;
        var launcher = new FakeProcessLauncher(spec =>
        {
            demoteDuringLoad = spec.ShouldDemoteForwardedLines?.Invoke() ?? false;
            return new FakeProcessHandle(pid: 4242);
        })
        {
            StartupLines = [FullOffloadLine]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.False(demoteDuringLoad, "the load window must stay at Information so the banner and failures are visible.");
        AssertEx.True(launcher.Launches.TryDequeue(out var spec2));
        AssertEx.True(spec2!.ShouldDemoteForwardedLines?.Invoke() ?? false,
            "once the process is serving, its raised-verbosity chatter must drop to Debug.");
    }

    [Test]
    public async Task EnsureRunning_OperatorDrivenSpawn_NeverDemotesItsLogging()
    {
        // A CPU spawn had no verbosity raised for it, so nothing about its logging may change.
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            layerPlacementReport: new LlamaLayerPlacementReport());

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.Null(spec!.ShouldDemoteForwardedLines);
    }

    [Test]
    public async Task EnsureRunning_CpuVariant_NeverRaisesVerbosity()
    {
        // There is no placement question on a CPU runtime, so there is nothing to buy with extra log volume.
        var report = new LlamaLayerPlacementReport();
        var telemetry = new FakeLlamaServerLoadTelemetry();
        var launcher = new FakeProcessLauncher();
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cpu),
            layerPlacementReport: report,
            loadTelemetry: telemetry);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Launches.TryDequeue(out var spec));
        AssertEx.False(spec!.Arguments.Contains("-lv"), "A CPU spawn has no layer placement to observe.");
        AssertEx.Null(report.Current);
        AssertEx.True(telemetry.Observations.TryDequeue(out var observation));
        AssertEx.Equal(LlamaServerPlacementOutcome.Cpu, observation!.Placement);
        AssertEx.Equal(LlamaServerLoadAttemptKind.Primary, observation.AttemptKind);
    }

    [Test]
    public async Task EnsureRunning_NoBannerInOutput_RecordsNothing_AndLeavesTheKeyOpen()
    {
        var report = new LlamaLayerPlacementReport();
        var launcher = new FakeProcessLauncher
        {
            StartupLines = ["0.00.297.569 W srv  llama_server: -----------------"]
        };
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: new FakeVariantSelector(GpuVariant.Cuda),
            layerPlacementReport: report);

        await supervisor.EnsureRunningAsync("qwen3-14b", ModelRole.Chat, CancellationToken.None);

        AssertEx.Null(report.Current);
    }
}
