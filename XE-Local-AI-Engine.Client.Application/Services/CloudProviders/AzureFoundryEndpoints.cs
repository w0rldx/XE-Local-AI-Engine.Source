namespace XE_Local_AI_Engine.Client.Services.CloudProviders;

using System;
using System.Linq;

/// <summary>
///     Host-allowlist guard for Azure Foundry / Azure OpenAI endpoints.
/// </summary>
/// <remarks>
///     Shared by store validation and the chat-client factory so a managed-identity Entra token can never be
///     sent to an arbitrary operator-entered host (MEDIUM-4).
/// </remarks>
public static class AzureFoundryEndpoints
{
    private static readonly string[] AllowedHostSuffixes =
    [
        ".openai.azure.com",
        ".services.ai.azure.com",
        ".cognitiveservices.azure.com",
    ];

    /// <summary>
    ///     Returns true when the endpoint host ends with a known Azure suffix (case-insensitive).
    /// </summary>
    public static bool IsAllowedHost(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return AllowedHostSuffixes.Any(suffix =>
            endpoint.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
