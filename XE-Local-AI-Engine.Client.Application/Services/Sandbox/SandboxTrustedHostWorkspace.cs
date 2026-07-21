namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Binds a sandbox to an engine-managed host workspace that must survive sandbox/process restarts. Providers that
///     cannot safely confine execution to the supplied canonical root reject the request fail-closed.
/// </summary>
public sealed record SandboxTrustedHostWorkspace
{
    /// <summary>Canonical engine-owned directory used as the sandbox root.</summary>
    public required string RootPath { get; init; }
}
