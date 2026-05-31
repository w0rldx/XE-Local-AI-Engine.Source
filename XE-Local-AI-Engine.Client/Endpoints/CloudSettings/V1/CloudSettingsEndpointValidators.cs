namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Configuration;

/// <summary>
///     Startup/options validator for save cloud settings request settings.
/// </summary>
public sealed class SaveCloudSettingsRequestValidator : Validator<SaveCloudSettingsRequest>
{
    public SaveCloudSettingsRequestValidator()
    {
        RuleFor(static request => request.ProviderName)
            .Must(static providerName => string.Equals(providerName?.Trim(), CloudProviderOptions.ProviderAzureFoundry, StringComparison.OrdinalIgnoreCase))
            .WithMessage($"ProviderName must be '{CloudProviderOptions.ProviderAzureFoundry}'.");

        RuleFor(static request => request.Endpoint)
            .NotEmpty()
            .Must(static endpoint => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                                     && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            .WithMessage("Endpoint must be an absolute HTTPS URL.");

        RuleFor(static request => request.ApiKey)
            .NotEmpty()
            .WithMessage("ApiKey is required when saving cloud settings.");

        RuleFor(static request => request.DeploymentName)
            .NotEmpty()
            .MaximumLength(128);
    }
}
