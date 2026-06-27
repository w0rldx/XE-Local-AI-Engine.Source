namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Public seam over the provider-internal llama.cpp <c>--fit on</c> banner parser. Lets the Application-layer
///     Inference Optimizer turn an explore spawn's captured startup output into a frozen replay draft without depending
///     on the internal parser type (which stays internal so its tolerant regexes are not part of the public surface).
/// </summary>
public interface IFittedArgsParser
{
    /// <summary>
    ///     Builds a replay draft from the captured explore-mode <paramref name="startupOutput" />, or
    ///     <see langword="null" /> when no fitted context size (the required anchor) can be located.
    /// </summary>
    ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> startupOutput);
}
