namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1.Validators;

using FastEndpoints;
using FluentValidation;

public sealed class GetLocalModelDetailsRequestValidator : Validator<GetLocalModelDetailsRequest>
{
    public GetLocalModelDetailsRequestValidator()
    {
        // Stop after the first failing rule: the handler that held the grammar check ran only once the bound-shape
        // rules had passed, so a name that is both over-length and ungrammatical still reports one error, not two.
        ClassLevelCascadeMode = CascadeMode.Stop;

        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);

        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}

public sealed class DeleteLocalModelRequestValidator : Validator<DeleteLocalModelRequest>
{
    public DeleteLocalModelRequestValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);

        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}

/// <summary>
///     The one local-model request whose name arrives in the body, so the grammar judges it raw.
/// </summary>
/// <remarks>
///     The select handler reads <c>ModelName</c> unchanged for both the external-registration guard and the write,
///     so decoding it here would accept a name that is then stored escaped. See <see cref="ModelNameRules" />.
/// </remarks>
public sealed class SelectLocalModelRequestValidator : Validator<SelectLocalModelRequest>
{
    public SelectLocalModelRequestValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);

        this.AddBodyBoundModelNameRule(static request => request.ModelName);
    }
}

public sealed class UnloadLocalModelRequestValidator : Validator<UnloadLocalModelRequest>
{
    public UnloadLocalModelRequestValidator()
    {
        ClassLevelCascadeMode = CascadeMode.Stop;

        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);

        this.AddRouteBoundModelNameRule(static request => request.ModelName);
    }
}
