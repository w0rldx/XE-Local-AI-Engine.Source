namespace XE_Local_AI_Engine.Providers.LlamaServer;

using XE_Local_AI_Engine.HostAgent.Abstractions.Contracts;

/// <summary>
///     STUB contract for the GGUF model store. <strong>Lane B owns the final name and shape</strong> (HF GGUF
///     discovery + on-disk store + disk guard). Lane A only consumes two operations from it:
///     <list type="number">
///         <item>the resolved local model-file path for a model name (to launch <c>llama-server -m &lt;path&gt;</c>);</item>
///         <item>installed-model enumeration (for <see cref="ILocalModelProvider.ListModelsAsync" />).</item>
///     </list>
///     Until Lane B lands, a fake (<see cref="FixedPathGgufModelStore" />) returns a fixed path so Lane A's
///     supervisor/provider tests run with no real GGUF download.
/// </summary>
public interface IGgufModelStore
{
    /// <summary>
    ///     Resolves the absolute path to the local GGUF file backing <paramref name="modelName" />, or
    ///     <see langword="null" /> when the model is not installed.
    /// </summary>
    Task<string?> ResolveModelFilePathAsync(string modelName, CancellationToken ct);

    /// <summary>Enumerates the installed GGUF models as normalized host-agent descriptors.</summary>
    Task<IReadOnlyList<LocalModelDescriptor>> ListInstalledModelsAsync(CancellationToken ct);
}
