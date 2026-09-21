namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;

/// <summary>Refuses a blank route name before the delete reaches the model store.</summary>
/// <remarks>
///     The raw bound name is judged, as the handler's own check judged it; the store is handed the trimmed name, and
///     a padded-but-non-blank name is accepted by both.
/// </remarks>
public sealed class DeleteImageModelRequestValidator : Validator<DeleteImageModelRequest>
{
    public DeleteImageModelRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
            string.IsNullOrWhiteSpace(request.ModelName) ? "A model name is required." : null);
    }
}
