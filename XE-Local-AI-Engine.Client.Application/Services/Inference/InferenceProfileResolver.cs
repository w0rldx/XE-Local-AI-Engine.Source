namespace XE_Local_AI_Engine.Client.Services.Inference;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     DB-backed <see cref="IInferenceProfileResolver" />: turns a persisted profile into the launch args for a cold
///     spawn — <see cref="ResolvedLaunchArguments.Explore" /> until a profile is frozen, otherwise the frozen
///     profile's replay args. Registered LAST so it wins over the supervisor's explore-only default.
/// </summary>
/// <remarks>
///     A <see cref="InferenceProfileStatus.Frozen" /> profile is re-validated through
///     <see cref="IInferenceInvalidationEvaluator" /> first and demoted to <see cref="InferenceProfileStatus.Stale" />
///     when its baseline no longer holds. Singleton on the cold spawn path: <see cref="IInferenceProfileStore" /> is
///     SCOPED, so the resolver resolves it through a fresh <see cref="IServiceScopeFactory" /> scope per call. This
///     path must NEVER throw — a corrupt persisted arg combo degrades to explore (auto-fit), never an exception.
/// </remarks>
public sealed class InferenceProfileResolver : IInferenceProfileResolver
{
    private readonly IInferenceInvalidationEvaluator _invalidationEvaluator;
    private readonly ILogger<InferenceProfileResolver> _logger;
    private readonly IMachineKeyProvider _machineKeyProvider;
    private readonly IServiceScopeFactory _scopeFactory;

    public InferenceProfileResolver(IServiceScopeFactory scopeFactory,
        IMachineKeyProvider machineKeyProvider,
        IInferenceInvalidationEvaluator invalidationEvaluator,
        ILogger<InferenceProfileResolver> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _machineKeyProvider = machineKeyProvider ?? throw new ArgumentNullException(nameof(machineKeyProvider));
        _invalidationEvaluator = invalidationEvaluator ?? throw new ArgumentNullException(nameof(invalidationEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ResolvedLaunchArguments> ResolveAsync(string modelName, ModelRole role, GpuVariant backend, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // CPU tuning is out of scope: always let llama.cpp auto-fit drive a CPU spawn.
        if (backend == GpuVariant.Cpu)
        {
            return ResolvedLaunchArguments.Explore();
        }

        var machineKey = await _machineKeyProvider.GetMachineKeyAsync(ct);
        var backendToken = InferenceBackends.FromVariant(backend);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInferenceProfileStore>();

        var record = await store.GetByKeyAsync(machineKey, modelName, (int)role, backendToken, ct);
        if (record is null)
        {
            return ResolvedLaunchArguments.Explore();
        }

        switch (record.Status)
        {
            case InferenceProfileStatus.Explored:
                // Explored rows are optimizer drafts, not serving policy: normal serving keeps auto-fit active until the
                // operator freezes a benchmark-justified profile. BenchmarkAsync supplies its own draft replay args, bypassing this.
                return ResolvedLaunchArguments.Explore();

            case InferenceProfileStatus.Frozen:
                if (await _invalidationEvaluator.IsStaleAsync(record, ct))
                {
                    await store.MarkStaleAsync(record.Id, ct);
                    return ResolvedLaunchArguments.Explore();
                }

                return BuildReplayOrExplore(record);

            default:
                // Stale (or any future status): re-exploration is the only path back to auto-fit.
                return ResolvedLaunchArguments.Explore();
        }
    }

    // Replay enforces the KV/flash-attn invariants and throws on a corrupt persisted combo. On the cold spawn hot path a
    // bad row must degrade to explore, never throw — so ANY failure here falls back to auto-fit (cancellation excepted).
    private ResolvedLaunchArguments BuildReplayOrExplore(InferenceProfileRecord record)
    {
        try
        {
            return ResolvedLaunchArguments.Replay(record.CtxSize,
                record.NGpuLayers,
                record.TensorSplit,
                record.OverrideTensor,
                record.KvTypeK,
                record.KvTypeV,
                record.FlashAttn);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Persisted inference profile {ProfileId} produced invalid replay arguments; falling back to explore.", record.Id);
            return ResolvedLaunchArguments.Explore();
        }
    }
}
