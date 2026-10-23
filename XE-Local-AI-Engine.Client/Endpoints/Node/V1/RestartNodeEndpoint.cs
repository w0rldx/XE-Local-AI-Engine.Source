namespace XE_Local_AI_Engine.Client.Endpoints.Node.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Restarts the node: 202, then the host stops and the process exits <see cref="Program.RestartRequestedExitCode" />,
///     which the desktop shell answers by relaunching the engine and reloading its window.
/// </summary>
/// <remarks>
///     Desktop-mode only (absent elsewhere), and 409 when no desktop shell owns this engine: nothing would start it again.
///     The exit code is set only once the response completed, beside the stop, so a failed send never leaves it behind.
/// </remarks>
public sealed class RestartNodeEndpoint : EndpointWithoutRequest, IDesktopOnlyEndpoint
{
    private readonly AppUpdateHostContext _hostContext;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<RestartNodeEndpoint> _logger;

    public RestartNodeEndpoint(AppUpdateHostContext hostContext, IHostApplicationLifetime lifetime, ILogger<RestartNodeEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(hostContext);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        _hostContext = hostContext;
        _lifetime = lifetime;
        _logger = logger;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Node.Restart);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status202Accepted)
                                      .ProducesProblem(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!_hostContext.IsShellOwned)
        {
            await Send.ResultAsync(Results.Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Only the desktop app can restart the node; stop and start it the way it was started."));
            return;
        }

        _logger.LogInformation("Node restart requested by the operator");
        HttpContext.Response.OnCompleted(static state =>
            {
                Environment.ExitCode = Program.RestartRequestedExitCode;
                ((IHostApplicationLifetime)state).StopApplication();
                return Task.CompletedTask;
            },
            _lifetime);
        await Send.ResultAsync(Results.Accepted());
    }
}
