namespace XE_Local_AI_Engine.Client.Configuration;

public sealed class CloudProviderOptions
{
    public const string SectionName = "CloudProvider";
    public const string ProviderNone = "None";
    public const string ProviderAzureFoundry = "AzureFoundry";

    /// <summary>
    ///     The Codex (OpenAI ChatGPT subscription) OAuth provider. Unlike Azure, it carries no endpoint / API key /
    ///     deployment in this options object — the OAuth session lives in the encrypted Codex token store.
    /// </summary>
    public const string ProviderCodexOAuth = "CodexOAuth";

    public string ProviderName { get; set; } = ProviderNone;

    public string? AzureEndpoint { get; set; }

    public string? AzureApiKey { get; set; }

    public string? AzureDeploymentName { get; set; }
}
