namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Public seam over the provider-internal <c>llama-fit-params</c> stdout parser. Lets the Application-layer
///     Inference Optimizer turn a machine-readable fit result into a frozen replay draft without exposing parser details.
/// </summary>
public interface IFittedArgsParser
{
    /// <summary>
    ///     Builds a replay draft from <paramref name="fitParamsOutput" />, the authoritative
    ///     <paramref name="startupOutput" /> placement evidence and the exact
    ///     <paramref name="successfulLaunchArguments" /> that reached readiness, or <see langword="null" /> when no
    ///     concrete replay can be proven.
    /// </summary>
    /// <remarks>
    ///     The helper's automatic layer-count sentinel is replayable only when startup output proves every layer was
    ///     offloaded. KV, flash-attention policy and expert placement are preserved only from the SUCCESSFUL argv, so a
    ///     failed optimized candidate cannot contaminate a safe fallback profile. An expert-offload spawn yields a
    ///     draft only when the fit output names the equivalent tensor-override placement — a replay that silently
    ///     dropped the flag would run outside its admitted footprint.
    /// </remarks>
    ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> fitParamsOutput,
        IReadOnlyList<string> startupOutput,
        IReadOnlyList<string> successfulLaunchArguments);
}
