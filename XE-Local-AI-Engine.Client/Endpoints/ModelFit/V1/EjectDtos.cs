namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

// ---------------------------------------------------------------------------
// Running-models / eject DTOs (llama-server supervisor passthrough)
// ---------------------------------------------------------------------------

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
///     Body for <c>POST model-fit/running/eject</c>. Evicts (tree-kills) the running <c>(model, role)</c> process.
///     <see cref="Role" /> is <c>chat|embedding</c> (case-insensitive); an unknown role is rejected with a 400.
/// </summary>
public sealed class EjectRunningModelRequest
{
    public required string ModelName { get; init; }

    /// <summary>Role of the process to evict — <c>chat|embedding</c>. Defaults to <c>chat</c> when omitted.</summary>
    public string? Role { get; init; }
}

/// <summary>Response for <c>POST model-fit/running/eject</c>. Eviction is idempotent.</summary>
public sealed class EjectRunningModelResponse
{
    public required string ModelName { get; init; }

    public required string Role { get; init; }
}
