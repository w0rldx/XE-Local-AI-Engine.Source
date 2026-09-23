namespace XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>Boundary allow-list for the update channel.</summary>
/// <remarks>
///     The same predicate and the same message as <c>SaveNodeSettingsRequestValidator</c>, so the two surfaces that
///     may write the channel cannot drift apart.
/// </remarks>
public sealed class SetAppUpdateChannelRequestValidator : Validator<SetAppUpdateChannelRequest>
{
    public SetAppUpdateChannelRequestValidator()
    {
        RuleFor(static request => request.Channel)
            .NotEmpty()
            .Must(StoredNodeSettings.IsValidUpdateChannel)
            .WithMessage("Update channel must be stable, preview or development.");
    }
}
