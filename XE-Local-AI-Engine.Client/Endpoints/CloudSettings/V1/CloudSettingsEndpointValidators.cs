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
            .WithMessage($"AuthMode must be '{nameof(AzureFoundryAuthMode.ApiKey)}', '{nameof(AzureFoundryAuthMode.ManagedIdentity)}', " +
                         $"or '{nameof(AzureFoundryAuthMode.EntraId)}'.");

        RuleFor(static request => request.ApiSurface)
            .Must(static apiSurface => Enum.TryParse<AzureFoundryApiSurface>(apiSurface?.Trim(), ignoreCase: true, out _))
            .WithMessage($"ApiSurface must be '{nameof(AzureFoundryApiSurface.AzureDeployments)}' or '{nameof(AzureFoundryApiSurface.OpenAiV1)}'.");

        // API key is required only for ApiKey mode; managed identity / Entra ID ignore any supplied key (the mapper drops it).
        RuleFor(static request => request.ApiKey)
            .NotEmpty()
            .WithMessage($"ApiKey is required when AuthMode is '{nameof(AzureFoundryAuthMode.ApiKey)}'.")
            .When(static request => IsApiKeyMode(request.AuthMode));

        // Entra ID requires a tenant, client id, and token scope regardless of sign-in shape (Locked build contract
        // §8) — the client secret is optional, and its absence selects interactive user sign-in.
        RuleFor(static request => request.EntraTenantId)
            .NotEmpty()
            .WithMessage($"EntraTenantId is required when AuthMode is '{nameof(AzureFoundryAuthMode.EntraId)}'.")
            .When(static request => IsEntraIdMode(request.AuthMode));

        RuleFor(static request => request.EntraClientId)
            .NotEmpty()
            .WithMessage($"EntraClientId is required when AuthMode is '{nameof(AzureFoundryAuthMode.EntraId)}'.")
            .When(static request => IsEntraIdMode(request.AuthMode));

        RuleFor(static request => request.EntraTokenScope)
            .NotEmpty()
            .WithMessage($"EntraTokenScope is required when AuthMode is '{nameof(AzureFoundryAuthMode.EntraId)}'.")
            .When(static request => IsEntraIdMode(request.AuthMode));

        RuleFor(static request => request.Models)
            .Must(static models => models is not null && models.Any(static model => !string.IsNullOrWhiteSpace(model.DeploymentName)))
            .WithMessage("At least one model with a non-blank deployment name is required.")
            .Must(static models => models is null || models.All(static model => (model.DeploymentName?.Length ?? 0) <= 128))
            .WithMessage("Deployment names must be 128 characters or fewer.");
    }

    private static bool IsApiKeyMode(string? authMode)
    {
        // Treat anything that does not parse to ManagedIdentity or EntraId (including an unparseable value) as
        // ApiKey so the key requirement is enforced; the AuthMode rule above surfaces a separate error for an
        // unparseable value.
        return !(Enum.TryParse<AzureFoundryAuthMode>(authMode?.Trim(), ignoreCase: true, out var parsed)
                 && parsed is AzureFoundryAuthMode.ManagedIdentity or AzureFoundryAuthMode.EntraId);
    }

    private static bool IsEntraIdMode(string? authMode)
    {
        return Enum.TryParse<AzureFoundryAuthMode>(authMode?.Trim(), ignoreCase: true, out var parsed)
               && parsed == AzureFoundryAuthMode.EntraId;
    }
}
