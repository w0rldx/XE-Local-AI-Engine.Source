namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using FluentValidation;

/// <summary>
///     Startup/options validator for get local model details request settings.
/// </summary>
public sealed class GetLocalModelDetailsRequestValidator : Validator<GetLocalModelDetailsRequest>
{
    public GetLocalModelDetailsRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

/// <summary>
///     Startup/options validator for delete local model request settings.
/// </summary>
public sealed class DeleteLocalModelRequestValidator : Validator<DeleteLocalModelRequest>
{
    public DeleteLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

/// <summary>
///     Startup/options validator for select local model request settings.
/// </summary>
public sealed class SelectLocalModelRequestValidator : Validator<SelectLocalModelRequest>
{
    public SelectLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}

/// <summary>
///     Startup/options validator for pull local model request settings.
/// </summary>
public sealed class PullLocalModelRequestValidator : Validator<PullLocalModelRequest>
{
    public PullLocalModelRequestValidator()
    {
        RuleFor(static request => request.ModelName)
            .NotEmpty()
            .MaximumLength(100);
    }
}
