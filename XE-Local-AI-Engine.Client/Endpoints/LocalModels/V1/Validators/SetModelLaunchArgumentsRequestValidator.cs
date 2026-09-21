namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using FastEndpoints;
using FluentValidation;
using FluentValidation.Results;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     The three shape checks on a launch-argument override: the model name, the length cap, and the flags the app
///     binds itself.
/// </summary>
/// <remarks>
///     Rejects the app-managed flags: reachability (<c>-m</c>/<c>--model</c>/<c>--host</c>/<c>--port</c>) and the
///     memory-fit placement family (<c>-c</c>/<c>-ngl</c>/<c>-ts</c>/<c>-ot</c>/<c>-ctk</c>/<c>-ctv</c>/<c>-fa</c>/
///     <c>--parallel</c>/<c>-b</c>/<c>-ub</c>, which the capacity/allocation resolver decides before admission). Which
///     flags those are is <see cref="LlamaLaunchArgumentParser" />'s to say, and the name grammar is
///     <see cref="ModelNameValidator" />'s: this type decides only when to ask and how to report.
/// </remarks>
public sealed class SetModelLaunchArgumentsRequestValidator : Validator<SetModelLaunchArgumentsRequest>
{
    // A generous cap for a hand-typed flag string; guards the store against an abusive payload while leaving room for
    // several flags with values. Well above any realistic llama.cpp argument line.
    private const int MaxRawArgumentsLength = 4096;

    // FastEndpoints' ErrorOptions.GeneralErrorsField default — the property AddError(message) attaches to.
    private const string GeneralErrorsField = "GeneralErrors";

    public SetModelLaunchArgumentsRequestValidator()
    {
        // Both halves carry the endpoint's behaviour across: a failure goes to the GENERAL error field rather than the
        // field it came from, and the cascade stops at the first failing rule, as returning after the first AddError did.
        ClassLevelCascadeMode = CascadeMode.Stop;

        // The name arrives in a route segment, so the shared rule decodes it before the grammar runs — the same
        // decode the handler does, and the reason a body-bound name uses the other form.
        this.AddRouteBoundModelNameRule(static request => request.ModelName);

        RuleFor(static request => request.RawArguments)
            .Custom(static (rawArguments, context) =>
            {
                if (Normalize(rawArguments).Length > MaxRawArgumentsLength)
                {
                    context.AddFailure(new ValidationFailure(GeneralErrorsField,
                        $"Launch arguments are too long (max {MaxRawArgumentsLength} characters)."));
                }
            });

        // Name the offending flag, so the operator understands why it cannot be set: the app binds the reachability flags (-m/--host/--port) itself, and the capacity/allocation
        // resolver decides the placement flags before admission (a post-hoc override breaks app-to-process reachability or invalidates the memory ledger). Everything else is permitted.
        RuleFor(static request => request.RawArguments)
            .Custom(static (rawArguments, context) =>
            {
                if (LlamaLaunchArgumentParser.FindReservedFlag(Normalize(rawArguments)) is { } reserved)
                {
                    context.AddFailure(new ValidationFailure(GeneralErrorsField,
                        $"The '{reserved}' argument is managed by the app and cannot be overridden here. Remove it and try again."));
                }
            });
    }

    /// <summary>The same trimmed, never-null string the endpoint stores, so the checks measure what is persisted.</summary>
    private static string Normalize(string? rawArguments) => (rawArguments ?? string.Empty).Trim();
}
