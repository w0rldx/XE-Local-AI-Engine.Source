namespace XE_Local_AI_Engine.Client.Endpoints.ApiFoundation.V1;

using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Request DTO for validation problem probe operations.
/// </summary>
public sealed class ValidationProblemProbeRequest
{
    public string? Name { get; init; }
}

/// <summary>
///     Response DTO for validation problem probe operations.
/// </summary>
public sealed class ValidationProblemProbeResponse
{
    public required string Name { get; init; }
}

/// <summary>
///     Startup/options validator for validation problem probe request settings.
/// </summary>
public sealed class ValidationProblemProbeRequestValidator : Validator<ValidationProblemProbeRequest>
{
    public ValidationProblemProbeRequestValidator()
    {
        RuleFor(static request => request.Name)
            .NotEmpty()
            .MaximumLength(64);
    }
}

/// <summary>
///     FastEndpoints handler for the validation problem probe local API operation.
/// </summary>
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
