namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using System.ComponentModel;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     FastEndpoints handler for the running llama-server processes (GET model-fit/running), one row per running
///     <c>(model, role)</c> process with its diagnostics already sanitized (no internal paths/secrets).
/// </summary>
/// <remarks>
///     There is no dedicated list-running seam: the rows are derived from the llama-server process supervisor's
///     <see cref="LlamaCppRuntimeOrchestrationService.CheckHealthAsync" /> snapshot. A process-probe or transport
///     failure returns an OK-empty list so the running panel can poll and degrade; any other exception is a defect and
///     is left to surface as a 500 rather than be disguised as "nothing is running".
/// </remarks>
public sealed class ListRunningModelsEndpoint : EndpointWithoutRequest<ListRunningModelsResponse>
{
    private readonly ILogger<ListRunningModelsEndpoint> _logger;
    private readonly LlamaCppRuntimeOrchestrationService _runtime;

    public ListRunningModelsEndpoint(LlamaCppRuntimeOrchestrationService runtime,
        ILogger<ListRunningModelsEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(runtime);
        _logger = logger;
        _runtime = runtime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Running);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var health = await _runtime.CheckHealthAsync(ct);
            await Send.OkAsync(new ListRunningModelsResponse
                {
                    Items = [.. health.Select(static process => process.ToResponse())]
                },
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // Narrowed to what the supervisor snapshot can fail with: Process.HasExited (InvalidOperationException, Win32Exception, NotSupportedException) and the probe's
        // transport (HttpRequestException/TimeoutException; a substituted probe may not swallow them). Any other exception must 500: reporting a defect as "nothing is running" misleads eject/update.
        catch (Exception exception) when (exception is InvalidOperationException
                                              or Win32Exception
                                              or NotSupportedException
                                              or HttpRequestException
                                              or TimeoutException
                                              or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Running llama-server process list could not be loaded.");
            await Send.OkAsync(new ListRunningModelsResponse
                {
                    Items = []
                },
                ct);
        }
    }
}
