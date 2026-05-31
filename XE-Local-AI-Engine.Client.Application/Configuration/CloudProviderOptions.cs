namespace XE_Local_AI_Engine.Client.Configuration;

/// <summary>
///     Configuration options for cloud provider behavior.
/// </summary>
public sealed class CloudProviderOptions
{
    public const string SectionName = "CloudProvider";
    public const string ProviderNone = "None";
    public const string ProviderAzureFoundry = "AzureFoundry";

    public string ProviderName { get; set; } = ProviderNone;

    public string? AzureEndpoint { get; set; }

    public string? AzureApiKey { get; set; }

    public string? AzureDeploymentName { get; set; }
}
