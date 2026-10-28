namespace XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Outcome of a <see cref="ISandboxRuntimeProvider.ExecuteAsync" /> call.
///     <see cref="Completed" /> is <see langword="false" /> when the command was cancelled or the sandbox was killed
///     before it finished. The real provider must redact host paths from <see cref="StandardError" /> at Information
///     level — that is a logging rule for the provider, not part of this DTO.
/// </summary>
public sealed record SandboxCommandResult
{
    /// <summary>How a disk-ceiling stop's <see cref="StandardError" /> starts; the compute gateway keys its typed reason on it.</summary>
    public const string JailDiskCeilingExceededPrefix = "Command exceeded the sandbox jail disk ceiling";

    /// <summary>How an executable that could not be started (not installed, not on the PATH) begins its <see cref="StandardError" />.</summary>
    public const string LaunchFailurePrefix = "The sandbox command could not be launched";

    /// <summary>How a launch failure begins when the filesystem boundary, not the executable, was what could not be set up.</summary>
    public const string BoundaryFailurePrefix = LaunchFailurePrefix + ": the sandbox filesystem boundary could not be established";

    /// <summary>Echo of the request's execution id.</summary>
    public required string ExecutionId { get; init; }

    public required int ExitCode { get; init; }

    /// <summary>Captured standard output.</summary>
    public string StandardOutput { get; init; } = string.Empty;

    /// <summary>Captured standard error.</summary>
    public string StandardError { get; init; } = string.Empty;

    /// <summary>Whether the provider discarded standard-output bytes after its capture ceiling was reached.</summary>
    public bool StandardOutputTruncated { get; init; }

    /// <summary>Whether the provider discarded standard-error bytes after its capture ceiling was reached.</summary>
    public bool StandardErrorTruncated { get; init; }

    /// <summary>Whether the command ran to completion (false when cancelled or killed mid-flight).</summary>
    public required bool Completed { get; init; }

    /// <summary>How long the command ran.</summary>
    public TimeSpan Duration { get; init; }
}
