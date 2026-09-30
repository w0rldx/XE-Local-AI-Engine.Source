namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;

/// <summary>The outcome of one filesystem-isolation measurement: the ingredients, or the reason there are none.</summary>
internal sealed class SandboxFilesystemIsolationProbeResult
{
    public required SandboxFilesystemIsolation? Isolation { get; init; }

    public required string? Reason { get; init; }
}
