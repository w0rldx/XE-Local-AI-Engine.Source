namespace XE_Local_AI_Engine.Client.Services.Compute;

using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Why <see cref="IComputeToolGateway.ExecuteDetailedAsync" /> refused to run a script. Every value here means the
///     SANDBOX could not be trusted to run the code, never that the code ran and failed — a caller that scores an
///     execution must treat a refusal as unmeasurable rather than as a zero.
/// </summary>
internal static class ComputeRefusalCodes
{
    /// <summary>The node kill-switch (<c>Compute:Enabled</c>) is off.</summary>
    public const string ComputeDisabled = "compute-disabled";

    /// <summary>The request did not satisfy <see cref="ComputeRunToolRequestValidator" />.</summary>
    public const string InvalidRequest = "invalid-request";

    /// <summary>The provider cannot give the script a mount namespace of its own.</summary>
    public const string NoIsolation = "no-isolation";

    /// <summary>
    ///     The caller demanded enforceable CPU / memory / process ceilings and the backend cannot impose them
    ///     (<see cref="SandboxResourceCeilings.Resolve" /> would return <see langword="null" />). Asked for only by
    ///     unattended callers; <c>run_python</c> deliberately does not ask.
    /// </summary>
    public const string NoResourceLimits = "no-resource-limits";

    /// <summary>The provider created a sandbox but named no jail root to back its writable tree.</summary>
    public const string NoJailRoot = "no-jail-root";

    /// <summary>The pinned interpreter could not be provisioned (<see cref="ComputeEnvironmentException" />).</summary>
    public const string EnvironmentUnavailable = "environment-unavailable";

    /// <summary>The sandbox could not be created with the requested containment.</summary>
    public const string ContainmentUnavailable = "containment-unavailable";
}

/// <summary>
///     The structured outcome of one compute invocation: either the sandbox ran the script and there is a
///     <see cref="SandboxCommandResult" /> to read, or it refused and there is a code and an operator-safe message.
///     <para>
///         This is the projection a PROGRAMMATIC caller needs. <see cref="IComputeToolGateway.ExecuteAsync" /> renders
///         the same outcome as the model-facing string, so the two can never disagree about whether a script ran — the
///         reason the boundary is one method with two projections rather than two entry points.
///     </para>
/// </summary>
internal sealed record ComputeExecutionOutcome(bool Ran, string? RefusalCode, string? RefusalMessage, SandboxCommandResult? Result)
{
    public static ComputeExecutionOutcome Refused(string refusalCode, string refusalMessage) =>
        new(Ran: false, refusalCode, refusalMessage, Result: null);

    public static ComputeExecutionOutcome Executed(SandboxCommandResult result) =>
        new(Ran: true, RefusalCode: null, RefusalMessage: null, result);
}
