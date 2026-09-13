namespace XE_Local_AI_Engine.Client.Endpoints.Transcription.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Bounds on the session page. An absent limit or offset is valid and means "the default page", so both rules are
///     conditional; the ceiling lives here because the handler clamps against the same constant.
/// </summary>
public sealed class ListTranscriptionSessionsRequestValidator : Validator<ListTranscriptionSessionsRequest>
{
    public const int MaxLimit = 200;

    public ListTranscriptionSessionsRequestValidator()
    {
        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, MaxLimit)
                  .WithMessage($"Ask for between 1 and {MaxLimit} sessions."));

        When(static request => request.Offset is not null,
            () => RuleFor(static request => request.Offset)
                  .GreaterThanOrEqualTo(valueToCompare: 0)
                  .WithMessage("The offset cannot be negative."));
    }
}
