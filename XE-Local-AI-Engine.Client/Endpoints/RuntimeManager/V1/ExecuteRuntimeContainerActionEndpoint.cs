namespace XE_Local_AI_Engine.Client.Endpoints.RuntimeManager.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Manager;

public sealed class ExecuteRuntimeContainerActionEndpoint(IHostAgentManagerService managerService) : Endpoint<RuntimeContainerActionRequest, RuntimeContainerActionResponse>
{
    private const int DefaultDrainTimeoutSeconds = 30;
    private const int MinimumDrainTimeoutSeconds = 1;
    private const int MaximumDrainTimeoutSeconds = 300;

    private readonly IHostAgentManagerService _managerService = managerService ?? throw new ArgumentNullException(nameof(managerService));

    public override void Configure()
    {
        Post(LocalApiRoutes.RuntimeManager.ContainerAction);
        Policies(LocalOperatorAuthorization.OperatorPolicy);
    }

    public override async Task HandleAsync(RuntimeContainerActionRequest req, CancellationToken ct)
    {
        var containerName = req.ContainerName?.Trim();
        var actionIsValid = TryParseAction(req.Action, out var action);
        var drainTimeoutIsValid = TryParseDrainTimeout(req.DrainTimeoutSeconds, out var drainTimeout);
        var hasValidationErrors = false;

        if (string.IsNullOrWhiteSpace(containerName))
        {
            AddError("Container name is required.");
            hasValidationErrors = true;
        }

        if (!actionIsValid)
        {
            AddError("Action must be one of: start, stop, restart.");
            hasValidationErrors = true;
        }

        if (!drainTimeoutIsValid)
        {
            AddError($"Drain timeout must be between {MinimumDrainTimeoutSeconds} and {MaximumDrainTimeoutSeconds} seconds.");
            hasValidationErrors = true;
        }

        if (hasValidationErrors)
        {
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var report = await _managerService.ExecuteContainerActionAsync(containerName!, action, drainTimeout, ct).ConfigureAwait(false);
            await Send.OkAsync(report.ToResponse(containerName!), ct).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }

    private static bool TryParseAction(string? action, out HostAgentContainerAction parsedAction)
    {
        switch (action?.Trim().ToUpperInvariant())
        {
            case "START":
                parsedAction = HostAgentContainerAction.Start;
                return true;
            case "STOP":
                parsedAction = HostAgentContainerAction.Stop;
                return true;
            case "RESTART":
                parsedAction = HostAgentContainerAction.Restart;
                return true;
            default:
                parsedAction = default;
                return false;
        }
    }

    private static bool TryParseDrainTimeout(int? drainTimeoutSeconds, out TimeSpan drainTimeout)
    {
        var seconds = drainTimeoutSeconds ?? DefaultDrainTimeoutSeconds;
        if (seconds is < MinimumDrainTimeoutSeconds or > MaximumDrainTimeoutSeconds)
        {
            drainTimeout = default;
            return false;
        }

        drainTimeout = TimeSpan.FromSeconds(seconds);
        return true;
    }
}
