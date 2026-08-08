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

    // The appsettings Azure* fields below are a legacy single-deployment, API-key-only seed superseded by the
    // DataProtection-encrypted, multi-model store (ICloudCredentialStore / StoredCloudProviderConfig). They are
    // validated by CloudProviderOptionsValidator but are not read by the runtime — the encrypted store is the source
    // of truth. No startup seam currently seeds the encrypted store from these values (follow-up: wire a first-run
    // seed if a seamless hosting hook is added).

    public string? AzureEndpoint { get; set; }

    public string? AzureApiKey { get; set; }

    public string? AzureDeploymentName { get; set; }
}
