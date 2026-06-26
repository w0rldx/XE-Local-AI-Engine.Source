namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The self-satisfying <see cref="IInferenceProfileResolver" /> shipped with the provider: it always resolves to
///     <see cref="ResolvedLaunchArguments.Explore" />, so a node with no profile store launches with llama.cpp
///     auto-fit (<c>--fit on</c>) driving placement.
/// </summary>
/// <remarks>
///     <para>
///         The real DB-backed resolver lives in <c>Client.Application</c> and replaces this one via DI registration
///         order — <c>AddLlamaServerLocalModelProvider</c> registers this default with <c>TryAddSingleton</c>, and the
///         Application host registers its own implementation last so the last registration wins. This default keeps the
///         supervisor resolvable (and the explore path working) until then, without inverting the layer dependency.
///     </para>
/// </remarks>
internal sealed class DefaultInferenceProfileResolver : IInferenceProfileResolver
{
    /// <inheritdoc />
    public Task<ResolvedLaunchArguments> ResolveAsync(string modelName, ModelRole role, GpuVariant backend, CancellationToken ct)
    {
        return Task.FromResult(ResolvedLaunchArguments.Explore());
    }
}
