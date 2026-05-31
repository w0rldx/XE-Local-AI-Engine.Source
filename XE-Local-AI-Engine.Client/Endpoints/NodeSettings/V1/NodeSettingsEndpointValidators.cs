namespace XE_Local_AI_Engine.Client.Endpoints.NodeSettings.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Startup/options validator for save node settings request settings.
/// </summary>
public sealed class SaveNodeSettingsRequestValidator : Validator<SaveNodeSettingsRequest>
{
    public SaveNodeSettingsRequestValidator()
    {
        RuleFor(static request => request.MaxMessageRequestTimeoutSeconds)
            .InclusiveBetween(StoredNodeSettings.MinMaxMessageRequestTimeoutSeconds, StoredNodeSettings.MaxMaxMessageRequestTimeoutSeconds);
    }
}
