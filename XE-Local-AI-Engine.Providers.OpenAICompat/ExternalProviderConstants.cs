namespace XE_Local_AI_Engine.Providers.OpenAICompat;

using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions;

public static class ExternalProviderConstants
{
    /// <summary>
    ///     Stable provider key used across persisted model selections, the per-model→provider map, and capability
    ///     payloads. Must match <see cref="ILocalModelProvider.ProviderName" /> of the external provider.
    ///     <para>
    ///         ONE key for every connection: the provider is a multiplexer, not a provider-per-connection registration,
    ///         so the resolver's ctor-snapshotted provider set never has to change when the operator adds a connection.
    ///         The connection is recovered from the model id itself (<c>ext:{connectionId}/{wireId}</c>).
    ///     </para>
    /// </summary>
    public const string ProviderName = "external";

    /// <summary>
    ///     In-process marker key under which the turn's SELECTED reasoning effort travels on
    ///     <see cref="ChatOptions.AdditionalProperties" />, in the canonical lowercase vocabulary
    ///     (<c>none</c>/<c>on</c>/<c>minimal</c>/<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>).
    ///     <para>
    ///         WHY a marker rather than a typed option at the call site: the effort is resolved far upstream, in the
    ///         application layer's reasoning resolver, which cannot reference this provider — the same reason the
    ///         llama.cpp reasoning budget travels as <c>xe.llama.reasoning_budget_tokens</c>. The marker is in-process
    ///         only; MEAI's OpenAI adapter drops unmapped additional properties, so it can never reach the wire by
    ///         accident. When it is absent the registered model's declared default effort applies instead.
    ///     </para>
    /// </summary>
    public const string ReasoningEffortMarkerKey = "xe.external.reasoning_effort";
}
