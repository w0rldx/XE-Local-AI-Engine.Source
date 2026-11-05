namespace XE_Local_AI_Engine.Client.Endpoints.Scheduler.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Bounds on <c>GET scheduler/upcoming</c>. An absent limit is valid and means the default count; the ceiling lives
///     here because the handler reads the same constants.
/// </summary>
public sealed class ListUpcomingScheduledJobsRequestValidator : Validator<ListUpcomingScheduledJobsRequest>
{
    public const int DefaultLimit = 5;

    public const int MaxLimit = 20;

    public ListUpcomingScheduledJobsRequestValidator()
    {
        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, MaxLimit)
                  .WithMessage($"Ask for between 1 and {MaxLimit} upcoming jobs."));
    }
}
