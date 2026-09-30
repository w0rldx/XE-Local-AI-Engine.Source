namespace XE_Local_AI_Engine.Client.Services.ModelFit;

/// <summary>
///     The result of the shared remove gate: whether the removal ran, how many llama-server processes were still
///     running when the gate was evaluated, and whether a source build blocked it.
/// </summary>
public sealed record LlamaCppRuntimeRemovalOutcome(bool Removed, int RunningProcessCount, bool BuildActive);
