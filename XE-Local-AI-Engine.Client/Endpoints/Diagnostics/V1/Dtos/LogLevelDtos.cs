namespace XE_Local_AI_Engine.Client.Endpoints.Diagnostics.V1;

/// <summary>Request for <c>PUT api/local/v1/diagnostics/log-level</c>: turn Debug logging on or off until the next restart.</summary>
public sealed record SetLogLevelRequest
{
    public required bool Verbose { get; init; }
}

/// <summary>Whether verbose (Debug) logging is on; it resets to the configured level on restart.</summary>
public sealed record LogLevelResponse
{
    public required bool Verbose { get; init; }
}
