namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Public <see cref="IFittedArgsParser" /> over the internal <see cref="LlamaFitParamsOutputParser" />. Stateless —
///     the parse is a pure function of the helper stdout, so this is registered as a singleton.
/// </summary>
public sealed class FittedArgsParser : IFittedArgsParser
{
    /// <inheritdoc />
    public ResolvedLaunchArguments? TryParseFittedArgs(IReadOnlyList<string> fitParamsOutput)
    {
        ArgumentNullException.ThrowIfNull(fitParamsOutput);

        return LlamaFitParamsOutputParser.TryParseFittedArgs(fitParamsOutput);
    }
}
