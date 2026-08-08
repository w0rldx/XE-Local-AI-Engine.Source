namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     A copy between host and sandbox. One shape serves both directions:
///     <see cref="ISandboxRuntimeProvider.CopyIntoAsync" /> reads <see cref="SourcePath" /> on the host and writes
///     <see cref="DestinationPath" /> in the sandbox; <see cref="ISandboxRuntimeProvider.CopyOutAsync" /> reverses
///     it. Selected-folder exclusion rules are applied by the workspace service, not
///     here.
/// </summary>
public sealed record SandboxCopyRequest
{
    /// <summary>The path to read from (host for copy-into, sandbox for copy-out).</summary>
    public required string SourcePath { get; init; }

    /// <summary>The path to write to (sandbox for copy-into, host for copy-out).</summary>
    public required string DestinationPath { get; init; }
}
