namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Shape and requiredness only.
/// </summary>
/// <remarks>
///     Every BOUND — the connection and model caps, the name lengths, the timeout range, the wire-id grammar, duplicate
///     rejection, the reasoning-effort vocabulary, a positive context length — is enforced by the encrypted store at
///     save time and surfaces as a 400 carrying the store's own message. Restating those rules here would produce two
///     places that must agree about what is storable, and the store is the one that decides.
/// </remarks>
public sealed class SaveExternalProviderConnectionRequestValidator : Validator<SaveExternalProviderConnectionRequest>
{
    public SaveExternalProviderConnectionRequestValidator()
    {
        RuleFor(static request => request.ConnectionId)
            .NotEmpty()
            .WithMessage("ConnectionId is required.");

        RuleFor(static request => request.DisplayName)
            .NotEmpty()
            .WithMessage("DisplayName is required.");

        RuleFor(static request => request.BaseUrl)
            .NotEmpty()
            .WithMessage("BaseUrl is required.");

        RuleFor(static request => request.Locality)
            .Must(static locality => Enum.TryParse<ExternalProviderLocality>(locality?.Trim(), ignoreCase: true, out _))
            .WithMessage($"Locality must be '{nameof(ExternalProviderLocality.Local)}' or '{nameof(ExternalProviderLocality.Cloud)}'.");

        RuleFor(static request => request.Models)
            .NotNull()
            .WithMessage("Models is required (send an empty list to register none).")
            .Must(static models => models is null || models.All(static model => !string.IsNullOrWhiteSpace(model.WireId)))
            .WithMessage("Every registered model needs a non-blank WireId.");
    }
}

/// <summary>
///     Requires the probe to name SOMETHING to probe. Everything else about the address is decided by the same
///     normalizer the save path uses, inside the probe service, so this validator deliberately does not second-guess it.
/// </summary>
public sealed class ExternalProviderProbeRequestValidator : Validator<ExternalProviderProbeRequest>
{
    public ExternalProviderProbeRequestValidator()
    {
        RuleFor(static request => request)
            .Must(static request => !string.IsNullOrWhiteSpace(request.ConnectionId) || !string.IsNullOrWhiteSpace(request.BaseUrl))
            .WithMessage("Either ConnectionId or BaseUrl is required.");
    }
}
