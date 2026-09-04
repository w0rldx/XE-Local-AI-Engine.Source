namespace XE_Local_AI_Engine.Tests.Inference;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InferenceProfileResolver" /> tests: no profile and optimizer drafts explore; a
///     valid frozen row replays its frozen args without demotion; and a frozen-but-stale row is demoted to Stale and
///     re-explores. The scoped store is reached through a real <see cref="IServiceScopeFactory" /> over a substituted
///     store; the machine key + invalidation seams are mocked. No DB, no process.
/// </summary>
public sealed class InferenceProfileResolverTests
{
    private const string MachineKey = "machine-abc";
    private const string Model = "bartowski/Model-GGUF:Q4_K_M";

    [Test]
    public async Task Resolver_NoProfile_ReturnsExplore()
    {
        var store = Substitute.For<IInferenceProfileStore>();
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(null));
        var resolver = BuildResolver(store, out _);

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, CancellationToken.None);

        AssertEx.True(result.ExploreMode);
    }

    [Test]
    public async Task Resolver_ExploredStatus_ReturnsExplore()
    {
        var store = Substitute.For<IInferenceProfileStore>();
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(Record(InferenceProfileStatus.Explored,
                 ctxSize: 8192,
                 nGpuLayers: 33,
                 launchPolicyFingerprintVersion: 3,
                 launchPolicyFingerprint: "current-fingerprint")));
        var resolver = BuildResolver(store, out _);

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.True(result.ExploreMode);
    }

    [Test]
    public async Task Resolver_ExploredStatusWithoutLaunchPolicyFingerprint_ReturnsExplore()
    {
        var store = Substitute.For<IInferenceProfileStore>();
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(Record(InferenceProfileStatus.Explored,
                 ctxSize: 8192,
                 nGpuLayers: 33,
                 launchPolicyFingerprintVersion: null,
                 launchPolicyFingerprint: null)));
        var resolver = BuildResolver(store, out _);

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, CancellationToken.None);

        AssertEx.True(result.ExploreMode);
    }

    [Test]
    public async Task Resolver_ExploredGpuStatusWithoutPlacement_ReturnsExplore()
    {
        var store = Substitute.For<IInferenceProfileStore>();
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(Record(InferenceProfileStatus.Explored,
                 ctxSize: 8192,
                 nGpuLayers: null,
                 launchPolicyFingerprintVersion: 3,
                 launchPolicyFingerprint: "current-fingerprint")));
        var resolver = BuildResolver(store, out _);

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Vulkan, CancellationToken.None);

        AssertEx.True(result.ExploreMode);
    }

    [Test]
    public async Task Resolver_FrozenValid_ReplaysFrozenArgs()
    {
        var store = Substitute.For<IInferenceProfileStore>();
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(Record(InferenceProfileStatus.Frozen, ctxSize: 4096, nGpuLayers: 20)));
        var resolver = BuildResolver(store, out var invalidation);
        invalidation.IsStaleAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(false));

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, CancellationToken.None);

        AssertEx.False(result.ExploreMode);
        AssertEx.Equal(4096, result.CtxSize);
        AssertEx.Equal(20, result.NGpuLayers);
        await store.DidNotReceive().MarkStaleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Resolver_FrozenButStale_MarksStaleAndExplores()
    {
        var store = Substitute.For<IInferenceProfileStore>();
        var record = Record(InferenceProfileStatus.Frozen, ctxSize: 4096, nGpuLayers: 20);
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(record));
        var resolver = BuildResolver(store, out var invalidation);
        invalidation.IsStaleAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(true));

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, CancellationToken.None);

        AssertEx.True(result.ExploreMode);
        await store.Received(1).MarkStaleAsync(record.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("q8_0", "q4_0")]
    [Arguments("q4_0", "q8_0")]
    public async Task FrozenProfile_AfterKvCacheTypeChange_IsMarkedStaleAndReturnsExplore(string frozenKvType, string selectedKvType)
    {
        // D13's lifecycle, from the resolver's side and in BOTH directions: the KV-cache knob is one more thing axis (b)
        // covers, so the existing MarkStale -> Explore path carries it with no new axis, call site or column. The
        // following spawn is an ordinary auto-fit explore, which no profiling lease can refuse.
        var store = Substitute.For<IInferenceProfileStore>();
        var record = Record(InferenceProfileStatus.Frozen, ctxSize: 4096, nGpuLayers: 20) with
        {
            KvTypeK = frozenKvType,
            KvTypeV = frozenKvType,
            FlashAttn = true
        };
        store.GetByKeyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(Task.FromResult<InferenceProfileRecord?>(record));
        var resolver = BuildResolver(store, out var invalidation);

        // The launch-policy axis reports drift because the node now selects a different KV type than the row was frozen
        // under; LaunchPolicyFingerprintProviderTests owns the pin that the hash actually moves.
        invalidation.IsStaleAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(!string.Equals(frozenKvType, selectedKvType, StringComparison.Ordinal)));

        var result = await resolver.ResolveAsync(Model, ModelRole.Chat, GpuVariant.Cuda, CancellationToken.None);

        AssertEx.True(result.ExploreMode, "A staled profile must fall back to auto-fit under the newly selected KV type.");
        await store.Received(1).MarkStaleAsync(record.Id, Arg.Any<CancellationToken>());
    }

    private static InferenceProfileResolver BuildResolver(IInferenceProfileStore store, out IInferenceInvalidationEvaluator invalidation)
    {
        invalidation = Substitute.For<IInferenceInvalidationEvaluator>();
        invalidation.IsStaleAsync(Arg.Any<InferenceProfileRecord>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(false));

        var machineKeyProvider = Substitute.For<IMachineKeyProvider>();
        machineKeyProvider.GetMachineKeyAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(MachineKey));

        return new InferenceProfileResolver(BuildScopeFactory(store),
            machineKeyProvider,
            invalidation,
            NullLogger<InferenceProfileResolver>.Instance);
    }

    // A real scope factory whose every scope resolves the substituted store as the SCOPED IInferenceProfileStore.
    private static IServiceScopeFactory BuildScopeFactory(IInferenceProfileStore store)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static InferenceProfileRecord Record(InferenceProfileStatus status,
        int ctxSize,
        int? nGpuLayers,
        int? launchPolicyFingerprintVersion = 3,
        string? launchPolicyFingerprint = "current-fingerprint")
    {
        return new InferenceProfileRecord(Id: Guid.NewGuid(),
            MachineKey: MachineKey,
            ModelName: Model,
            Role: (int)ModelRole.Chat,
            Backend: "cuda",
            LlamacppBuild: "b9692",
            Quant: "Q4_K_M",
            CtxSize: ctxSize,
            NGpuLayers: nGpuLayers,
            TensorSplit: null,
            OverrideTensor: null,
            KvTypeK: null,
            KvTypeV: null,
            FlashAttn: false,
            NParams: 7_000_000_000,
            IsMoe: false,
            ExpertCount: null,
            GlobalFreeVramAtFreezeBytes: null,
            Status: status,
            BenchmarkSnapshotId: status == InferenceProfileStatus.Frozen ? Guid.NewGuid() : null,
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LaunchPolicyFingerprintVersion: launchPolicyFingerprintVersion,
            LaunchPolicyFingerprint: launchPolicyFingerprint);
    }
}
