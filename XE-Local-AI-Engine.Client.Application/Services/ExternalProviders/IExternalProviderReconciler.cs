namespace XE_Local_AI_Engine.Client.Services.ExternalProviders;

/// <summary>
///     Brings the three stores an external-provider save touches back into agreement with the encrypted store, which is
///     the single source of truth.
/// </summary>
/// <remarks>
///     <para>
///         A save has to write the encrypted file, then a provider-map row per registered model, then the node's
///         tool-capable allow-list — three stores with no shared transaction. Rather than pretend that sequence is
///         atomic, the encrypted file commits FIRST and this idempotent pass repairs whatever the other two are missing
///         (or still carrying), the way the model-deletion coordinator's journal compensates its own partial failures.
///     </para>
///     <para>
///         It runs at startup and again after every committed save or delete, so a crash between two of the three
///         writes self-heals on the next boot rather than leaving an <c>ext:</c> id that routes nowhere or a deleted
///         model still sitting in the node's tool allow-list.
///     </para>
/// </remarks>
public interface IExternalProviderReconciler
{
    /// <summary>Runs one full reconciliation pass and reports what it repaired.</summary>
    Task<ExternalProviderReconciliationReport> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>What one reconciliation pass changed. All zeros is the healthy steady state.</summary>
/// <param name="MapRowsWritten">Provider-map rows inserted or re-pointed at the external provider.</param>
/// <param name="MapRowsRemoved">Orphaned <c>ext:</c> provider-map rows removed.</param>
/// <param name="AllowListAdded">Namespaced ids added to the tool-capable allow-list.</param>
/// <param name="AllowListRemoved">Namespaced ids removed from the tool-capable allow-list.</param>
/// <param name="DefaultModelCleared">Whether a dangling <c>ext:</c> default model name was cleared.</param>
public readonly record struct ExternalProviderReconciliationReport(
    int MapRowsWritten,
    int MapRowsRemoved,
    int AllowListAdded,
    int AllowListRemoved,
    bool DefaultModelCleared)
{
    /// <summary>True when the pass found drift and repaired it.</summary>
    public bool Changed => MapRowsWritten > 0 || MapRowsRemoved > 0 || AllowListAdded > 0 || AllowListRemoved > 0 || DefaultModelCleared;
}
