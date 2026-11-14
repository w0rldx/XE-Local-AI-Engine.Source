namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Bounds on <c>GET scheduler/runs</c>. An absent limit is valid and means the default page; the ceiling lives here
///     because the handler reads the same constants.
/// </summary>
public sealed class ListScheduledJobRunsRequestValidator : Validator<ListScheduledJobRunsRequest>
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;

    public ListScheduledJobRunsRequestValidator()
    {
        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, MaxLimit)
                  .WithMessage($"Ask for between 1 and {MaxLimit} runs."));

        When(static request => request.Offset is not null,
            () => RuleFor(static request => request.Offset)
                  .GreaterThanOrEqualTo(valueToCompare: 0)
                  .WithMessage("The offset cannot be negative."));
    }
}
