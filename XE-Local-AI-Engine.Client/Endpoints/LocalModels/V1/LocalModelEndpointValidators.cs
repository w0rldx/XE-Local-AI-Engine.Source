namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using FluentValidation;

public sealed class GetLocalModelDetailsRequestValidator : Validator<GetLocalModelDetailsRequest>
{
    public GetLocalModelDetailsRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class DeleteLocalModelRequestValidator : Validator<DeleteLocalModelRequest>
{
    public DeleteLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class SelectLocalModelRequestValidator : Validator<SelectLocalModelRequest>
{
    public SelectLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class PullLocalModelRequestValidator : Validator<PullLocalModelRequest>
{
    public PullLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

public sealed class UnloadLocalModelRequestValidator : Validator<UnloadLocalModelRequest>
{
    public UnloadLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}
