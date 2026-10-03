namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>Reads the session-only verbose-logging switch.</summary>
public sealed class GetLogLevelEndpoint : EndpointWithoutRequest<LogLevelResponse>
{
    private readonly NodeLogLevelSwitch _logLevel;

    public GetLogLevelEndpoint(NodeLogLevelSwitch logLevel)
    {
        ArgumentNullException.ThrowIfNull(logLevel);
        _logLevel = logLevel;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Diagnostics.LogLevel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(CancellationToken ct) =>
        Send.OkAsync(new LogLevelResponse
        {
            Verbose = _logLevel.Verbose
        }, ct);
}
