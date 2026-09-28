namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Default <see cref="ILlamaServerPooledLaunchAdmission" />: runs a cold embedder or reranker launch through
///     <see cref="ICapacityService" />.
/// </summary>
/// <remarks>
///     The capacity service is Scoped and depends on the supervisor that calls this hook, so it is resolved per call from a
///     fresh scope rather than injected. Disposing that scope cannot touch the returned reservation: it holds only the
///     singleton ledger and launch-admission registry. The embedder is booked but never refused on budget (ingestion depends
///     on it); the reranker is gated like any other model, and a refusal leaves search on fusion order.
/// </remarks>
public sealed class PooledRoleLaunchAdmission : ILlamaServerPooledLaunchAdmission
{
    private readonly IServiceScopeFactory _scopeFactory;

    public PooledRoleLaunchAdmission(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <inheritdoc />
    public async Task<IDisposable?> AdmitAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        if (role is not (ModelRole.Embedding or ModelRole.Reranker))
        {
            throw new ArgumentOutOfRangeException(nameof(role), role, "Only pooled roles are admitted by this hook.");
        }

        CapacityDecision decision;
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var capacity = scope.ServiceProvider.GetRequiredService<ICapacityService>();
            decision = await capacity.DecideAsync(new CapacityRequest
            {
                ModelName = modelName,
                Role = role,
                NeverRejectOnBudget = role == ModelRole.Embedding
            }, ct);
        }

        return decision.Verdict switch
        {
            CapacityVerdict.Allow => decision.Reservation,
            CapacityVerdict.QueueSameModel => null,
            _ => throw new LlamaRuntimeException(decision.Reason)
        };
    }
}
