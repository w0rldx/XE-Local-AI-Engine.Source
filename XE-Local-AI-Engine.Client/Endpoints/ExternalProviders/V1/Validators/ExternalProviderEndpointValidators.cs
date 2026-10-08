namespace XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.ExternalProviders.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Shape and requiredness, plus the custom header rules.
/// </summary>
/// <remarks>
///     Every other BOUND (caps, name lengths, timeout range, wire-id grammar, duplicates, reasoning-effort vocabulary,
///     context length) is enforced by the store at save time and surfaces as a 400 carrying its own message. The header
///     rules run here too because the probe sends headers without a save; both call <see cref="StoredExternalProviderHeader.FindViolations" />.
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

        RuleFor(static request => request.Headers)
            .Custom(static (headers, context) => AddHeaderViolations(headers, context.AddFailure));
    }

    /// <summary>Adds one failure per header rule violation; the messages name the header, never its value.</summary>
    internal static void AddHeaderViolations(IReadOnlyList<ExternalProviderHeaderRequest>? headers, Action<string> addFailure)
    {
        foreach (var violation in StoredExternalProviderHeader.FindViolations(headers.ToStoreHeaders()))
        {
            addFailure(violation);
        }
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

        RuleFor(static request => request.Headers)
            .Custom(static (headers, context) => SaveExternalProviderConnectionRequestValidator.AddHeaderViolations(headers, context.AddFailure));
    }
}
