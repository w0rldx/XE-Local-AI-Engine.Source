namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>One running llama-server process derived from the supervisor health snapshot. Diagnostics are sanitized.</summary>
public sealed class RunningModelResponse
{
    public required string ModelName { get; init; }

    /// <summary>Lowercase role the process serves — <c>chat|embedding</c>.</summary>
    public required string Role { get; init; }

    public required bool IsResponsive { get; init; }

    /// <summary>A sanitized, user-safe diagnostic line (no internal paths/secrets).</summary>
    public required string Detail { get; init; }
}

/// <summary>Response envelope for <c>GET model-fit/running</c>.</summary>
public sealed class ListRunningModelsResponse
{
    public required IReadOnlyList<RunningModelResponse> Items { get; init; }
}

/// <summary>
///     Body for <c>POST model-fit/running/eject</c>. Ejects the running <c>(model, role)</c> process. By default the
///     eject is GRACEFUL: it waits a bounded window for in-flight inference to drain before teardown, and reports back
///     (via <see cref="EjectRunningModelResponse.Outcome" />) when it could not complete safely rather than killing a
///     running turn silently. <see cref="Role" /> is <c>chat|embedding</c> (case-insensitive); an unknown role → 400.
/// </summary>
public sealed class EjectRunningModelRequest
{
    public required string ModelName { get; init; }

    /// <summary>Role of the process to evict — <c>chat|embedding</c>. Defaults to <c>chat</c> when omitted.</summary>
    public string? Role { get; init; }

    /// <summary>
    ///     When <see langword="true" />, tear the process down even if in-flight inference has not drained within the
    ///     bounded window (interrupting the running turn, which is then marked operator-ejected). Defaults to
    ///     <see langword="false" /> (graceful — never interrupts a running turn).
    /// </summary>
    public bool Force { get; init; }
}

/// <summary>
///     Response for <c>POST model-fit/running/eject</c>. The eject is idempotent; <see cref="Outcome" /> reports what
///     actually happened.
/// </summary>
public sealed class EjectRunningModelResponse
{
    public required string ModelName { get; init; }

    public required string Role { get; init; }

    /// <summary>
    ///     What the eject actually did: <c>ejected</c> (idle or drained cleanly — no turn interrupted),
    ///     <c>timed_out_still_busy</c> (in-flight work did not drain and no force was requested — the process was left
    ///     running), <c>forced</c> (torn down despite in-flight work because <c>force</c> was set), or <c>not_running</c>
    ///     (nothing was loaded — idempotent no-op).
    /// </summary>
    public required string Outcome { get; init; }
}
