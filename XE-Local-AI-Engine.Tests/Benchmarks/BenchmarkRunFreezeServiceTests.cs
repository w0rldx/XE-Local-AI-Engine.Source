namespace XE_Local_AI_Engine.Tests.Benchmarks;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Agents;
using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkRunFreezeServiceTests
{
    [Test]
    public async Task Start_UsesExactTaskAcquiresDistinctModelsInOrderAndCarriesCommitGuard()
    {
        var harness = new FreezeHarness(judgeModel: "z-judge.gguf");

        _ = await harness.StartAsync();

        // Only the primary: the judge is defined by the project's policy revision and frozen per attempt, so a freeze
        // neither leases nor resolves it.
        AssertEx.True(harness.LeaseProvider.Acquired.SequenceEqual(["a-primary.gguf"]),
            "A freeze must lease exactly the primary model.");
        AssertEx.Equal("exact task", AssertEx.NotNull(harness.SnapshotInput).CoreTask);
        AssertEx.Equal(GpuVariant.Cpu, harness.SnapshotInput!.PrimaryRuntime.Variant);
        AssertEx.Equal(4096, harness.SnapshotInput.PrimaryRuntime.ContextTokens);
        AssertEx.Equal(BenchmarkFrozenPolicies.FixedSeedPolicy, harness.SnapshotInput.PrimarySampling.SeedPolicy);
        AssertEx.Equal("0", harness.SnapshotInput.PrimarySampling.SeedValue);
        AssertEx.NotNull(harness.Command);
        AssertEx.NotNull(harness.Command!.FreezeCommitGuard);
        AssertEx.True(harness.LeaseProvider.Leases.All(static lease => lease.Disposed), "All installed-model read leases must be released after commit.");
        _ = harness.Resolver.Received(1).ResolveAsync(harness.AgentId, "a-primary.gguf", "exact task", true, false, false, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_WithRepeatsAndAWarmup_EnqueuesOneGroupInQueueOrderAgainstASingleFreeze()
    {
        var harness = new FreezeHarness();

        var runs = await harness.StartAsync(repeatCount: 3, warmup: true);

        // ONE freeze, four inserts. Re-freezing per repeat could straddle a runtime swap and give two runs of the same
        // "group" different snapshots — which is exactly the variable a repeat is supposed to hold still.
        AssertEx.Equal(1, harness.SnapshotsCreated, "A repeat group must be frozen once and shared.");
        AssertEx.Equal(4, runs.Count);
        AssertEx.Equal(4, harness.Commands.Count);
        var groupId = harness.Commands[0].RepeatGroupId;
        AssertEx.True(groupId is not null && groupId != Guid.Empty, "A repeat group must carry a real id.");
        AssertEx.True(harness.Commands.TrueForAll(command => command.RepeatGroupId == groupId), "Every run of a group shares its id.");
        AssertEx.True(harness.Commands.Select(static command => command.RepeatIndex).SequenceEqual<int?>([0, 1, 2, 3]),
            "The warm-up is index 0 and the measured repeats are 1..N, in queue order.");
        AssertEx.True(harness.Commands.Select(static command => command.IsWarmup).SequenceEqual([true, false, false, false]),
            "Only the index-0 run is a warm-up.");

        // ONE store call and ONE compare-and-swap, the caller's own. Inserting per run against a chained version let a
        // concurrent writer land mid-group: the caller got a conflict and no ids while the runs already inserted
        // stayed queued and consumed the exclusive runtime.
        AssertEx.Equal(1, harness.StoreCalls, "A repeat group must be inserted by a single, atomic store call.");
        AssertEx.True(harness.ExpectedVersions.SequenceEqual([7L]), "The group presents the caller's expected version, once.");
    }

    [Test]
    public async Task Start_InThroughputMode_FreezesOneSamplingAndRecordsItOnEveryRun()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync(repeatCount: 3, BenchmarkRepeatMode.Throughput);

        // The default must stay exactly what it was: one snapshot, temperature 0, one seed. Anything else would make
        // every existing repeat group a different measurement than the one it recorded.
        AssertEx.Equal(expected: 1, harness.SnapshotsCreated, "A throughput group is one freeze, shared.");
        AssertEx.True(harness.Commands.TrueForAll(static command => command.RepeatMode == BenchmarkRepeatMode.Throughput));
        AssertEx.True(harness.Commands.TrueForAll(static command => string.Equals(command.SamplingSeed, "0", StringComparison.Ordinal)),
            "One fixed seed across the group is what makes the answer identical.");
        AssertEx.True(harness.Commands.TrueForAll(static command => command.SamplingTemperature is 0d));
        AssertEx.True(harness.SnapshotInputs.TrueForAll(static input => input.PrimarySampling.Temperature is 0f));
    }

    [Test]
    public async Task Start_InAnswerVarianceMode_AdvancesOnlyTheSeedAcrossOneSharedLaunch()
    {
        var harness = new FreezeHarness();

        var runs = await harness.StartAsync(repeatCount: 3, BenchmarkRepeatMode.AnswerVariance, temperature: 0.9d);

        AssertEx.Equal(expected: 3, runs.Count);
        AssertEx.True(harness.Commands.Select(static command => command.SamplingSeed).SequenceEqual(["1", "2", "3"], StringComparer.Ordinal),
            "Each repeat advances the seed off the base, so the runs differ in exactly one input.");
        AssertEx.True(harness.Commands.TrueForAll(static command => command.RepeatMode == BenchmarkRepeatMode.AnswerVariance));
        // EXACT, not within a tolerance: the run column and the export are double, and a float carried into them
        // widens to 0.899999976158142 — a tolerance is what let that reach the operator's CSV.
        AssertEx.True(harness.Commands.TrueForAll(static command => command.SamplingTemperature is 0.9d),
            "Every run of the group samples at the requested temperature, recorded exactly.");

        // The seed and the temperature are SAMPLING. If either reached the launch arguments, the runs of one group
        // would carry different launch identities and stop being comparable as a launch — which is the whole point of
        // freezing a group together.
        AssertEx.Equal(expected: 3, harness.SnapshotsCreated, "A distinct seed is a distinct frozen sampling.");
        AssertEx.True(harness.SnapshotInputs.TrueForAll(input => input.PrimaryRuntime == harness.SnapshotInputs[0].PrimaryRuntime),
            "Nothing about the launch may differ across an answer-variance group.");
        var launchIdentities = harness.Commands.Select(static command => command.PrimaryLaunchIntent?.IntendedLaunchIdentity).Distinct(StringComparer.Ordinal);
        AssertEx.Equal(expected: 1, launchIdentities.Count(), "One group, one launch identity.");
    }

    [Test]
    public async Task Start_InAnswerVarianceModeWithNoTemperature_TakesTheDefault()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync(repeatCount: 1, BenchmarkRepeatMode.AnswerVariance);

        AssertEx.Equal<double?>(BenchmarkRunFreezeService.DefaultAnswerVarianceTemperature, AssertEx.NotNull(harness.Command).SamplingTemperature,
            "An omitted temperature takes the everyday default rather than falling back to the deterministic 0.");
    }

    [Test]
    [Arguments(0f)]
    [Arguments(-1f)]
    [Arguments(2.5f)]
    public async Task Start_InAnswerVarianceModeWithAnImpossibleTemperature_IsRejectedBeforeAnythingIsFrozen(float temperature)
    {
        var harness = new FreezeHarness();

        // Zero would silently be a throughput group wearing an answer-variance label.
        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(async () =>
            await harness.StartAsync(repeatCount: 2, BenchmarkRepeatMode.AnswerVariance, temperature));

        AssertEx.Equal(expected: 0, harness.SnapshotsCreated);
        AssertEx.Equal(expected: 0, harness.StoreCalls);
    }

    [Test]
    public async Task Start_WithASharedScope_ProbesTheBinaryOnceAndVerifiesEachModelOnce()
    {
        // What a batch actually pays for: every cell used to run its own llama-server capability probe and its own
        // full re-verification of the same model files, serially, before the endpoint answered.
        var harness = new FreezeHarness();
        await using var scope = new BenchmarkFreezeScope();

        _ = await harness.StartAsync(scope);
        _ = await harness.StartAsync(scope);

        _ = harness.LaunchInspector.Received(1).InspectAsync(Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 1, harness.LeaseProvider.Acquired.Count, "The same model must be verified once per request, not once per cell.");
        AssertEx.Equal(expected: 2, harness.StoreCalls, "Both cells still start.");
    }

    [Test]
    public async Task Start_WithNoScope_StaysSelfContained()
    {
        // The single-run path must keep behaving exactly as it did: acquire, freeze, release. A scope leaking across
        // calls would hold a model's read lease open long after the request that took it had answered.
        var harness = new FreezeHarness();

        _ = await harness.StartAsync();
        _ = await harness.StartAsync();

        _ = harness.LaunchInspector.Received(2).InspectAsync(Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 2, harness.LeaseProvider.Acquired.Count);
        AssertEx.True(harness.LeaseProvider.Leases.TrueForAll(static lease => lease.Disposed), "A self-contained freeze releases every lease it took.");
    }

    [Test]
    public async Task Start_ForAPlainSingleRun_LeavesTheRepeatColumnsNull()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync();

        // A group only exists when there is something to group; a single run must look exactly as it always did.
        AssertEx.Null(AssertEx.NotNull(harness.Command).RepeatGroupId);
        AssertEx.Null(harness.Command!.RepeatIndex);
        AssertEx.False(harness.Command.IsWarmup, "A single run is never a warm-up.");
    }

    [Test]
    public async Task Start_WithRepeatsButNoWarmup_NumbersTheRepeatsFromOne()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync(repeatCount: 2, warmup: false);

        AssertEx.True(harness.Commands.Select(static command => command.RepeatIndex).SequenceEqual<int?>([1, 2]),
            "Without a warm-up there is no index 0 — the measured repeats still start at 1.");
        AssertEx.True(harness.Commands.TrueForAll(static command => !command.IsWarmup), "Nothing is a warm-up unless one was asked for.");
    }

    [Test]
    [Arguments(0)]
    [Arguments(11)]
    public async Task Start_WithARepeatCountOutOfRange_IsRejectedBeforeAnythingIsFrozen(int repeatCount)
    {
        var harness = new FreezeHarness();

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(async () => await harness.StartAsync(repeatCount, warmup: false));

        AssertEx.Equal(0, harness.Commands.Count, "A rejected repeat count must not leave a partial group behind.");
    }

    [Test]
    public async Task Start_WhenTheModelCannotBeVerified_IsAnEligibilityRefusalRatherThanAnUnhandledFailure()
    {
        // Verification moved OFF the catalog listing onto freeze, so a model whose files no longer match its registry
        // entry now LISTS happily and only fails here. Unmapped, InstalledGgufSnapshotException is in neither the
        // endpoint's handled-exception filter nor its KeyNotFoundException clause: it escaped as a 500, and inside a
        // batch it killed every cell after it instead of rejecting one.
        var harness = new FreezeHarness(unverifiableModel: true);

        var exception = await AssertEx.ThrowsAsync<BenchmarkEligibilityException>(async () => await harness.StartAsync());

        AssertEx.Contains(exception.Message, "could not be verified");
        AssertEx.False(exception.Message.Contains("registry value", StringComparison.Ordinal),
            "The store's own reason is logged, never returned to the caller.");
        AssertEx.Equal(0, harness.Commands.Count, "Nothing may be enqueued for a model that failed verification.");
    }

    [Test]
    public async Task Start_CopiesTheProjectGenerationTimeoutOntoTheRun()
    {
        var pinned = new FreezeHarness(invocationTimeoutSeconds: 1800);
        var defaulted = new FreezeHarness();

        _ = await pinned.StartAsync();
        _ = await defaulted.StartAsync();

        // Copied onto the run, not read from the project at execution: a run replays with the budget it was started
        // under, exactly like its context.
        AssertEx.Equal<int?>(1800, AssertEx.NotNull(pinned.Command).InvocationTimeoutSeconds);
        AssertEx.Null(AssertEx.NotNull(defaulted.Command).InvocationTimeoutSeconds, "No project setting means the node default.");
    }

    [Test]
    public async Task Start_FreezesTheProjectOutputBudgetIntoTheRunSampling()
    {
        var budgeted = new FreezeHarness(maxOutputTokens: 2048);
        var unbudgeted = new FreezeHarness();

        _ = await budgeted.StartAsync();
        _ = await unbudgeted.StartAsync();

        AssertEx.Equal<int?>(2048, AssertEx.NotNull(budgeted.SnapshotInput).PrimarySampling.MaxOutputTokens,
            "The budget is frozen per run, so a later project edit cannot change what an existing run replays.");
        AssertEx.Null(AssertEx.NotNull(unbudgeted.SnapshotInput).PrimarySampling.MaxOutputTokens,
            "No budget means context-limited, which is the sampling every existing snapshot already hashes.");
    }

    [Test]
    public async Task Start_WithAReasoningBudgetOnAModelThatDoesNotThink_FreezesItAsUnenforceable()
    {
        // A model that does not reason at all cannot have its reasoning capped, and the capability record defaults
        // ReasoningBudgetEnforceable to true for anything undetected — so freezing that field alone claimed the cap
        // was enforceable for every non-thinking model, sent it on the wire, and llama-server accepted and ignored it.
        var nonThinking = new FreezeHarness(reasoningBudgetTokens: 4096);
        var thinking = new FreezeHarness(reasoningBudgetTokens: 4096, supportsThinking: true);
        var unbudgeted = new FreezeHarness();

        _ = await nonThinking.StartAsync();
        _ = await thinking.StartAsync();
        _ = await unbudgeted.StartAsync();

        var frozen = AssertEx.NotNull(nonThinking.SnapshotInput).PrimarySampling;
        AssertEx.Equal<int?>(4096, frozen.ReasoningBudgetTokens, "The pinned budget is still frozen — it is the ENFORCEABILITY that is false.");
        AssertEx.Equal<bool?>(false, frozen.ReasoningBudgetEnforceable,
            "SupportsThinking is half the answer: a non-thinking model can never honour the cap, whatever the template says.");
        AssertEx.Equal<bool?>(true, AssertEx.NotNull(thinking.SnapshotInput).PrimarySampling.ReasoningBudgetEnforceable);

        // Null, never false: the member is omitted when writing null, which is what keeps every snapshot frozen
        // before the field existed hashing to the bytes it already hashed to.
        AssertEx.Null(AssertEx.NotNull(unbudgeted.SnapshotInput).PrimarySampling.ReasoningBudgetEnforceable,
            "No pinned budget means no enforceability claim at all.");
    }

    [Test]
    public async Task Start_AutoOnAGpuBinaryThatAcceptsTheOptimizedVector_FreezesQuantizedKv()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, intent.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, intent.KvCacheTypeSource);
        AssertEx.Null(intent.KvAutoReason);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionOn, intent.FlashAttentionMode);
        AssertEx.Equal("cuda", intent.Variant);
        AssertEx.Equal("manifest-sha", intent.IntendedExecutableSha256);
        var runtime = AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime;
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, runtime.KvTypeK);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, runtime.KvTypeV);
        AssertEx.True(runtime.FlashAttention, "A quantized KV cache must pin the fused flash-attention path.");
    }

    [Test]
    public async Task Start_AutoWhenTheManifestDoesNotAdvertiseTheVector_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, supportsQuantizedKv: false);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceAuto, intent.KvCacheTypeSource);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonManifestUnsupported, intent.KvAutoReason);
        AssertEx.Equal(LlamaServerLaunchProjection.FlashAttentionAuto, intent.FlashAttentionMode);
        AssertEx.Null(AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime.KvTypeK);
    }

    [Test]
    public async Task Start_AutoWhenTheOptimizedConfigWasRecordedAsFailing_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, optimizedConfigDisabled: true);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonFallbackDisabled, intent.KvAutoReason);
    }

    [Test]
    public async Task Start_AutoWhenTheBinaryCouldNotBeProbed_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, probeSucceeded: false);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonProbeUnavailable, intent.KvAutoReason);
    }

    [Test]
    public async Task Start_AutoOnACpuBuild_FreezesF16WithAReason()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonCpuVariant, intent.KvAutoReason);
        AssertEx.Equal("cpu", intent.Variant);
    }

    [Test]
    public async Task Start_ExplicitF16OnACpuBuild_IsAccepted()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync(BenchmarkKvCacheType.F16);

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkKvCacheType.SourceExplicit, intent.KvCacheTypeSource);
        AssertEx.Null(intent.KvAutoReason);
    }

    [Test]
    public async Task Start_ExplicitQuantizedKvOnACpuBuild_IsRefused()
    {
        var harness = new FreezeHarness();

        var exception = await AssertEx.ThrowsAsync<BenchmarkUnsupportedKvCacheTypeException>(() => harness.StartAsync(BenchmarkKvCacheType.Q4_0));

        AssertEx.Contains(exception.Message, "CPU");
    }

    [Test]
    public async Task Start_ExplicitQuantizedKvTheManifestDoesNotAdvertise_IsRefused()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, supportsQuantizedKv: false);

        var exception = await AssertEx.ThrowsAsync<BenchmarkUnsupportedKvCacheTypeException>(() => harness.StartAsync(BenchmarkKvCacheType.Q8_0));

        AssertEx.Contains(exception.Message, "does not accept");
    }

    [Test]
    public async Task Start_ExplicitQuantizedKvWhenTheBinaryCouldNotBeProbed_IsRefusedNamingTheProbe()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, probeSucceeded: false);

        var exception = await AssertEx.ThrowsAsync<BenchmarkUnsupportedKvCacheTypeException>(() => harness.StartAsync(BenchmarkKvCacheType.Q8_0));

        AssertEx.Contains(exception.Message, "could not be inspected");
    }

    [Test]
    public async Task Start_ExplicitPickOverAFrozenProfile_KeepsThePlacementItWasFittedWith()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda,
            profile: static context => ResolvedLaunchArguments.Replay(context, 33, "0.7,0.3", "exps=CPU", BenchmarkKvCacheType.Q4_0, BenchmarkKvCacheType.Q4_0, flashAttn: true));

        _ = await harness.StartAsync(BenchmarkKvCacheType.F16);

        var runtime = AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime;
        AssertEx.Equal<int?>(33, runtime.GpuLayers);
        AssertEx.Equal("0.7,0.3", runtime.TensorSplit);
        AssertEx.Equal("exps=CPU", runtime.OverrideTensor);
        AssertEx.Null(runtime.KvTypeK);
        AssertEx.Null(runtime.KvTypeV);
        AssertEx.False(runtime.FlashAttention, "Dropping the quantized KV cache must drop the flag it requires with it.");
    }

    [Test]
    public async Task Start_IntendedLaunchIdentity_IsTheProjectionOfTheFrozenVector()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda,
            profile: static context => ResolvedLaunchArguments.Replay(context, 33));

        _ = await harness.StartAsync(BenchmarkKvCacheType.Q8_0);

        var runtime = AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime;
        var policy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1;
        var expected = LlamaServerLaunchProjection.From(GpuVariant.Cuda,
                                                      runtime.ToResolvedLaunchArguments(),
                                                      plan: null,
                                                      ModelRole.Chat,
                                                      policy.ChatCacheReuse,
                                                      policy.ChatCacheRamMiB)
                                                  .ComputeIdentity();
        AssertEx.Equal(expected, AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent).IntendedLaunchIdentity);
    }

    [Test]
    public async Task Start_FreezesTheVariantTheInspectedBinaryReports_SoTheIntendedDigestDescribesIt()
    {
        // A second selection could answer differently from the one the inspection used; the digest recorded as
        // INTENDED must belong to the binary that was actually inspected, or every run reads as intended != effective.
        var harness = new FreezeHarness(variant: GpuVariant.Cpu, inspectedVariant: GpuVariant.Cuda, judgeModel: "z-judge.gguf");

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal("cuda", intent.Variant);
        AssertEx.Equal("manifest-sha", intent.IntendedExecutableSha256);
        AssertEx.Equal(BenchmarkKvCacheType.Q8_0, intent.KvCacheType, "The inspected GPU binary's own capabilities decide Auto.");
        AssertEx.Equal(GpuVariant.Cuda, AssertEx.NotNull(harness.SnapshotInput).PrimaryRuntime.Variant);
    }

    [Test]
    public async Task Start_WhenTheBinaryCannotBeInspected_FallsBackToTheSelectedVariantAndRecordsNoDigest()
    {
        var harness = new FreezeHarness(variant: GpuVariant.Cuda, inspectionFails: true);

        _ = await harness.StartAsync();

        var intent = AssertEx.NotNull(harness.Command!.PrimaryLaunchIntent);
        AssertEx.Equal("cuda", intent.Variant);
        AssertEx.Null(intent.IntendedExecutableSha256, "No inspection means no digest to claim as intended.");
        AssertEx.Equal(BenchmarkKvCacheType.F16, intent.KvCacheType);
        AssertEx.Equal(BenchmarkRunFreezeService.AutoReasonProbeUnavailable, intent.KvAutoReason);
    }

    [Test]
    public async Task Start_UnknownKvCacheType_IsRejectedBeforeAnythingIsFrozen()
    {
        var harness = new FreezeHarness();

        _ = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.StartAsync("q3_k"));

        AssertEx.Null(harness.Command);
    }

    [Test]
    public async Task Start_OnASingleItemProject_FreezesExactlyWhatItAlwaysDid()
    {
        var harness = new FreezeHarness();

        _ = await harness.StartAsync();

        // The single-item degenerate case is byte-identical: one snapshot, the project's own task, no repeat group,
        // and a NULL cell key — the store's instruction to stamp the run's own singleton cell, exactly what every
        // legacy pre-suite run already carries.
        AssertEx.Equal(1, harness.SnapshotsCreated);
        AssertEx.Equal("exact task", AssertEx.NotNull(harness.SnapshotInput).CoreTask);
        AssertEx.Equal(1, harness.Commands.Count);
        AssertEx.Null(harness.Commands[0].CellKey, "A one-item, one-repeat freeze names no cell group.");
        AssertEx.Null(harness.Commands[0].RepeatGroupId);
        AssertEx.Null(harness.Commands[0].RepeatIndex);
        AssertEx.Equal<Guid?>(harness.TaskItems[0].Id, harness.Commands[0].TaskItemId);
        AssertEx.Equal<int?>(0, harness.Commands[0].TaskItemIndex);
    }

    [Test]
    public async Task Start_WithThreeItemsAndNoRepeats_ProducesOneCellOfThreeInOneStoreCall()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        _ = await harness.StartAsync();

        // THE finding: deriving the cell from repeat_group_id alone left every run of a plain multi-item suite in its
        // own singleton cell, so every cell was missing two of three items and the project ranked nothing.
        AssertEx.Equal(3, harness.Commands.Count);
        AssertEx.Equal(1, harness.StoreCalls, "A suite is inserted by a single, atomic store call.");
        var cells = harness.Commands.Select(static command => command.CellKey).Distinct(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(1, cells.Length, "Three items measured together are ONE cell.");
        AssertEx.True(cells[0]?.StartsWith("cell:", StringComparison.Ordinal) is true && cells[0]!.EndsWith(":1", StringComparison.Ordinal),
            "The single measured repeat is index 1 by the existing convention.");
    }

    [Test]
    public async Task Start_WithThreeItemsAndNoRepeats_LeavesRepeatGroupIdAndRepeatIndexNull()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        _ = await harness.StartAsync();

        // The no-regression half. Fabricating a repeat group to get a cell would change repeat semantics — and the
        // meaning of repeat_group_id — for every existing query.
        AssertEx.True(harness.Commands.TrueForAll(static command => command.RepeatGroupId is null));
        AssertEx.True(harness.Commands.TrueForAll(static command => command.RepeatIndex is null));
    }

    [Test]
    public async Task Start_WithThreeItems_FreezesThreeDistinctCoreTasks()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        _ = await harness.StartAsync();

        // The snapshot cache is keyed on (item, seed), not on the seed alone. Keyed on the seed, all three runs would
        // have shared item a's serialized snapshot — every run answering the first prompt while its task_item_id
        // column claimed otherwise, and nothing failing loudly.
        AssertEx.Equal(3, harness.SnapshotsCreated);
        AssertEx.True(harness.SnapshotInputs.Select(static input => input.CoreTask).SequenceEqual(["item a", "item b", "item c"], StringComparer.Ordinal));
        AssertEx.Equal(3, harness.Commands.Count);
    }

    [Test]
    public async Task Start_WithThreeItemsAndTwoAnswerVarianceRepeats_FreezesOneSnapshotPerItemAndSeedPair()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        _ = await harness.StartAsync(repeatCount: 2, BenchmarkRepeatMode.AnswerVariance, temperature: 0.9d);

        // Six runs, six distinct (item, seed) pairs, six snapshots. A cache keyed on either half alone collapses to
        // three and silently mislabels half the batch.
        AssertEx.Equal(6, harness.SnapshotsCreated);
        var pairs = harness.SnapshotInputs.Select(static input => input.CoreTask + "|" + input.PrimarySampling.SeedValue)
                           .Distinct(StringComparer.Ordinal)
                           .Count();
        AssertEx.Equal(6, pairs);
    }

    [Test]
    public async Task Start_WithThreeItemsAndTwoRepeats_ProducesTwoCellsOfThree()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        var runs = await harness.StartAsync(repeatCount: 2, warmup: false);

        AssertEx.Equal(6, runs.Count);
        var cells = harness.Commands.GroupBy(static command => command.CellKey, StringComparer.Ordinal).ToArray();
        AssertEx.Equal(2, cells.Length, "Two repeats of a three-item suite are two cells.");
        AssertEx.True(cells.All(static cell => cell.Count() == 3), "Each cell holds every item once.");
        AssertEx.True(cells.All(static cell => cell.Select(static command => command.TaskItemId).Distinct().Count() == 3));
    }

    [Test]
    public async Task Start_WhenARepeatGroupAndACellGroupBothExist_TheyAreTheSameGuid()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b"]);

        _ = await harness.StartAsync(repeatCount: 2, warmup: false);

        // One identity, not two to keep in sync: the cell key is built from the repeat group's own GUID whenever
        // there is one.
        var groupId = harness.Commands[0].RepeatGroupId;
        AssertEx.True(groupId is not null && groupId != Guid.Empty, "A repeat group must carry a real id.");
        AssertEx.True(harness.Commands.TrueForAll(command =>
                command.CellKey == "cell:" + groupId!.Value.ToString("D") + ":" + command.RepeatIndex),
            "The cell key is the repeat group's GUID plus the repeat index.");
    }

    [Test]
    public async Task Start_WithAWarmup_PutsTheWarmupRunsInTheirOwnCell()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b"]);

        _ = await harness.StartAsync(repeatCount: 1, warmup: true);

        // A warm-up is stamped like everything else — an identity is not a ranking decision — and sits at repeat
        // index 0, so it forms a cell the ranking read drops before it groups anything.
        var warmupCells = harness.Commands.Where(static command => command.IsWarmup).Select(static command => command.CellKey).Distinct(StringComparer.Ordinal).ToArray();
        var measuredCells = harness.Commands.Where(static command => !command.IsWarmup).Select(static command => command.CellKey).Distinct(StringComparer.Ordinal).ToArray();
        AssertEx.Equal(1, warmupCells.Length);
        AssertEx.Equal(1, measuredCells.Length);
        AssertEx.True(warmupCells[0]!.EndsWith(":0", StringComparison.Ordinal));
        AssertEx.True(measuredCells[0]!.EndsWith(":1", StringComparison.Ordinal));
    }

    [Test]
    public async Task Start_StampsTheFourIdentityColumnsOnEveryRun()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"], taskItemSetHash: "v1:set-hash");

        _ = await harness.StartAsync();

        for (var index = 0; index < 3; index++)
        {
            var command = harness.Commands[index];
            AssertEx.Equal<Guid?>(harness.TaskItems[index].Id, command.TaskItemId);
            AssertEx.Equal<int?>(index, command.TaskItemIndex);
            AssertEx.Equal(harness.TaskItems[index].InputHash, AssertEx.NotNull(command.TaskInputHash));
            AssertEx.Equal("v1:set-hash", AssertEx.NotNull(command.TaskItemSetHash));
            AssertEx.NotNull(command.CellKey);
        }
    }

    [Test]
    public async Task Start_OrdersTheBatchItemMajorWithinEachRepeat()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        _ = await harness.StartAsync(repeatCount: 2, warmup: false);

        // Items are the INNER loop, so a partially drained queue yields whole comparable cells rather than one item
        // across every cell.
        AssertEx.True(harness.Commands.Select(static command => command.TaskItemIndex).SequenceEqual<int?>([0, 1, 2, 0, 1, 2]));
        AssertEx.True(harness.Commands.Select(static command => command.RepeatIndex).SequenceEqual<int?>([1, 1, 1, 2, 2, 2]));
    }

    [Test]
    public async Task Start_PastTheRunCap_IsRefusedWithTheComputedCount()
    {
        var harness = new FreezeHarness(itemPrompts: [.. Enumerable.Range(0, 12).Select(index => "item " + index)]);

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.StartAsync(repeatCount: 9, warmup: false));

        // 12 items x 9 repeats = 108. The count is in the message because "too many runs" without it tells the
        // operator nothing about which knob to turn.
        AssertEx.True(failure.Message.Contains("108", StringComparison.Ordinal), "The refusal must name the computed run count.");
        AssertEx.Equal(0, harness.StoreCalls, "Nothing is inserted when the cap refuses the request.");
    }

    [Test]
    public async Task Start_WithThreeItems_InspectsTheRuntimeOnce()
    {
        var harness = new FreezeHarness(itemPrompts: ["item a", "item b", "item c"]);

        _ = await harness.StartAsync();

        // One manifest digest per freeze. A second inspection could straddle a runtime swap and give two items of one
        // measurement different launch answers.
        _ = harness.LaunchInspector.Received(1).InspectAsync(Arg.Any<CancellationToken>());
        // The agent runtime, by contrast, is resolved PER ITEM: the task text is the resolver's retrieval query.
        _ = harness.Resolver.Received(3).ResolveAsync(harness.AgentId, "a-primary.gguf", Arg.Any<string>(), true, false, false, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A probe's cases are ordinary leaf items, so the fan-out reaches them with no NIAH-specific code: six cases
    ///     are six runs of one cell, each answering its own haystack and stamped with its own item.
    /// </summary>
    [Test]
    public async Task Start_WithSixProbeCases_FreezesSixRunsOfOneCell()
    {
        var harness = new FreezeHarness(probeContextTokens: [2048, 2048, 2048, 3072, 3072, 3072]);

        _ = await harness.StartAsync();

        AssertEx.Equal(6, harness.Commands.Count, "Six generated cases are six runs; a probe costs what its cases cost.");
        AssertEx.Equal(1, harness.StoreCalls, "The whole cell goes in through one all-or-nothing insert.");
        AssertEx.Equal(6, harness.Commands.Select(static command => command.TaskItemId).Distinct().Count(),
            "Each run names the case it answered.");
        AssertEx.Equal(1, harness.Commands.Select(static command => command.CellKey).Distinct(StringComparer.Ordinal).Count(),
            "And they measure one thing together, so they share a cell.");
    }

    /// <summary>
    ///     The freeze half of the length refusal. Expansion compared these numbers already, but the project's context
    ///     window is editable afterwards — and a probe truncated to a smaller window measures the window rather than
    ///     the model, which is a number that looks like a result and is not one.
    /// </summary>
    [Test]
    public async Task Start_WithAProbeLongerThanTheProjectWindow_IsRefusedNamingBothNumbers()
    {
        var harness = new FreezeHarness(probeContextTokens: [2048, 32768]);

        var failure = await AssertEx.ThrowsAsync<BenchmarkValidationException>(() => harness.StartAsync());

        AssertEx.Contains(failure.Message, "32768", message: "The refusal names the probe that does not fit.");
        AssertEx.Contains(failure.Message, "4096", message: "And the window it does not fit in.");
        AssertEx.Equal(0, harness.StoreCalls, "Nothing is frozen when a probe cannot be measured.");
    }

    /// <summary>
    ///     Several frozen plans go in as ONE insert. A caller that must validate two models before queuing either —
    ///     the training comparison hand-off — froze and committed one side at a time, so a second side that then
    ///     failed left the first queued with the caller holding an error and no ids.
    /// </summary>
    [Test]
    public async Task CommitAsync_WithSeveralPlans_InsertsThemAllInOneCall()
    {
        var harness = new FreezeHarness();
        await using var scope = new BenchmarkFreezeScope();

        var first = await harness.FreezeAsync(scope);
        var second = await harness.FreezeAsync(scope);

        // A freeze writes nothing, which is why both plans present the SAME project version.
        AssertEx.Equal(0, harness.StoreCalls, "Freezing decides; it does not queue.");
        AssertEx.Equal(first.ExpectedProjectVersion, second.ExpectedProjectVersion);

        var committed = await harness.CommitAsync([first, second]);

        AssertEx.Equal(1, harness.StoreCalls, "One insert, one compare-and-swap, all or nothing.");
        AssertEx.Equal(expected: 2, harness.Commands.Count);
        AssertEx.Equal(first.ExpectedProjectVersion, harness.ExpectedVersions.Single());

        // Split back per plan, so each side learns its OWN run ids out of one flat insert.
        AssertEx.Equal(expected: 2, committed.Count);
        AssertEx.Equal(expected: 1, committed[0].Count);
        AssertEx.Equal(expected: 1, committed[1].Count);
    }

    /// <summary>
    ///     One freeze wired end to end. Everything but the KV decision is held constant so a matrix test reads as the
    ///     single input it varies.
    /// </summary>
    private sealed class FreezeHarness
    {
        private readonly string _primaryModel;
        private readonly BenchmarkProjectRecord _project;
        private readonly BenchmarkRunFreezeService _service;

        public FreezeHarness(string primaryModel = "a-primary.gguf",
            string? judgeModel = null,
            GpuVariant variant = GpuVariant.Cpu,
            GpuVariant? inspectedVariant = null,
            bool inspectionFails = false,
            bool probeSucceeded = true,
            bool supportsQuantizedKv = true,
            bool optimizedConfigDisabled = false,
            Func<int, ResolvedLaunchArguments>? profile = null,
            int? maxOutputTokens = null,
            int? invocationTimeoutSeconds = null,
            int? reasoningBudgetTokens = null,
            bool supportsThinking = false,
            bool unverifiableModel = false,
            IReadOnlyList<string>? itemPrompts = null,
            string? taskItemSetHash = null,
            IReadOnlyList<int>? probeContextTokens = null)
        {
            _primaryModel = primaryModel;
            AgentId = Guid.NewGuid();
            _project = Project(Guid.NewGuid(), AgentId, judgeModel is not null, judgeModel, maxOutputTokens, invocationTimeoutSeconds,
                reasoningBudgetTokens, taskItemSetHash);
            TaskItems = probeContextTokens is null
                ? [.. (itemPrompts ?? ["exact task"]).Select((prompt, index) => Item(_project.Id, index, prompt))]
                : [.. probeContextTokens.Select((contextTokens, index) => ProbeCase(_project.Id, index, contextTokens))];
            var store = Substitute.For<IBenchmarkStore>();
            store.GetProjectAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(_project);
            store.ListTaskItemsAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(TaskItems);
            // Only reached by a project frozen before task items existed; a test that sees it called has found the
            // freeze writing on a path that should only read.
            store.GetOrCreateItemsAsync(_project.Id, Arg.Any<CancellationToken>()).Returns(TaskItems);
            // ONE store call per freeze, however many repeats: the group is inserted atomically, so a mid-group
            // conflict can no longer leave orphan runs queued behind an exception the caller reads as "nothing started".
            store.StartRunsAsync(Arg.Do<IReadOnlyList<BenchmarkStartRunCommand>>(batch =>
                     {
                         StoreCalls++;
                         Command = batch[^1];
                         Commands.AddRange(batch);
                     }),
                     Arg.Do<long>(version => ExpectedVersions.Add(version)),
                     Arg.Any<CancellationToken>())
                 .Returns(call => (IReadOnlyList<BenchmarkRunRecord>)[.. call.Arg<IReadOnlyList<BenchmarkStartRunCommand>>().Select(Run)]);

            var definitions = Substitute.For<IAgentDefinitionStore>();
            definitions.GetByIdAsync(AgentId, Arg.Any<CancellationToken>()).Returns(Definition(AgentId));
            Resolver = Substitute.For<IAgentDefinitionResolver>();
            // The task text is the RETRIEVAL QUERY, so it differs per item; the resolution itself is held constant.
            Resolver.ResolveAsync(AgentId, Arg.Any<string>(), Arg.Any<string>(), true, false, false, Arg.Any<CancellationToken>())
                    .Returns(Runtime(AgentId));
            var capabilities = Substitute.For<IGgufModelCapabilityResolver>();
            // ReasoningBudgetEnforceable is left at its own default (true) on purpose: it is the inert answer for a
            // model nothing was detected about, and freezing it ALONE is what claimed enforceability for a model that
            // does not reason at all.
            capabilities.TryResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(new GgufModelCapabilities(supportsThinking, true, false));

            var models = new Dictionary<string, InstalledModelSnapshot>(StringComparer.OrdinalIgnoreCase)
            {
                [primaryModel] = CreateInstalledModel(primaryModel)
            };
            if (judgeModel is not null)
            {
                models[judgeModel] = CreateInstalledModel(judgeModel);
            }

            LeaseProvider = new RecordingLeaseProvider(models, unverifiableModel);
            var dependencies = Substitute.For<IBenchmarkFreezeDependencyService>();
            dependencies.CaptureAsync(Arg.Any<Guid>(), Arg.Any<ResolvedAgentRuntime>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                        .Returns(Dependencies("initial"));
            var snapshots = Substitute.For<IBenchmarkRuntimeSnapshotFactory>();
            snapshots.Create(Arg.Do<BenchmarkRuntimeSnapshotInput>(input =>
                     {
                         SnapshotInput = input;
                         SnapshotInputs.Add(input);
                         SnapshotsCreated++;
                     }))
                     .Returns(call => CreateRuntimeSnapshot(call.Arg<BenchmarkRuntimeSnapshotInput>()));
            snapshots.Serialize(Arg.Any<BenchmarkRuntimeSnapshotV1>()).Returns([1, 2, 3]);

            LaunchInspector = Inspector(inspectedVariant ?? variant, probeSucceeded, supportsQuantizedKv, inspectionFails);
            _service = new BenchmarkRunFreezeService(store,
                definitions,
                Resolver,
                capabilities,
                LeaseProvider,
                new BenchmarkEligibilityPolicy(),
                dependencies,
                snapshots,
                new BenchmarkPhaseLaunchResolver(Profiles(profile),
                    Variants(variant),
                    LaunchInspector,
                    FallbackStore(optimizedConfigDisabled),
                    LaunchPolicy(),
                    new LlamaServerLaunchPolicyOptions()),
                TimeProvider.System,
                NullLogger<BenchmarkRunFreezeService>.Instance);
        }

        public Guid AgentId { get; }

        /// <summary>The project's leaf items, in index order — what the freeze fans out over.</summary>
        public IReadOnlyList<BenchmarkTaskItemRecord> TaskItems { get; }

        public IAgentDefinitionResolver Resolver { get; }
        public RecordingLeaseProvider LeaseProvider { get; }

        /// <summary>The llama-server probe, so a test can count how many times a request actually inspected it.</summary>
        public ILlamaServerLaunchCapabilityInspector LaunchInspector { get; }

        public BenchmarkStartRunCommand? Command { get; private set; }

        /// <summary>Every insert in order — a repeat group is several, and their ORDER is the contract.</summary>
        public List<BenchmarkStartRunCommand> Commands { get; } = [];

        /// <summary>How many times the store was asked to insert. A repeat group must be exactly one.</summary>
        public int StoreCalls { get; private set; }

        /// <summary>The expected project version each insert presented — one CAS, the caller's own.</summary>
        public List<long> ExpectedVersions { get; } = [];

        public int SnapshotsCreated { get; private set; }
        public BenchmarkRuntimeSnapshotInput? SnapshotInput { get; private set; }

        /// <summary>Every snapshot the freeze created, in order — an answer-variance group is several.</summary>
        public List<BenchmarkRuntimeSnapshotInput> SnapshotInputs { get; } = [];

        public async Task<BenchmarkRunRecord> StartAsync(string? kvCacheType = null) =>
            (await _service.StartAsync(new BenchmarkRunStartRequest(_project.Id, _primaryModel, _project.Version, kvCacheType))
                           .ConfigureAwait(false))[0];

        public Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(int repeatCount, bool warmup) =>
            _service.StartAsync(new BenchmarkRunStartRequest(_project.Id, _primaryModel, _project.Version, KvCacheType: null, repeatCount, warmup));

        public Task<BenchmarkFrozenRunPlan> FreezeAsync(BenchmarkFreezeScope scope) =>
            _service.FreezeAsync(new BenchmarkRunStartRequest(_project.Id, _primaryModel, _project.Version), scope);

        public Task<IReadOnlyList<IReadOnlyList<BenchmarkRunRecord>>> CommitAsync(IReadOnlyList<BenchmarkFrozenRunPlan> plans) =>
            _service.CommitAsync(plans);

        public Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(BenchmarkFreezeScope scope) =>
            _service.StartAsync(new BenchmarkRunStartRequest(_project.Id, _primaryModel, _project.Version), scope);

        public Task<IReadOnlyList<BenchmarkRunRecord>> StartAsync(int repeatCount, BenchmarkRepeatMode mode, double? temperature = null) =>
            _service.StartAsync(new BenchmarkRunStartRequest(_project.Id, _primaryModel, _project.Version, KvCacheType: null, repeatCount,
                Warmup: false, mode, temperature));

        private static BenchmarkProjectRecord Project(Guid id,
            Guid agentId,
            bool judgeEnabled,
            string? judgeModel,
            int? maxOutputTokens,
            int? invocationTimeoutSeconds,
            int? reasoningBudgetTokens,
            string? taskItemSetHash)
        {
            _ = judgeModel;
            return new BenchmarkProjectRecord(id, "Benchmark", JsonSerializer.SerializeToUtf8Bytes("exact task"), 4096, agentId,
                judgeEnabled, judgeEnabled ? Guid.NewGuid() : null, IsFrozen: false, 7, 1, 1, maxOutputTokens, invocationTimeoutSeconds,
                reasoningBudgetTokens, TaskItemSetHash: taskItemSetHash);
        }

        /// <summary>
        ///     One generated long-context case, carrying its own parameters exactly as the expansion writes them —
        ///     which is what lets the freeze re-check its length without parsing a haystack back out of the prompt.
        /// </summary>
        private static BenchmarkTaskItemRecord ProbeCase(Guid projectId, int index, int contextTokens) =>
            new(Guid.NewGuid(), projectId, Guid.NewGuid(), index, BenchmarkTaskItemKinds.NiahCase, 1, "v1:case-" + index, false,
                JsonSerializer.SerializeToUtf8Bytes("haystack " + index),
                null,
                null,
                JsonSerializer.SerializeToUtf8Bytes(new BenchmarkNiahCaseV1(contextTokens, 50, contextTokens - 100, 0,
                    $"NIAH ~{contextTokens} @ 50%", "Lisbon", "wikitext2-raw-test@abc"), BenchmarkNiahGenerator.SerializerOptions),
                1, 1, 1);

        /// <summary>One leaf item, with the prompt encoded exactly as the item store encodes it.</summary>
        private static BenchmarkTaskItemRecord Item(Guid projectId, int index, string prompt) =>
            new(Guid.NewGuid(), projectId, null, index, BenchmarkTaskItemKinds.Prompt, 1, "v1:item-" + index, true,
                JsonSerializer.SerializeToUtf8Bytes(prompt), null, null, null, 1, 1, 1);

        private static AgentDefinitionRecord Definition(Guid id) =>
            new(id, "Agent", null, "instructions", null, null, AgentDefinitionKind.Single, [], new Dictionary<string, bool>(), null, 3, 1, 1);

        private static ResolvedAgentRuntime Runtime(Guid id) =>
            new("prompt", [], null, null, 3, id, "Agent", Kind: AgentDefinitionKind.Single);

        private static InstalledModelSnapshot CreateInstalledModel(string name)
        {
            var v1 = "v1:" + new string('a', 64);
            return new InstalledModelSnapshot(name,
                v1,
                [],
                v1,
                [
                    new InstalledModelPhysicalMember(name, InstalledModelPhysicalMemberRole.Weight, 12, new string('b', 64),
                        "sha256:" + new string('b', 64) + ":12", [name], true, null)
                ],
                v1,
                LocalModelOrigin.Imported,
                "llamacpp",
                "map-revision",
                "repo/model",
                "revision",
                "Q4_K_M",
                GgufRole.Chat,
                v1);
        }

        private static BenchmarkFreezeDependencySetV1 Dependencies(string value) =>
            new(value, value, value, value, value, value);

        private static BenchmarkRuntimeSnapshotV1 CreateRuntimeSnapshot(BenchmarkRuntimeSnapshotInput input) =>
            new(1, input.ProjectId, input.AgentDefinitionId, input.AgentVersion, input.CoreTask, input.RequestedContextTokens,
                input.ResolvedRuntime, input.PrimaryRuntime, input.PrimarySampling, input.PrimaryModel, input.Dependencies,
                input.ApplicationVersion, input.CreatedAtUtc, "hash");

        private static IInferenceProfileResolver Profiles(Func<int, ResolvedLaunchArguments>? profile)
        {
            var profiles = Substitute.For<IInferenceProfileResolver>();
            profiles.ResolveAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        var context = call.ArgAt<string>(0).StartsWith("z-", StringComparison.Ordinal) ? 2048 : 4096;
                        return profile is null ? ResolvedLaunchArguments.Replay(context) : profile(context);
                    });
            return profiles;
        }

        private static IGpuVariantSelector Variants(GpuVariant variant)
        {
            var variants = Substitute.For<IGpuVariantSelector>();
            variants.SelectVariantAsync(Arg.Any<CancellationToken>()).Returns(variant);
            return variants;
        }

        private static ILlamaServerLaunchCapabilityInspector Inspector(GpuVariant variant,
            bool probeSucceeded,
            bool supportsQuantizedKv,
            bool inspectionFails)
        {
            if (inspectionFails)
            {
                var unavailable = Substitute.For<ILlamaServerLaunchCapabilityInspector>();
                unavailable.InspectAsync(Arg.Any<CancellationToken>())
                           .Returns<LlamaServerLaunchCapabilities>(_ => throw new LlamaRuntimeException("The llama.cpp runtime is not installed."));
                return unavailable;
            }

            var cacheTypes = supportsQuantizedKv
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    BenchmarkKvCacheType.F16,
                    BenchmarkKvCacheType.Q8_0,
                    BenchmarkKvCacheType.Q4_0
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    BenchmarkKvCacheType.F16
                };
            var flashAttention = supportsQuantizedKv
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    LlamaServerLaunchProjection.FlashAttentionAuto,
                    LlamaServerLaunchProjection.FlashAttentionOn
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    LlamaServerLaunchProjection.FlashAttentionAuto
                };
            var inspector = Substitute.For<ILlamaServerLaunchCapabilityInspector>();
            inspector.InspectAsync(Arg.Any<CancellationToken>())
                     .Returns(new LlamaServerLaunchCapabilities(variant, probeSucceeded, "b10201", "manifest-sha", cacheTypes, cacheTypes, flashAttention));
            return inspector;
        }

        private static ILlamaServerLaunchFallbackStore FallbackStore(bool disabled)
        {
            var store = Substitute.For<ILlamaServerLaunchFallbackStore>();
            store.IsOptimizedConfigDisabledAsync(Arg.Any<GpuVariant>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(disabled);
            return store;
        }

        private static ILlamaServerLaunchPolicy LaunchPolicy()
        {
            var policy = Substitute.For<ILlamaServerLaunchPolicy>();
            policy.ResolveCpuReplayPlan(Arg.Any<ResolvedLaunchArguments>())
                  .Returns(call => new LlamaServerLaunchPlan(call.Arg<ResolvedLaunchArguments>().CtxSize, false, BenchmarkKvCacheType.Q8_0, 8, 8));
            return policy;
        }

        private static BenchmarkRunRecord Run(BenchmarkStartRunCommand command) =>
            new(command.RunId, command.ProjectId, command.RuntimeSnapshotJson, command.PrimaryModelName, command.PrimaryModelOrigin,
                command.ModelContentFingerprint, command.AgentName, command.AgentVersion, command.RequestedContextTokens,
                BenchmarkPrimaryStatus.Queued, null, null, null, null, null, 0, null, null, 1, 1, null, null, 1);
    }

    private sealed class RecordingLeaseProvider(IReadOnlyDictionary<string, InstalledModelSnapshot> snapshots, bool unverifiable = false)
        : IBenchmarkInstalledModelLeaseProvider
    {
        public List<string> Acquired { get; } = [];
        public List<RecordingLease> Leases { get; } = [];

        public Task<IBenchmarkInstalledModelLease> AcquireAsync(string modelName, CancellationToken cancellationToken)
        {
            Acquired.Add(modelName);
            if (unverifiable)
            {
                throw new InstalledGgufSnapshotException("InstalledModelMemberFingerprintMismatch",
                    "The installed model weight no longer matches its registry value.");
            }

            var lease = new RecordingLease(snapshots[modelName]);
            Leases.Add(lease);
            return Task.FromResult<IBenchmarkInstalledModelLease>(lease);
        }
    }

    private sealed class RecordingLease(InstalledModelSnapshot snapshot) : IBenchmarkInstalledModelLease
    {
        public InstalledModelSnapshot Snapshot { get; } = snapshot;
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
