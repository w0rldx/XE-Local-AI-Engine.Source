namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using System.ComponentModel;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     FastEndpoints handler for the running llama-server processes (GET model-fit/running). There is no dedicated
///     list-running seam — the running models are derived from the llama-server process supervisor's
///     <see cref="ILlamaServerProcessSupervisor.CheckHealthAsync" /> snapshot (one row per running <c>(model, role)</c>
///     process). A process-probe or transport failure returns an OK-empty list so the running panel can poll and
///     degrade; any other exception is a defect and is left to surface as a 500 rather than be disguised as "nothing is
///     running". Each row's diagnostics are already sanitized (no internal paths/secrets).
/// </summary>
public sealed class ListRunningModelsEndpoint(
    ILlamaServerProcessSupervisor supervisor,
    ILogger<ListRunningModelsEndpoint> logger) : EndpointWithoutRequest<ListRunningModelsResponse>
{
    private readonly ILogger<ListRunningModelsEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ILlamaServerProcessSupervisor _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.Running);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        try
        {
            var health = await _supervisor.CheckHealthAsync(ct).ConfigureAwait(false);
            await Send.OkAsync(new ListRunningModelsResponse
                {
                    Items = [.. health.Select(static process => process.ToResponse())]
                },
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        // Narrowed to what the supervisor snapshot can actually fail with: Process.HasExited (InvalidOperationException,
        // Win32Exception, NotSupportedException) and the liveness probe's transport (HttpRequestException/TimeoutException
        // — the shipped probe already swallows both, a substituted one need not). Everything else is a bug in our code
        // and must surface as a 500 rather than be reported to the running panel as "nothing is running", which is what
        // the previous catch-all did — and eject/update decisions are made off that answer.
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
                ct).ConfigureAwait(false);
        }
    }
}
