namespace XE_Local_AI_Engine.Client.Services.Models;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     The model picker's whole catalog, gathered from five independent sources that each degrade on their own.
///     Everything here is raw material for the list mapper — no source failure ever fails the catalog.
/// </summary>
/// <param name="OllamaModels">
///     The Ollama runtime's models, or <see langword="null" /> when that runtime could not be reached. Null is the
///     ONLY unavailability signal: an empty list means a reachable runtime with nothing installed.
/// </param>
/// <param name="Classifications">
///     Effective kind per Ollama model name. Empty when <paramref name="OllamaModels" /> is null — there is nothing
///     to classify, and classification probes the same unreachable runtime.
/// </param>
/// <param name="HasUsableCodexSession">
///     Whether a stored Codex session exists whose access token is non-expired (skew-adjusted) — the same gate
///     <c>cloud/codex/status</c> uses. Codex entries are offered only then.
/// </param>
/// <param name="AzureFoundryConnection">
///     The stored Azure Foundry connection, if any. Unlike Codex this does not gate on a live session: a saved
///     connection's deployments are always offered (routing stays selected-model-driven).
/// </param>
/// <param name="ExternalModels">
///     Every model registered on an operator-configured external OpenAI-compatible connection, key-free. Like the
///     Azure deployments these are offered on the strength of the registration alone: reachability is the health
///     surface's job, and a picker that hid a model whenever its endpoint was briefly down would be unusable.
/// </param>
public sealed record LocalModelCatalog(
    string? SelectedModelName,
    string? ConfiguredDefaultModelName,
    IReadOnlyList<Model>? OllamaModels,
    IReadOnlyDictionary<string, ModelClassificationResult> Classifications,
    IReadOnlyList<LocalModelDescriptor> InstalledGgufModels,
    bool HasUsableCodexSession,
    StoredAzureFoundryConnection? AzureFoundryConnection,
    IReadOnlyList<ExternalProviderModelRegistration> ExternalModels);

/// <summary>
///     Aggregates the model picker's five independent sources (Ollama, installed GGUF, Codex, Azure Foundry, external
///     OpenAI-compatible connections) and owns the per-source degradation policy, so the list endpoint stays a single
///     call plus a mapping.
/// </summary>
public interface ILocalModelCatalogService
{
    /// <summary>
    ///     Reads every source. Never throws for a source-level failure — only cancellation propagates.
    /// </summary>
    Task<LocalModelCatalog> GetCatalogAsync(CancellationToken cancellationToken = default);
}
