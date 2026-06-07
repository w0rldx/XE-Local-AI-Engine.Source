namespace XE_Local_AI_Engine.Client.Configuration.Validation;

using Microsoft.Extensions.Options;

public sealed class CloudProviderOptionsValidator : IValidateOptions<CloudProviderOptions>
{
    public ValidateOptionsResult Validate(string? name, CloudProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var providerName = string.IsNullOrWhiteSpace(options.ProviderName)
            ? CloudProviderOptions.ProviderNone
            : options.ProviderName.Trim();
        var isAzureFoundry = string.Equals(providerName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase);

        var errors = Enumerable.Empty<string>()
                               .AppendIf(!IsKnownProvider(providerName),
                                   $"CloudProvider:ProviderName must be '{CloudProviderOptions.ProviderNone}', '{CloudProviderOptions.ProviderAzureFoundry}', or '{CloudProviderOptions.ProviderCodexOAuth}'.")
                               .AppendIf(isAzureFoundry && !IsHttpsAbsoluteUri(options.AzureEndpoint),
                                   "CloudProvider:AzureEndpoint must be an absolute HTTPS URL when ProviderName is AzureFoundry.")
                               .AppendIf(isAzureFoundry && string.IsNullOrWhiteSpace(options.AzureApiKey),
                                   "CloudProvider:AzureApiKey is required when ProviderName is AzureFoundry.")
                               .AppendIf(isAzureFoundry && string.IsNullOrWhiteSpace(options.AzureDeploymentName),
                                   "CloudProvider:AzureDeploymentName is required when ProviderName is AzureFoundry.")
                               .ToArray();

        return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsKnownProvider(string providerName)
    {
        return string.Equals(providerName, CloudProviderOptions.ProviderNone, StringComparison.OrdinalIgnoreCase)
               || string.Equals(providerName, CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase)
               || string.Equals(providerName, CloudProviderOptions.ProviderCodexOAuth, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHttpsAbsoluteUri(string? endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
               && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }
}
