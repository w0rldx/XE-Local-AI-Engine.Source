namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Validation;

/// <summary>
///     Sets (or clears, when the string is blank) the per-model extra <c>llama-server</c> launch-argument override
///     (developer/advanced), taking effect the next time the model is (re)loaded.
/// </summary>
/// <remarks>
///     Rejects the app-managed flags: reachability (<c>-m</c>/<c>--model</c>/<c>--host</c>/<c>--port</c>) and the
///     memory-fit placement family (<c>-c</c>/<c>-ngl</c>/<c>-ts</c>/<c>-ot</c>/<c>-ctk</c>/<c>-ctv</c>/<c>-fa</c>/
///     <c>--parallel</c>/<c>-b</c>/<c>-ub</c>, which the capacity/allocation resolver decides before admission). Every
///     other flag llama.cpp supports (sampling, RoPE, penalties, …) is stored verbatim and appended to the process on
///     the next cold load, so the operator can experiment with it.
/// </remarks>
public sealed class PutModelLaunchArgumentsEndpoint : Endpoint<SetModelLaunchArgumentsRequest, ModelLaunchArgumentsResponse>
{
    // A generous cap for a hand-typed flag string; guards the store against an abusive payload while leaving room for
    // several flags with values. Well above any realistic llama.cpp argument line.
    private const int MaxRawArgumentsLength = 4096;

    private readonly ModelLaunchArgumentsService _launchArguments;
    private readonly ModelNameValidator _modelNameValidator;

    public PutModelLaunchArgumentsEndpoint(
        ModelLaunchArgumentsService launchArguments,
        ModelNameValidator modelNameValidator)
    {
        ArgumentNullException.ThrowIfNull(launchArguments);
        ArgumentNullException.ThrowIfNull(modelNameValidator);
        _launchArguments = launchArguments;
        _modelNameValidator = modelNameValidator;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.LocalModels.ModelLaunchArguments);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetModelLaunchArgumentsRequest req, CancellationToken ct)
    {
        // Decode FIRST: the bound route value may still contain literal %2F (see ModelRouteName), so validate and store
        // the decoded canonical name.
        var decodedModelName = ModelRouteName.Decode(req.ModelName);
        var validationError = _modelNameValidator.GetValidationError(decodedModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var raw = (req.RawArguments ?? string.Empty).Trim();

        if (raw.Length > MaxRawArgumentsLength)
        {
            AddError($"Launch arguments are too long (max {MaxRawArgumentsLength} characters).");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        // A blank override is a clear-to-default, not a stored empty row.
        if (raw.Length == 0)
        {
            await _launchArguments.ClearAsync(decodedModelName!, ct);
            await Send.OkAsync(new ModelLaunchArgumentsResponse
                {
                    ModelName = decodedModelName!,
                    RawArguments = string.Empty
                },
                ct);
            return;
        }

        // Name the offending flag, so the operator understands why it cannot be set: the app binds the reachability flags (-m/--host/--port) itself, and the capacity/allocation
        // resolver decides the placement flags before admission (a post-hoc override breaks app-to-process reachability or invalidates the memory ledger). Everything else is permitted.
        if (LlamaLaunchArgumentParser.FindReservedFlag(raw) is { } reserved)
        {
            AddError($"The '{reserved}' argument is managed by the app and cannot be overridden here. Remove it and try again.");
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        var result = await _launchArguments.SaveAsync(decodedModelName!, raw, ct);
        await Send.OkAsync(new ModelLaunchArgumentsResponse
            {
                ModelName = result.ModelName,
                RawArguments = result.RawArguments
            },
            ct);
    }
}
