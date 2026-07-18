namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;

/// <summary>
///     Pure mapping from the runtime's own provider names to the canonical usage-ledger provider labels
///     (<see cref="AgentUsageProviders" />). A resolved cloud selection wins (a turn that reached a cloud provider is
///     attributed there even if a local model of the same name exists); otherwise the local runtime that serves the model
///     decides <c>local</c> (llama.cpp) vs <c>ollama</c>. Anything unrecognized — including a cloud name this build does
///     not know and the "no provider resolved" case — degrades to <see cref="AgentUsageProviders.Unknown" />. No I/O and
///     no throwing, so it is trivially unit-testable; the async resolution around it lives in
///     <see cref="UsageProviderResolver" />.
/// </summary>
internal static class UsageProviderClassifier
{
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
