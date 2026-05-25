namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

public sealed class ValidationProblemProbeRequest
{
    public string? Name { get; init; }
}

public sealed class ValidationProblemProbeResponse
{
    public required string Name { get; init; }
}

public sealed class ValidationProblemProbeRequestValidator : Validator<ValidationProblemProbeRequest>
{
    public ValidationProblemProbeRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .MaximumLength(64);
    }
}

public sealed class ValidationProblemProbeEndpoint : Endpoint<ValidationProblemProbeRequest, ValidationProblemProbeResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.ApiFoundation.ValidationProblemProbe);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ValidationProblemProbeRequest req, CancellationToken ct)
    {
        await Send.OkAsync(new ValidationProblemProbeResponse
        {
            Name = req.Name!
        }, ct).ConfigureAwait(false);
    }
}
