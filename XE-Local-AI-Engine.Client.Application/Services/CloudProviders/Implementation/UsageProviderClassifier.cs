namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Pure mapping from the runtime's own provider names to the canonical usage-ledger provider labels
///     (<see cref="AgentUsageProviders" />). A resolved cloud selection wins (a turn that reached a cloud provider is
///     attributed there even if a local model of the same name exists); an external OpenAI-compatible model is
///     attributed to its own connection; otherwise the local runtime that serves the model decides <c>local</c>
///     (llama.cpp) vs <c>ollama</c>. Anything unrecognized — including a cloud name this build does not know and the
///     "no provider resolved" case — degrades to <see cref="AgentUsageProviders.Unknown" />. No I/O and no throwing, so
///     it is trivially unit-testable; the async resolution around it lives in <see cref="UsageProviderResolver" />.
/// </summary>
internal static class UsageProviderClassifier
{
    /// <summary>The usage-provider label prefix for an external connection: <c>external:{connectionId}</c>.</summary>
    /// <remarks>
    ///     One label per CONNECTION rather than one for all external turns, because the ledger's provider column is
    ///     what the usage view groups and rates by, and a single "external" bucket would merge a free self-hosted box
    ///     with a metered hosted API. It rides the existing one-string <c>IUsageProviderResolver</c> contract, so the
    ///     ledger schema is unchanged; the UI maps it back to a display name through the registry, falling back to a
    ///     plain "External" for a connection that has since been deleted.
    /// </remarks>
    public const string ExternalPrefix = "external:";

    /// <summary>
    ///     Classifies a turn whose model is an <c>ext:</c> id, or <see langword="null" /> when it is not one. Separate
    ///     from <see cref="Classify" /> because it reads the MODEL id rather than a resolved provider name: the
    ///     connection is part of the identity, and no provider-name string carries it.
    /// </summary>
    public static string? ClassifyExternal(string? modelName)
    {
        return ExternalModelId.TryParse(modelName, out var connectionId, out _) ? ExternalPrefix + connectionId : null;
    }

    public static string Classify(string? cloudProviderName, string? localProviderName)
    {
        if (!string.IsNullOrWhiteSpace(cloudProviderName))
        {
            // The cloud factory reports "codex"/"azure"; a name this build does not recognize degrades to unknown rather
            // than silently mislabelling the tokens.
            if (string.Equals(cloudProviderName, AgentUsageProviders.Codex, StringComparison.OrdinalIgnoreCase))
            {
                return AgentUsageProviders.Codex;
            }

            return string.Equals(cloudProviderName, AgentUsageProviders.Azure, StringComparison.OrdinalIgnoreCase)
                ? AgentUsageProviders.Azure
                : AgentUsageProviders.Unknown;
        }

        if (string.Equals(localProviderName, LlamaServerProviderConstants.ProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return AgentUsageProviders.Local;
        }

        return string.Equals(localProviderName, OllamaLocalModelProvider.OllamaProviderName, StringComparison.OrdinalIgnoreCase)
            ? AgentUsageProviders.Ollama
            : AgentUsageProviders.Unknown;
    }
}
