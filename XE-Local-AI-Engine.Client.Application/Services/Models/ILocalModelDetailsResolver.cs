namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     Answers "which provider owns this model, and what does <em>details</em> mean for it" for the model-details
///     route. The five-way routing — Codex cloud id, external OpenAI-compatible id, Azure Foundry deployment, GGUF
///     served by llama.cpp, Ollama-served model — is a decision, so it lives here rather than in the endpoint, which
///     is left to map one <see cref="LocalModelDetailsResolution" /> onto the wire response.
///     <para>
///         The branch order is a PRIORITY, not an arbitrary sequence: the cloud/external ids are recognised by shape
///         before anything probes a local runtime, because probing the Ollama daemon's <c>/api/show</c> for an id it
///         has never heard of answers 500, and a cloud model simply has no local details to report.
///     </para>
/// </summary>
public interface ILocalModelDetailsResolver
{
    /// <summary>
    ///     Resolves the details for <paramref name="modelName" />, which must already be the DECODED canonical name
    ///     (see <c>ModelRouteName</c>) so "validated name == probed name" holds. Never throws for an unreachable
    ///     runtime — that degrades to <see cref="LocalModelDetailsResolution.NoLocalDetails" />.
    /// </summary>
    Task<LocalModelDetailsResolution> ResolveAsync(string modelName, CancellationToken cancellationToken = default);
}

/// <summary>The discriminated outcome of model-details resolution. Each case is one branch of the provider routing.</summary>
public abstract record LocalModelDetailsResolution
{
    /// <summary>
    ///     The model has no local details to report — a Codex cloud id, an Azure Foundry deployment, an external
    ///     registration that is gone, a GGUF with no installed descriptor, or an unreachable Ollama daemon. Every one
    ///     of those is a clean 404 rather than a fault.
    /// </summary>
    public sealed record NoLocalDetails : LocalModelDetailsResolution;

    /// <summary>An external OpenAI-compatible registration; the operator's declarations are the only detail source.</summary>
    public sealed record External(ExternalProviderModelRegistration Registration) : LocalModelDetailsResolution;

    /// <summary>
    ///     An installed GGUF served by llama.cpp. <paramref name="EffectiveContextTokens" /> is the running process's
    ///     launched window when one is warm, and <see langword="null" /> otherwise (including when the probe failed).
    /// </summary>
    public sealed record Gguf(LocalModelDescriptor Descriptor, int? EffectiveContextTokens) : LocalModelDetailsResolution;

    /// <summary>An Ollama-served model, as reported by the daemon's <c>/api/show</c>.</summary>
    public sealed record Ollama(OllamaModelDetails Details) : LocalModelDetailsResolution;
}
