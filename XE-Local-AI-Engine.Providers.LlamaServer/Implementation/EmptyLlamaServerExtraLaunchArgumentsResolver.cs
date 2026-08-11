namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The self-satisfying <see cref="ILlamaServerExtraLaunchArgumentsResolver" /> shipped with the provider: it always
///     resolves to an empty token list, so a node with no per-model override store spawns exactly as before.
/// </summary>
/// <remarks>
///     The real store-backed resolver lives in <c>Client.Application</c> and replaces this one via DI registration order —
///     <c>AddLlamaServerLocalModelProvider</c> registers this default with <c>TryAddSingleton</c>, and the Application host
///     registers its own implementation last so the last registration wins.
/// </remarks>
internal sealed class EmptyLlamaServerExtraLaunchArgumentsResolver : ILlamaServerExtraLaunchArgumentsResolver
{
    private static readonly IReadOnlyList<string> None = [];

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> ResolveAsync(string modelName, ModelRole role, CancellationToken ct)
    {
        return Task.FromResult(None);
    }
}
