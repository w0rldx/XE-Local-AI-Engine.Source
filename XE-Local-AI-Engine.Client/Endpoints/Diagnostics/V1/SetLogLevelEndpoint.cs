namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Diagnostics;

/// <summary>Turns verbose (Debug) logging on or off until the next restart, and records the change in the log.</summary>
public sealed class SetLogLevelEndpoint : Endpoint<SetLogLevelRequest, LogLevelResponse>
{
    private readonly NodeLogLevelSwitch _logLevel;
    private readonly ILogger<SetLogLevelEndpoint> _logger;

    public SetLogLevelEndpoint(NodeLogLevelSwitch logLevel, ILogger<SetLogLevelEndpoint> logger)
    {
        ArgumentNullException.ThrowIfNull(logLevel);
        ArgumentNullException.ThrowIfNull(logger);
        _logLevel = logLevel;
        _logger = logger;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.Diagnostics.LogLevel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override Task HandleAsync(SetLogLevelRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        _logLevel.Set(req.Verbose);
        _logger.LogInformation("Verbose logging {State} by the operator", req.Verbose ? "enabled" : "disabled");
        return Send.OkAsync(new LogLevelResponse { Verbose = _logLevel.Verbose }, ct);
    }
}
