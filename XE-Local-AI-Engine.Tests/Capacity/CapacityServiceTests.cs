namespace XE_Local_AI_Engine.Tests.Capacity;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="CapacityService" /> tests: the single admission gate. Cloud short-circuits without a probe; a local
///     model already running queues; a fitting local model with process headroom is admitted (and reserves its
///     footprint); the process cap and byte budget each reject independently; an unknown footprint rejects; CPU mode
///     does not double-count resident models; concurrent different-model spawns cannot both pass (TOCTOU); and a
///     non-fitting Ollama model rejects with the eviction warning flagged; GPU mode fits against measured FREE VRAM
///     (not total) and falls back to total when free is unmeasurable; the decision reads a fresh (forceRefresh) profile.
///     Every probe is mocked — no Ollama/network.
/// </summary>
public sealed class CapacityServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";
    private const string Llamacpp = LlamaServerProviderConstants.ProviderName;

    [Test]
    public async Task Capacity_WhenCloudModel_ReturnsAllow_WithoutProbe()
    {
        var harness = new Harness
        {
            CloudSelected = true
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.False(decision.OllamaEvictionWarning);
        AssertEx.Null(decision.Reservation);
        // No hardware/audit/supervisor/footprint probe may run on the cloud path.
        await harness.RuntimeAudit.DidNotReceive().GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.RuntimeAudit.DidNotReceive().GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.Supervisor.DidNotReceive().CheckHealthAsync(Arg.Any<CancellationToken>());
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenLlamaModelUsesExternalEndpoint_AllowsRepeatedlyWithoutLocalReservation()
    {
        var harness = new Harness
        {
            ExternalEndpoints = new LlamaServerExternalEndpointOptions
            {
                ChatEndpointsByModel = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
                {
                    [Model] = new Uri("http://127.0.0.1:18080/v1")
                }
            }
        };
        var service = harness.Build();

        var first = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);
        var second = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, first.Verdict);
        AssertEx.Equal(CapacityVerdict.Allow, second.Verdict);
        AssertEx.Null(first.Reservation);
        AssertEx.Null(second.Reservation);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
        AssertEx.False(harness.LaunchAdmissions.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
        await harness.RuntimeAudit.DidNotReceive().GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenExternalProviderModel_ReturnsAllow_WithoutProbeOrReservation()
    {
        // An external OpenAI-compatible model runs on someone else's hardware: no process, no weights, no RAM/VRAM. It
        // must be admitted without a probe — and it MUST be admitted, because the footprint provider has no GGUF to
        // size, so the byte-budget path would otherwise reject every such send as "footprint could not be determined".
        var harness = new Harness
        {
            ProviderName = ExternalProviderConstants.ProviderName
        };
        var service = harness.Build();

        var decision = await service.DecideAsync("ext:unsloth-box/unsloth/Qwen3.8-27B-GGUF", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.False(decision.OllamaEvictionWarning);
        AssertEx.Null(decision.Reservation);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
        await harness.RuntimeAudit.DidNotReceive().GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenExternalIdHasNoProviderMapRowYet_StillAllows()
    {
        // The backstop branch: between the encrypted-store commit and the provider-map sync (or before reconciliation
        // repairs a lost row) an ext: id default-routes to "llamacpp" and would be rejected on a footprint it can never
        // have. The id's scheme alone is enough to know no local capacity is consumed.
        var harness = new Harness
        {
            ProviderName = Llamacpp
        };
        var service = harness.Build();

        var decision = await service.DecideAsync("ext:unsloth-box/qwen3", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.Null(decision.Reservation);
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenLocalFitsAndProcessHeadroom_ReturnsAllow()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(4 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.NotNull(decision.Reservation);
        // The footprint was reserved in the ledger so a concurrent decision sees the in-flight load.
        AssertEx.Equal(4 * Gb, harness.Ledger.Reserved.GpuBytes);
        AssertEx.True(harness.LaunchAdmissions.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
        decision.Reservation!.Dispose();
        decision.Reservation.Dispose();
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
        AssertEx.False(harness.LaunchAdmissions.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
    }

    [Test]
    public async Task Capacity_WhenTheCallerLaunchesItsOwnProcess_ReservesBytesWithoutPublishingALaunchAdmission()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(4 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(new CapacityRequest(Model, ModelRole.Chat, PublishLaunchAdmission: false),
            CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.NotNull(decision.Reservation);
        // The bytes are still booked — only the admission the caller would never consume is withheld.
        AssertEx.Equal(4 * Gb, harness.Ledger.Reserved.GpuBytes);
        AssertEx.False(harness.LaunchAdmissions.Snapshot(Model, ModelRole.Chat).HasRequestedKey,
            "An admission nothing consumes is exactly what the supervisor refuses to launch against.");
        decision.Reservation!.Dispose();
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_WhileABenchmarkReservationIsHeld_ASpawnOfTheSameModelCanStillBeginItsOwnLaunch()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(4 * Gb)
        };
        var service = harness.Build();

        // The benchmark holds its reservation open for the whole exclusive spawn, which is when the supervisor asks
        // the registry whether it may launch. An ordinary Allow leaves an admission there and that ask fails.
        var decision = await service.DecideAsync(new CapacityRequest(Model, ModelRole.Chat, PublishLaunchAdmission: false),
            CancellationToken.None);
        using var reservation = decision.Reservation;

        var began = harness.LaunchAdmissions.TryBeginLaunch(Model, ModelRole.Chat, out var admission, out var ticket);
        using (ticket)
        {
            AssertEx.True(began, "The supervisor must be able to begin a launch while the benchmark reservation is held.");
            AssertEx.Null(admission, "A frozen replay must launch from its own arguments, never from a published admission.");
        }
    }

    [Test]
    public async Task Capacity_ContextAwareRequest_ForwardsRequiredContextToFootprintProbe()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(4 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(new CapacityRequest(Model, ModelRole.Chat, RequiredContextTokens: 32768), CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        _ = harness.FootprintProvider.Received(1)
                   .ResolveFootprintAsync(Model, ModelRole.Chat, harness.Profile, 32768, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        decision.Reservation?.Dispose();
    }

    [Test]
    public async Task Capacity_WhenProcessCapReached_ReturnsReject_EvenIfBytesFit()
    {
        // Two distinct processes already loaded, cap = 2 → adding a third exceeds the cap even though the bytes fit.
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(1 * Gb),
            MaxLoadedProcesses = 2,
            RunningLlama =
            [
                new LlamaServerProcessHealth("running/a:Q4_K_M", ModelRole.Chat, IsResponsive: true, "ok"),
                new LlamaServerProcessHealth("running/b:Q4_K_M", ModelRole.Chat, IsResponsive: true, "ok")
            ]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(0, harness.Ledger.Reserved.GpuBytes);
    }

    [Test]
    public async Task Capacity_PendingAdmissionCountsTowardProcessCap()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(1 * Gb),
            MaxLoadedProcesses = 1
        };
        var service = harness.Build();
        var first = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        var second = await service.DecideAsync("model/b", ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, first.Verdict);
        AssertEx.Equal(CapacityVerdict.RejectInsufficient, second.Verdict);
        first.Reservation!.Dispose();
    }

    [Test]
    public async Task Capacity_RegistryConflictRejectsWithoutTentativeReservation()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(1 * Gb)
        };
        var admission = AssertEx.NotNull(AdmissionFootprint(1 * Gb, 8192).Admission);
        AssertEx.True(harness.LaunchAdmissions.TryAcquire(admission, out var existing));
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
        existing!.Dispose();
    }

    [Test]
    public async Task Capacity_RegistryPublishRace_DisposesTentativeFootprintReservation()
    {
        var registry = Substitute.For<IProcessLaunchAdmissionRegistry>();
        registry.Snapshot(Arg.Any<string>(), Arg.Any<ModelRole>())
                .Returns(new ProcessLaunchAdmissionSnapshot(new HashSet<ProcessLaunchAdmissionKey>(),
                    HasRequestedKey: false,
                    HasGlobalBlocker: false));
        registry.Acquire(Arg.Any<ProcessLaunchAdmission>())
                .Returns((IProcessLaunchAdmissionLease?)null);
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(1 * Gb),
            LaunchAdmissions = registry
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_OllamaFitDoesNotPublishLlamaLaunchAdmission()
    {
        var harness = new Harness
        {
            ProviderName = OllamaLocalModelProvider.OllamaProviderName,
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(1 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.False(harness.LaunchAdmissions.Snapshot(Model, ModelRole.Chat).HasRequestedKey);
        decision.Reservation!.Dispose();
    }

    [Test]
    public async Task Capacity_WhenLocalSameAsRunning_ReturnsQueueSameModel()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            RunningLlama = [new LlamaServerProcessHealth(Model, ModelRole.Chat, IsResponsive: true, "ok")]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.QueueSameModel, decision.Verdict);
        // No fit math runs on the same-model path.
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenTheOnlyProcessForTheKeyHasExited_DoesNotQueueOnIt()
    {
        // A crashed llama-server lingers in the supervisor's table until the idle reaper collects it, up to a quarter
        // of the idle TTL. Counting the corpse as resident short-circuits this decision to QueueSameModel, telling the
        // caller to serialize on a process that can never grant an inference lease — which is how the adaptive-effort
        // fast-model swap stayed refused for the rest of the node's life after its process died, instead of
        // relaunching through the ordinary ensure-running path.
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(1 * Gb),
            RunningLlama = [new LlamaServerProcessHealth(Model, ModelRole.Chat, IsResponsive: false, "Process has exited.", HasExited: true)]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        decision.Reservation?.Dispose();
    }

    [Test]
    public async Task Capacity_WhenAProcessIsAliveButUnresponsive_StillQueuesOnIt()
    {
        // The other half of the same rule: an alive-but-wedged process still holds its VRAM, its port and its
        // loaded-process slot, so only an EXITED entry is filtered — never an unresponsive one.
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            RunningLlama = [new LlamaServerProcessHealth(Model, ModelRole.Chat, IsResponsive: false, "Not responding to health probe.")]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.QueueSameModel, decision.Verdict);
    }

    [Test]
    public async Task Capacity_WhenLocalSameAsRunningWithDifferentCasing_ReturnsQueueSameModel()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            RunningLlama = [new LlamaServerProcessHealth(Model.ToUpperInvariant(), ModelRole.Chat, IsResponsive: true, "ok")]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.QueueSameModel, decision.Verdict);
        await harness.FootprintProvider.DidNotReceive()
                     .ResolveFootprintAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<HardwareProfile>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenLocalDifferentNoFit_ReturnsRejectInsufficient()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(4 * Gb),
            Footprint = GpuFootprint(40 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.True(decision.Reason.Length > 0);
        // Sanitized reason carries no model identity, path, or byte figure.
        AssertEx.False(decision.Reason.Contains(Model, StringComparison.Ordinal));
        harness.FootprintProvider.DidNotReceive()
               .TryCommitAdmissionFootprint(Arg.Any<ModelFootprint>(), out Arg.Any<ModelFootprint>());
    }

    [Test]
    public async Task Capacity_WhenLowerHardwareTierFits_AdmitsAndReservesSelectedFootprint()
    {
        var initial = AdmissionFootprint(20 * Gb, 65536);
        var middle = AdmissionFootprint(12 * Gb, 32768);
        var adjusted = AdmissionFootprint(6 * Gb, 16384);
        var committed = AdmissionFootprint(5 * Gb, 8192);
        var candidates = new Queue<ModelFootprint>([middle, adjusted]);
        var harness = new Harness
        {
            Profile = GpuProfile(8 * Gb),
            Footprint = initial
        };
        harness.FootprintProvider.TryDownTierForAdmission(Arg.Any<ModelFootprint>(), out Arg.Any<ModelFootprint>())
               .Returns(call =>
               {
                   call[1] = candidates.Dequeue();
                   return true;
               });
        var service = harness.Build();
        harness.FootprintProvider.TryCommitAdmissionFootprint(adjusted, out Arg.Any<ModelFootprint>())
               .Returns(call =>
               {
                   call[1] = committed;
                   return true;
               });

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.Equal(committed.Resources, harness.Ledger.Reserved);
        harness.FootprintProvider.Received(2)
               .TryDownTierForAdmission(Arg.Any<ModelFootprint>(), out Arg.Any<ModelFootprint>());
        harness.FootprintProvider.Received(1)
               .TryCommitAdmissionFootprint(adjusted, out Arg.Any<ModelFootprint>());
    }

    [Test]
    public async Task Capacity_WhenTheOnlyFittingTierIsBelowTheRequiredContext_RejectsWithoutCommitting()
    {
        // The caller NAMED 16384 and will launch at 16384 (a benchmark replays its frozen -c). Admitting the 8192 tier
        // would under-book the bytes AND pin 8192 into the model's shared allocation for the process lifetime, after
        // which every later admission naming 16384 fails the required-context check and reports the footprint as
        // undeterminable until the app restarts.
        var initial = AdmissionFootprint(20 * Gb, 16384);
        var downTiered = AdmissionFootprint(6 * Gb, 8192);
        var harness = new Harness
        {
            Profile = GpuProfile(8 * Gb),
            Footprint = initial
        };
        harness.FootprintProvider.TryDownTierForAdmission(Arg.Any<ModelFootprint>(), out Arg.Any<ModelFootprint>())
               .Returns(call =>
               {
                   call[1] = downTiered;
                   return true;
               });
        var service = harness.Build();

        var decision = await service.DecideAsync(new CapacityRequest(Model, ModelRole.Chat, RequiredContextTokens: 16384),
            CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
        harness.FootprintProvider.DidNotReceive()
               .TryCommitAdmissionFootprint(Arg.Any<ModelFootprint>(), out Arg.Any<ModelFootprint>());
    }

    [Test]
    public async Task Capacity_WhenFootprintUnknown_ReturnsReject()
    {
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = ModelFootprint.Unknown
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
    }

    [Test]
    public async Task Capacity_WhenVramUnknown_UsesRamBudget()
    {
        // VRAM unknown → the byte budget is AvailableRamBytes; a footprint under it is admitted.
        var harness = new Harness
        {
            Profile = CpuProfile(availableRam: 24 * Gb),
            Footprint = CpuFootprint(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
    }

    [Test]
    public async Task Capacity_CpuMode_DoesNotDoubleCountResidentModels()
    {
        // CPU mode: AvailableRamBytes already nets out the resident running model, so a footprint that fits the
        // available RAM is admitted even though a large model is "running" — its bytes must NOT be subtracted again.
        var harness = new Harness
        {
            Profile = CpuProfile(availableRam: 10 * Gb),
            Footprint = CpuFootprint(8 * Gb),
            MaxLoadedProcesses = 5,
            RunningLlama = [new LlamaServerProcessHealth("resident/big:Q4_K_M", ModelRole.Chat, IsResponsive: true, "ok")]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
    }

    [Test]
    public async Task Capacity_GpuMode_UsesFreeVramBaseline_NotTotalVram()
    {
        // The bug: a GPU whose TOTAL VRAM fits the model but whose FREE VRAM does not (residents outside the ledger —
        // the main chat model / warm sub-agents — hold the rest). Total-based math would over-admit; the free baseline
        // must reject. Total = 64 GB, free = 4 GB, footprint = 8 GB → reject.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: 4 * Gb),
            Footprint = GpuFootprint(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(0, harness.Ledger.Reserved.GpuBytes);
    }

    [Test]
    public async Task Capacity_GpuMode_WhenFreeVramFits_Admits()
    {
        // Free baseline comfortably fits the footprint → admit and reserve. Total is larger but irrelevant now.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: 20 * Gb),
            Footprint = GpuFootprint(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.Equal(8 * Gb, harness.Ledger.Reserved.GpuBytes);
    }

    [Test]
    public async Task Capacity_GpuResidentFootprint_WithNoRamReservation_DoesNotRequireRamTelemetry()
    {
        // A fully GPU-resident llama.cpp process memory-maps the GGUF rather than committing a second copy of its
        // weights in RAM. A zero-RAM footprint must therefore be decided solely against free VRAM even when the platform
        // cannot provide a useful available-RAM measurement.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: 20 * Gb) with
            {
                AvailableRamBytes = 0
            },
            Footprint = GpuFootprint(8 * Gb, ramBytes: 0)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        AssertEx.Equal(new ResourceFootprint(8 * Gb, 0), harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_NvidiaGpu_WhenFreeVramUnknown_Rejects()
    {
        // NVIDIA is the one backend where the profiler has an authoritative global-free reader. If that measurement is
        // absent, falling back to total VRAM would hide resident processes and over-admit, so admission fails closed.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null),
            Footprint = GpuFootprint(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_NonNvidiaGpu_WhenFreeVramUnknown_FallsBackToTotalVram()
    {
        // With no known resident and no external draft, the first degraded non-NVIDIA load can use total VRAM minus
        // the in-flight ledger. Total = 64 GB, footprint = 8 GB → admit.
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null) with
            {
                GpuVendor = GpuVendor.Amd
            },
            Footprint = GpuFootprint(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
    }

    [Test]
    public async Task Capacity_NonNvidiaGpu_WhenFreeVramUnknownAndModelResident_Rejects()
    {
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null) with
            {
                GpuVendor = GpuVendor.Amd
            },
            Footprint = GpuFootprint(8 * Gb),
            RunningLlama = [new LlamaServerProcessHealth("resident/model", ModelRole.Chat, IsResponsive: true, "ok")],
            MaxLoadedProcesses = 5
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_NonNvidiaGpu_WhenFreeVramUnknownAndExternalDraftConfigured_Rejects()
    {
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null) with
            {
                GpuVendor = GpuVendor.Amd
            },
            Footprint = GpuFootprint(8 * Gb),
            SupervisorOptions = new LlamaServerSupervisorOptions
            {
                SpeculativeMode = "draft-simple",
                SpeculativeDraftModelName = "draft/model"
            }
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_NonNvidiaGpu_WhenSupervisorResidencyProbeFails_Rejects()
    {
        var harness = new Harness
        {
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null) with
            {
                GpuVendor = GpuVendor.Amd
            },
            Footprint = GpuFootprint(8 * Gb),
            SupervisorProbeThrows = true
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_NonNvidiaGpu_WhenOllamaResidencyProbeFails_Rejects()
    {
        var harness = new Harness
        {
            ProviderName = OllamaLocalModelProvider.OllamaProviderName,
            Profile = GpuProfileWithFreeVram(64 * Gb, availableVramBytes: null) with
            {
                GpuVendor = GpuVendor.Intel
            },
            Footprint = GpuFootprint(8 * Gb),
            OllamaProbeThrows = true
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Equal(ResourceFootprint.Zero, harness.Ledger.Reserved);
    }

    [Test]
    public async Task Capacity_LocalDecision_ReadsFreshProfile_NotBootCache()
    {
        // A capacity decision must re-probe live VRAM/RAM (forceRefresh:true) — a boot-time cached free-VRAM figure
        // would defeat the resident-model accounting.
        var harness = new Harness
        {
            Profile = GpuProfile(64 * Gb),
            Footprint = GpuFootprint(4 * Gb)
        };
        var service = harness.Build();

        await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        await harness.RuntimeAudit.Received().GetEffectiveProfileAsync(forceRefreshProfile: true, Arg.Any<CancellationToken>());
        await harness.RuntimeAudit.DidNotReceive().GetEffectiveProfileAsync(forceRefreshProfile: false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Capacity_WhenTwoDifferentModelsRaceTheGate_DoesNotDoubleAdmit()
    {
        // Budget = 10 GB, each model = 6 GB → at most one of two concurrent different-model spawns can be admitted; the
        // ledger reservation from the first must make the second see only 4 GB free.
        var harness = new Harness
        {
            Profile = GpuProfile(10 * Gb),
            Footprint = GpuFootprint(6 * Gb),
            MaxLoadedProcesses = 5
        };
        var service = harness.Build();

        var first = service.DecideAsync("model/a:Q4_K_M", ModelRole.Chat, CancellationToken.None);
        var second = service.DecideAsync("model/b:Q4_K_M", ModelRole.Chat, CancellationToken.None);
        var decisions = await Task.WhenAll(first, second);

        var allowed = decisions.Count(d => d.Verdict == CapacityVerdict.Allow);
        var rejected = decisions.Count(d => d.Verdict == CapacityVerdict.RejectInsufficient);
        AssertEx.Equal(1, allowed);
        AssertEx.Equal(1, rejected);
        AssertEx.Equal(6 * Gb, harness.Ledger.Reserved.GpuBytes);
    }

    [Test]
    public async Task Capacity_WhenOllamaDifferentModelNoFit_RejectsAndFlagsWarning()
    {
        var harness = new Harness
        {
            ProviderName = OllamaLocalModelProvider.OllamaProviderName,
            Profile = GpuProfile(4 * Gb),
            Footprint = GpuFootprint(40 * Gb),
            RunningOllama =
            [
                new RunningModelSnapshot("other-model", "other-model", ExpiresAt: null, SizeBytes: 3 * Gb, SizeVramBytes: 3 * Gb)
            ]
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.True(decision.OllamaEvictionWarning, "a different running Ollama model that won't fit must flag the eviction warning.");
    }

    [Test]
    public async Task Capacity_WhenDeviceAuditDegradesToCpuMode_SizesAgainstRam_NotPhantomVram()
    {
        // On a physical GPU box whose selected runtime silently fell back to the CPU, the device audit hands the
        // gate a CPU-mode EFFECTIVE profile (VRAM unknown). A model that would fit the 16 GB GPU but not the 4 GB free RAM
        // must therefore be rejected on the byte budget — capacity must never pretend the unusable VRAM exists.
        var harness = new Harness
        {
            Profile = CpuProfile(4 * Gb),
            Footprint = CpuFootprint(8 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None);

        AssertEx.Equal(CapacityVerdict.RejectInsufficient, decision.Verdict);
        AssertEx.Null(decision.Reservation);
    }

    [Test]
    public async Task Capacity_Decision_ComposesWithGpuLoadAdmission_NoDeadlock()
    {
        // Lock ordering: the capacity decision gate and the GPU-load admission gate are never nested — the
        // decision fully completes (releasing the ledger gate) BEFORE the supervisor spawn acquires the load gate. Prove
        // it composes deadlock-free: even while a GPU load holds the admission gate, a capacity decision still completes.
        using var admission = new GpuModelLoadAdmission(new GpuModelLoadAdmissionOptions());
        using var heldTicket = await admission.AcquireAsync(CancellationToken.None);

        var harness = new Harness
        {
            Profile = GpuProfile(16 * Gb),
            Footprint = GpuFootprint(4 * Gb)
        };
        var service = harness.Build();

        var decision = await service.DecideAsync(Model, ModelRole.Chat, CancellationToken.None)
                                    .WaitAsync(TimeSpan.FromSeconds(3));

        AssertEx.Equal(CapacityVerdict.Allow, decision.Verdict);
        decision.Reservation?.Dispose();
    }

    private static ModelFootprint GpuFootprint(long gpuBytes, long ramBytes = Gb) =>
        ModelFootprint.Known(new ResourceFootprint(gpuBytes, ramBytes));

    private static ModelFootprint CpuFootprint(long ramBytes) =>
        ModelFootprint.Known(new ResourceFootprint(GpuBytes: 0, ramBytes));

    private static ModelFootprint AdmissionFootprint(long gpuBytes, int contextTokens, string modelName = Model)
    {
        return AdmissionFootprint(new ResourceFootprint(gpuBytes, RamBytes: 0),
            contextTokens,
            modelName,
            GpuVariant.Cuda);
    }

    private static ModelFootprint AdmissionFootprint(ResourceFootprint resources,
        int contextTokens,
        string modelName,
        GpuVariant variant)
    {
        var allocation = new ProcessContextAllocation(contextTokens,
            ModelTrainContextTokens: 131072,
            ProcessContextAllocationSource.HardwareTier,
            variant == GpuVariant.Cpu ? ProcessPlacementMode.Cpu : ProcessPlacementMode.GpuResident,
            resources,
            ContentIdentity: $"{modelName}:0",
            CacheKey: $"capacity-test:{modelName}");
        return ModelFootprint.Known(new ProcessLaunchAdmission(modelName,
            ModelRole.Chat,
            variant,
            ResolvedLaunchArguments.Explore(),
            allocation));
    }

    // An empty-GPU profile: the measured free-VRAM baseline equals total, so existing fit expectations are unchanged.
    private static HardwareProfile GpuProfile(long vramBytes)
    {
        return GpuProfileWithFreeVram(vramBytes, availableVramBytes: vramBytes);
    }

    // A GPU profile with an explicit free-VRAM baseline (or null to force the total-VRAM fallback). Total VRAM is held
    // separate from free so a test can model residents already holding VRAM the ledger never saw.
    private static HardwareProfile GpuProfileWithFreeVram(long vramBytes, long? availableVramBytes)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 64 * Gb,
            AvailableRamBytes = 48 * Gb,
            VramBytes = vramBytes,
            AvailableVramBytes = availableVramBytes,
            VramKnown = true,
            GpuVendor = GpuVendor.Nvidia,
            GpuAccelAvailable = true,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    private static HardwareProfile CpuProfile(long availableRam)
    {
        return new HardwareProfile
        {
            TotalRamBytes = 32 * Gb,
            AvailableRamBytes = availableRam,
            VramBytes = null,
            VramKnown = false,
            GpuVendor = GpuVendor.Unknown,
            GpuAccelAvailable = false,
            CpuCores = 16,
            FreeDiskBytes = 500 * Gb
        };
    }

    // Assembles the capacity service over mocked probes + a real ledger. Mutate the public fields before Build().
    private sealed class Harness
    {
        public bool CloudSelected { get; init; }
        public string ProviderName { get; init; } = Llamacpp;
        public int MaxLoadedProcesses { get; init; } = 3;
        public HardwareProfile Profile { get; init; } = null!;
        public ModelFootprint Footprint { get; init; } = ModelFootprint.Unknown;
        public IReadOnlyList<LlamaServerProcessHealth> RunningLlama { get; init; } = [];
        public IReadOnlyList<RunningModelSnapshot> RunningOllama { get; init; } = [];
        public bool SupervisorProbeThrows { get; init; }
        public bool OllamaProbeThrows { get; init; }
        public LlamaServerExternalEndpointOptions ExternalEndpoints { get; init; } = new();
        public LlamaServerSupervisorOptions SupervisorOptions { get; init; } = new();

        public IRuntimeDeviceAudit RuntimeAudit { get; } = Substitute.For<IRuntimeDeviceAudit>();
        public ILlamaServerProcessSupervisor Supervisor { get; } = Substitute.For<ILlamaServerProcessSupervisor>();
        public IModelFootprintProvider FootprintProvider { get; } = Substitute.For<IModelFootprintProvider>();
        public IProcessLaunchAdmissionRegistry LaunchAdmissions { get; init; } = new ProcessLaunchAdmissionRegistry();
        public PendingFootprintLedger Ledger { get; } = new();

        public CapacityService Build()
        {
            var cloud = Substitute.For<IActiveCloudChatClientFactory>();
            cloud.IsCloudProviderSelected(Arg.Any<string?>()).Returns(CloudSelected);

            var resolver = Substitute.For<ILocalModelProviderResolver>();
            resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(ProviderName));
            resolver.MaxLoadedProcesses.Returns(MaxLoadedProcesses);

            // The effective profile the audit hands the capacity gate; for these tests it is the raw Profile unchanged
            // (no CPU fallback). A dedicated fallback test overrides GetEffectiveProfileAsync to return a CPU-degraded one.
            RuntimeAudit.GetEffectiveProfileAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(Profile));
            RuntimeAudit.GetAuditAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(new RuntimeDeviceAuditState
                        {
                            InferenceBackend = "cuda",
                            GpuExpected = true,
                            CpuFallback = false
                        }));
            Supervisor.CheckHealthAsync(Arg.Any<CancellationToken>())
                      .Returns(SupervisorProbeThrows
                          ? Task.FromException<IReadOnlyList<LlamaServerProcessHealth>>(new InvalidOperationException("Synthetic supervisor probe failure."))
                          : Task.FromResult(RunningLlama));
            FootprintProvider.ResolveFootprintAsync(Arg.Any<string>(),
                                 Arg.Any<ModelRole>(),
                                 Arg.Any<HardwareProfile>(),
                                 Arg.Any<int?>(),
                                 Arg.Any<string?>(),
                                 Arg.Any<CancellationToken>())
                             .Returns(call =>
                             {
                                 if (!string.Equals(ProviderName, Llamacpp, StringComparison.OrdinalIgnoreCase)
                                     || Footprint is not { IsKnown: true, Admission: null })
                                 {
                                     return Task.FromResult(Footprint);
                                 }

                                 return Task.FromResult(AdmissionFootprint(Footprint.Resources,
                                     contextTokens: 8192,
                                     call.ArgAt<string>(0),
                                     Profile.GpuAccelAvailable ? GpuVariant.Cuda : GpuVariant.Cpu));
                             });
            FootprintProvider.TryCommitAdmissionFootprint(Arg.Any<ModelFootprint>(), out Arg.Any<ModelFootprint>())
                             .Returns(call =>
                             {
                                 call[1] = call[0];
                                 return true;
                             });

            var ollama = Substitute.For<IOllamaModelService>();
            ollama.ListRunningModelsAsync(Arg.Any<CancellationToken>())
                  .Returns(OllamaProbeThrows
                      ? Task.FromException<IReadOnlyList<RunningModelSnapshot>>(new InvalidOperationException("Synthetic Ollama probe failure."))
                      : Task.FromResult(RunningOllama));

            return new CapacityService(cloud,
                resolver,
                RuntimeAudit,
                Supervisor,
                ollama,
                FootprintProvider,
                Ledger,
                LaunchAdmissions,
                ExternalEndpoints,
                SupervisorOptions);
        }
    }
}
