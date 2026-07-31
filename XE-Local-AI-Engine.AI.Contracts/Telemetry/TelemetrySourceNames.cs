namespace XE_Local_AI_Engine.AI.Contracts.Telemetry;

/// <summary>
///     Stable OpenTelemetry source and meter names shared by instrumentation producers and exporter registration.
/// </summary>
public static class TelemetrySourceNames
{
    /// <summary>The node application's in-house activity source and meter name.</summary>
    public const string Node = "XE.Node";

    /// <summary>The dependency-neutral AI agent activity source and meter name.</summary>
    public const string Agent = "XE.LocalAiEngine.AI.Agent";
}
