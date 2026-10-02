namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.Abstractions.Image;

/// <summary>
///     Refuses a job with no model, no prompt, a seed that is not a base-10 64-bit integer, a size outside 64..2048, no
///     steps, or inconsistent edit parameters.
/// </summary>
/// <remarks>
///     The seed rides the wire as a string because a 64-bit value serialized as a JSON number would round above 2^53,
///     so its shape is checked here rather than left to the binder. The checks report in the order written. Whether
///     the model offers the edit mode and whether the source exists are checked by the coordinator.
/// </remarks>
public sealed class CreateImageJobRequestValidator : Validator<CreateImageJobRequest>
{
    private const int MinDimension = 64;
    private const int MaxDimension = 2048;

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

            if (!SeedValue.TryParse(request.Seed, out _, out var seedError))
            {
                return seedError;
            }

            if (request.Width is < MinDimension or > MaxDimension || request.Height is < MinDimension or > MaxDimension)
            {
                return $"Width and height must be between {MinDimension} and {MaxDimension} pixels.";
            }

            if (request.Steps < 1)
            {
                return "Steps must be at least 1.";
            }

            return EditViolation(request);
        });
    }

    private static string? EditViolation(CreateImageJobRequest request)
    {
        ImageEditMode? mode = null;
        if (request.EditMode is not null)
        {
            if (!ImageEditModeNames.TryParse(request.EditMode, out var parsed))
            {
                return $"The edit mode must be '{ImageEditModeNames.Img2Img}' or '{ImageEditModeNames.Reference}'.";
            }

            mode = parsed;
        }

        if (mode is not null && request.SourceImageId is null)
        {
            return "An edit mode requires a source image.";
        }

        if (mode is null && request.SourceImageId is not null)
        {
            return "A source image requires an edit mode.";
        }

        if (request.Strength is { } strength)
        {
            if (mode != ImageEditMode.Img2Img)
            {
                return "Strength applies only to img2img edits.";
            }

            if (strength is < 0 or > 1 || double.IsNaN(strength))
            {
                return "Strength must be between 0 and 1.";
            }
        }

        return null;
    }
}
