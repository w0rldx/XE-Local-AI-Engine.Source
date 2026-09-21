namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Models;

/// <summary>Refuses a job with no model, no prompt, or a seed that is not a base-10 64-bit integer.</summary>
/// <remarks>
///     The seed rides the wire as a string because a 64-bit value serialized as a JSON number would round above 2^53,
///     so its shape is checked here rather than left to the binder. The three checks report in the order written.
/// </remarks>
public sealed class CreateImageJobRequestValidator : Validator<CreateImageJobRequest>
{
    public CreateImageJobRequestValidator()
    {
        this.AddFirstViolationRule(static request =>
        {
            if (string.IsNullOrWhiteSpace(request.ModelName))
            {
                return "A model name is required.";
            }

            if (string.IsNullOrWhiteSpace(request.Prompt))
            {
                return "A prompt is required.";
            }

            return SeedValue.TryParse(request.Seed, out _, out var seedError) ? null : seedError;
        });
    }
}
