namespace XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Validators;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Integrations.V1.Mappers;

/// <summary>
///     Shape validation only: the name pattern, lengths, enum membership and the input-kind array. The two checks that
///     need a database — the target agent exists, and a caller-managed trigger's agent is read-only — belong to
///     <c>IIntegrationTriggerService</c>, because a probe does not belong in a validator.
/// </summary>
public static class IntegrationTriggerValidationRules
{
    /// <summary>The external name pattern, restated from the entity's own contract.</summary>
    public const string NamePattern = "^[a-z0-9][a-z0-9-]{1,63}$";

    public const string NameMessage = "Use lowercase letters, digits and hyphens (2-64 characters).";

    public const int MaxDisplayNameLength = 128;

    public const int MaxDescriptionLength = 1024;

    public const string InputKindsMessage = "Select at least one accepted input kind (text, json).";

    internal static void ApplyInputKinds<T>(AbstractValidator<T> validator, Func<T, IReadOnlyList<string>?> selector)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(selector);

        // One rule for "empty" and "contains something that is not a kind": both mean the trigger would accept
        // something other than what the operator asked for, and FromWireInputKinds is the single decoder.
        _ = validator.RuleFor(request => selector(request))
                     .Must(static names => names is { Count: > 0 } && IntegrationMapper.FromWireInputKinds(names) is not null)
                     .WithMessage(InputKindsMessage)
                     .OverridePropertyName("acceptedInputKinds");
    }

    internal static void ApplyDisplayFields<T>(AbstractValidator<T> validator,
        Func<T, string?> displayName,
        Func<T, string?> description)
    {
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(description);

        _ = validator.RuleFor(request => displayName(request))
                     .NotEmpty()
                     .WithMessage("A trigger needs a display name.")
                     .MaximumLength(MaxDisplayNameLength)
                     .WithMessage($"The display name is longer than the {MaxDisplayNameLength}-character limit.")
                     .OverridePropertyName("displayName");

        _ = validator.RuleFor(request => description(request))
                     .MaximumLength(MaxDescriptionLength)
                     .WithMessage($"The description is longer than the {MaxDescriptionLength}-character limit.")
                     .OverridePropertyName("description");
    }
}

public sealed class CreateIntegrationTriggerRequestValidator : Validator<CreateIntegrationTriggerRequest>
{
    public CreateIntegrationTriggerRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .WithMessage(IntegrationTriggerValidationRules.NameMessage)
            .Matches(IntegrationTriggerValidationRules.NamePattern)
            .WithMessage(IntegrationTriggerValidationRules.NameMessage);

        RuleFor(static request => request.TargetAgentDefinitionId)
            .NotEmpty()
            .WithMessage("A trigger needs a target agent.");

        RuleFor(static request => request.TargetKind).IsInEnum();
        RuleFor(static request => request.SessionPolicy).IsInEnum();

        IntegrationTriggerValidationRules.ApplyDisplayFields(this, static request => request.DisplayName, static request => request.Description);
        IntegrationTriggerValidationRules.ApplyInputKinds(this, static request => request.AcceptedInputKinds);
    }
}

public sealed class UpdateIntegrationTriggerRequestValidator : Validator<UpdateIntegrationTriggerRequest>
{
    public UpdateIntegrationTriggerRequestValidator()
    {
        RuleFor(static request => request.TargetAgentDefinitionId)
            .NotEmpty()
            .WithMessage("A trigger needs a target agent.");

        RuleFor(static request => request.SessionPolicy).IsInEnum();

        RuleFor(static request => request.ExpectedVersion)
            .GreaterThan(valueToCompare: 0)
            .WithMessage("Send the version the trigger was loaded with.");

        IntegrationTriggerValidationRules.ApplyDisplayFields(this, static request => request.DisplayName, static request => request.Description);
        IntegrationTriggerValidationRules.ApplyInputKinds(this, static request => request.AcceptedInputKinds);
    }
}

public sealed class GenerateIntegrationApiKeyRequestValidator : Validator<GenerateIntegrationApiKeyRequest>
{
    public GenerateIntegrationApiKeyRequestValidator()
    {
        RuleFor(static request => request.Label)
            .NotEmpty()
            .WithMessage("A credential needs a label so an operator can tell two keys apart.")
            .MaximumLength(IntegrationTriggerValidationRules.MaxDisplayNameLength)
            .WithMessage($"The label is longer than the {IntegrationTriggerValidationRules.MaxDisplayNameLength}-character limit.");

        // An explicit but EMPTY allowlist is a key that can invoke nothing, which is never what an operator means:
        // "every trigger" is expressed by omitting the field, not by sending [].
        When(static request => request.AllowedTriggerIds is not null,
            () => RuleFor(static request => request.AllowedTriggerIds)
                  .Must(static ids => ids is { Count: > 0 })
                  .WithMessage("Select at least one trigger, or omit the allowlist to allow every trigger."));
    }
}

/// <summary>
///     Shape rules for the executions query. The paging bounds are here rather than clamped silently in the service: a
///     caller asking for 5,000 rows has misunderstood the endpoint, and answering 400 says so.
/// </summary>
public sealed class ListIntegrationExecutionsRequestValidator : Validator<ListIntegrationExecutionsRequest>
{
    public const int MaxLimit = 200;

    public ListIntegrationExecutionsRequestValidator()
    {
        When(static request => request.Status is not null,
            () => RuleFor(static request => request.Status)
                  .IsInEnum()
                  .WithMessage("Filter on a known execution status."));

        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, MaxLimit)
                  .WithMessage($"Ask for between 1 and {MaxLimit} executions."));

        When(static request => request.Offset is not null,
            () => RuleFor(static request => request.Offset)
                  .GreaterThanOrEqualTo(valueToCompare: 0)
                  .WithMessage("The offset cannot be negative."));
    }
}

/// <summary>
///     Shape rules for the sessions query. Same bounds and same reasoning as the executions list: a caller asking for
///     5,000 rows has misunderstood the endpoint, and answering 400 says so.
/// </summary>
public sealed class ListIntegrationSessionsRequestValidator : Validator<ListIntegrationSessionsRequest>
{
    public const int MaxLimit = 200;

    public ListIntegrationSessionsRequestValidator()
    {
        When(static request => request.Status is not null,
            () => RuleFor(static request => request.Status)
                  .IsInEnum()
                  .WithMessage("Filter on a known session status."));

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

/// <summary>
///     Bounds on the event page. The limit shares its ceiling with the external recovery route, which reads
///     <see cref="IntegrationEventPage" /> too, so the two pages cannot drift.
/// </summary>
public sealed class ListIntegrationExecutionEventsRequestValidator : Validator<ListIntegrationExecutionEventsRequest>
{
    public ListIntegrationExecutionEventsRequestValidator()
    {
        When(static request => request.SinceSeq is not null,
            () => RuleFor(static request => request.SinceSeq)
                  .GreaterThanOrEqualTo(valueToCompare: 0)
                  .WithMessage("A sequence watermark cannot be negative."));

        When(static request => request.Limit is not null,
            () => RuleFor(static request => request.Limit)
                  .InclusiveBetween(from: 1, IntegrationEventPage.MaxLimit)
                  .WithMessage($"Ask for between 1 and {IntegrationEventPage.MaxLimit} events."));
    }
}
