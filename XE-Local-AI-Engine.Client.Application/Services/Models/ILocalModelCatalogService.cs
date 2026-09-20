namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     The model picker's whole catalog, gathered from five independent sources that each degrade on their own.
///     Everything here is raw material for the list mapper — no source failure ever fails the catalog.
/// </summary>
public sealed class LocalModelCatalog
{
    public required string? SelectedModelName { get; init; }

    public required string? ConfiguredDefaultModelName { get; init; }

    /// <summary>
    ///     The Ollama runtime's models, or <see langword="null" /> when that runtime could not be reached. Null is the
    ///     ONLY unavailability signal: an empty list means a reachable runtime with nothing installed.
    /// </summary>
    public required IReadOnlyList<OllamaModelSummary>? OllamaModels { get; init; }

    /// <summary>
    ///     Effective kind per Ollama model name. Empty when <see cref="OllamaModels" /> is null — there is nothing
    ///     to classify, and classification probes the same unreachable runtime.
    /// </summary>
    public required IReadOnlyDictionary<string, ModelClassificationResult> Classifications { get; init; }

    public required IReadOnlyList<LocalModelDescriptor> InstalledGgufModels { get; init; }

    /// <summary>
    ///     Whether a stored Codex session exists whose access token is non-expired (skew-adjusted) — the same gate
    ///     <c>cloud/codex/status</c> uses. Codex entries are offered only then.
    /// </summary>
    public required bool HasUsableCodexSession { get; init; }

    /// <summary>
    ///     The stored Azure Foundry connection, if any. Unlike Codex this does not gate on a live session: a saved
    ///     connection's deployments are always offered (routing stays selected-model-driven).
    /// </summary>
    public required StoredAzureFoundryConnection? AzureFoundryConnection { get; init; }

    /// <summary>Every model registered on an operator-configured external OpenAI-compatible connection, key-free.</summary>
    /// <remarks>
    ///     Like the Azure deployments these are offered on the strength of the registration alone: reachability is the health surface's
    ///     job, and a picker that hid a model whenever its endpoint was briefly down would be unusable.
    /// </remarks>
    public required IReadOnlyList<ExternalProviderModelRegistration> ExternalModels { get; init; }
}

/// <summary>
///     Aggregates the model picker's five independent sources (Ollama, installed GGUF, Codex, Azure Foundry, external
///     OpenAI-compatible connections) and owns the per-source degradation policy.
/// </summary>
/// <remarks>The list endpoint therefore stays a single call plus a mapping.</remarks>
public interface ILocalModelCatalogService
{
    /// <summary>
    ///     Reads every source. Never throws for a source-level failure — only cancellation propagates.
    /// </summary>
    Task<LocalModelCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);

    /// <summary>Lists the models the Ollama runtime currently holds in memory.</summary>
    /// <remarks>
    ///     Delegates straight to the runtime service this catalog already depends on, so the loaded-models endpoint reaches it through an
    ///     Application-owned seam rather than injecting a concrete provider's contract.
    /// </remarks>
    Task<IReadOnlyList<RunningModelSnapshot>> ListRunningOllamaModelsAsync(CancellationToken cancellationToken = default);
}
