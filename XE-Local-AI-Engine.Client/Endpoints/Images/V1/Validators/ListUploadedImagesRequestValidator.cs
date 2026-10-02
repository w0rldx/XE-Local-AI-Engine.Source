namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Bounds the upload list page; an absent limit or offset means the handler's default.</summary>
public sealed class ListUploadedImagesRequestValidator : Validator<ListUploadedImagesRequest>
{
    public const int MaxLimit = 200;

    public ListUploadedImagesRequestValidator()
    {
        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, MaxLimit)
                  .WithMessage($"Ask for between 1 and {MaxLimit} uploads."));

        When(static request => request.Offset is not null,
            () => RuleFor(static request => request.Offset)
                  .GreaterThanOrEqualTo(valueToCompare: 0)
                  .WithMessage("The offset cannot be negative."));
    }
}
