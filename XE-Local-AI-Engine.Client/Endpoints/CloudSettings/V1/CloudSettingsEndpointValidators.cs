namespace XE_Local_AI_Engine.Client.Endpoints.CloudSettings.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

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

        RuleFor(static request => request.AuthMode)
            .Must(static authMode => Enum.TryParse<AzureFoundryAuthMode>(authMode?.Trim(), ignoreCase: true, out _))
            .WithMessage($"AuthMode must be '{nameof(AzureFoundryAuthMode.ApiKey)}' or '{nameof(AzureFoundryAuthMode.ManagedIdentity)}'.");

        // API key is required only for ApiKey mode; managed identity ignores any supplied key (the mapper drops it).
        RuleFor(static request => request.ApiKey)
            .NotEmpty()
            .WithMessage($"ApiKey is required when AuthMode is '{nameof(AzureFoundryAuthMode.ApiKey)}'.")
            .When(static request => IsApiKeyMode(request.AuthMode));

        RuleFor(static request => request.Models)
            .Must(static models => models is not null && models.Any(static model => !string.IsNullOrWhiteSpace(model.DeploymentName)))
            .WithMessage("At least one model with a non-blank deployment name is required.")
            .Must(static models => models is null || models.All(static model => (model.DeploymentName?.Length ?? 0) <= 128))
            .WithMessage("Deployment names must be 128 characters or fewer.");
    }

    private static bool IsApiKeyMode(string? authMode)
    {
        // Treat anything that is not explicitly ManagedIdentity (including an unparseable value) as ApiKey so the key
        // requirement is enforced; the AuthMode rule above surfaces a separate error for an unparseable value.
        return !(Enum.TryParse<AzureFoundryAuthMode>(authMode?.Trim(), ignoreCase: true, out var parsed)
                 && parsed == AzureFoundryAuthMode.ManagedIdentity);
    }
}
