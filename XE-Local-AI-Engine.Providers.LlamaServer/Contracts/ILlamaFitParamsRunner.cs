namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Outcome of probing and invoking the sibling <c>llama-fit-params</c> capability.</summary>
internal enum LlamaFitParamsRunStatus
{
    /// <summary>The runtime exposes the helper and it emitted stdout successfully.</summary>
    Succeeded = 0,

    /// <summary>The resolved runtime does not contain a sibling helper executable.</summary>
    MissingCapability = 1,

    /// <summary>The helper exists but could not be started, timed out, or exited unsuccessfully.</summary>
    Failed = 2
}

/// <summary>Machine-readable stdout acquisition result from <c>llama-fit-params</c>.</summary>
internal sealed record LlamaFitParamsRunResult(
    LlamaFitParamsRunStatus Status,
    IReadOnlyList<string> StandardOutput,
    string? FailureReason)
{
    public static LlamaFitParamsRunResult Success(IReadOnlyList<string> standardOutput) =>
        new(LlamaFitParamsRunStatus.Succeeded, standardOutput, FailureReason: null);

    public static LlamaFitParamsRunResult Missing() =>
        new(LlamaFitParamsRunStatus.MissingCapability, StandardOutput: [], FailureReason: null);

    public static LlamaFitParamsRunResult Failure(string reason) =>
        new(LlamaFitParamsRunStatus.Failed, StandardOutput: [], reason);
}

/// <summary>
///     Runs the machine-readable <c>llama-fit-params</c> helper co-located with a resolved
///     <c>llama-server</c> runtime.
/// </summary>
internal interface ILlamaFitParamsRunner
{
    Task<LlamaFitParamsRunResult> RunAsync(LlamaServerLaunchSpec serverSpec, CancellationToken ct);
}
