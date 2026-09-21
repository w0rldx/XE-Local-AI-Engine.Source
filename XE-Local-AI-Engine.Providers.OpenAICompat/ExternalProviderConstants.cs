namespace XE_Local_AI_Engine.Providers.OpenAICompat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions;

public static class ExternalProviderConstants
{
    /// <summary>
    ///     Stable provider key used across persisted model selections, the per-model provider map and capability
    ///     payloads; must match <see cref="ILocalModelProvider.ProviderName" /> of the external provider.
    /// </summary>
    /// <remarks>
    ///     ONE key for every connection, because the provider is a multiplexer rather than a provider-per-connection
    ///     registration, so the resolver's ctor-snapshotted provider set never changes when the operator adds a
    ///     connection. The connection is recovered from the model id itself (<c>ext:{connectionId}/{wireId}</c>).
    /// </remarks>
    public const string ProviderName = "external";

    /// <summary>
    ///     In-process marker key under which the turn's SELECTED reasoning effort travels on
    ///     <see cref="ChatOptions.AdditionalProperties" />, in the canonical lowercase vocabulary.
    /// </summary>
    /// <remarks>
    ///     A marker rather than a typed option because the effort is resolved far upstream, in the application layer's
    ///     reasoning resolver, which cannot reference this provider — the same reason the llama.cpp reasoning budget
    ///     travels as <c>xe.llama.reasoning_budget_tokens</c>. It is in-process only, since MEAI's OpenAI adapter drops
    ///     unmapped additional properties, and its ABSENCE applies the registered model's declared default effort.
    /// </remarks>
    public const string ReasoningEffortMarkerKey = "xe.external.reasoning_effort";
}
