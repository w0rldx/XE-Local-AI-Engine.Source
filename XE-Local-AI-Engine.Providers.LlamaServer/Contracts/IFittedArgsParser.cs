namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Public seam over the provider-internal <c>llama-fit-params</c> stdout parser. Lets the Application-layer
///     Inference Optimizer turn a machine-readable fit result into a frozen replay draft without exposing parser details.
/// </summary>
public interface IFittedArgsParser
{
    /// <summary>
    ///     Builds a replay draft from <paramref name="fitParamsOutput" /> plus authoritative
    ///     <paramref name="startupOutput" /> placement evidence and the exact
    ///     <paramref name="successfulLaunchArguments" /> that reached readiness, or <see langword="null" /> when no
    ///     concrete replay can be proven. The helper's automatic <c>-ngl -1</c> sentinel is replayable only when startup
    ///     output proves every layer was offloaded. KV/flash-attention policy and expert placement are preserved only
    ///     from the successful argv, so a failed optimized candidate cannot contaminate a safe fallback profile. A spawn
    ///     that carried <c>--cpu-moe</c> yields a draft only when the fit output names the equivalent <c>-ot</c>
    ///     placement — a replay that silently dropped the flag would run outside its admitted footprint.
    /// </summary>
    ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> fitParamsOutput,
        IReadOnlyList<string> startupOutput,
        IReadOnlyList<string> successfulLaunchArguments);
}
