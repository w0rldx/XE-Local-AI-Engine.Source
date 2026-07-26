namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Public seam over the provider-internal <c>llama-fit-params</c> stdout parser. Lets the Application-layer
///     Inference Optimizer turn a machine-readable fit result into a frozen replay draft without exposing parser details.
/// </summary>
public interface IFittedArgsParser
{
    /// <summary>
    ///     Builds a replay draft from <paramref name="fitParamsOutput" />, or <see langword="null" /> when the complete
    ///     <c>-c N -ngl N [-ts ...] [-ot "..."]</c> grammar cannot be located.
    /// </summary>
    ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> fitParamsOutput);
}
