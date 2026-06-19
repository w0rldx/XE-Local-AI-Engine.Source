namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     DI wiring for the llama-server local-model provider stack (binary manager + GPU probe/selector + the
///     supervisor + provider seams). Mirrors the <c>AddOllamaLocalModelProvider</c> registration shape.
/// </summary>
public static class LlamaServerServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the model-runtime-core services: the GPU vendor probe, the OS-aware
    ///     variant selector, and the llama.cpp binary manager, plus the supervisor
    ///     (<see cref="ILlamaServerProcessSupervisor" />) and the provider (<c>ILocalModelProvider</c> for
    ///     <c>llamacpp</c>) wired into the multi-provider resolver.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Caller contract:</strong> the consuming application must register a named/typed
    ///         <see cref="System.Net.Http.HttpClient" /> for binary downloads via <c>AddHttpClient</c> (the
    ///         <c>Microsoft.Extensions.Http</c> package is referenced by the Application host, not this provider
    ///         project) and supply an <see cref="IGgufModelStore" /> — the Hugging Face GGUF store
    ///         (<c>AddHuggingFaceGgufStore</c>).
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddLlamaServerLocalModelProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGpuVendorProbe, ProcessGpuVendorProbe>();
        services.TryAddSingleton<IGpuVariantSelector, GpuVariantSelector>();
        services.TryAddSingleton<ILlamaCppBinaryManager>(static sp =>
            new LlamaCppBinaryManager(sp.GetRequiredService<HttpClient>()));

        // Options default here so the supervisor is resolvable; the host overrides them from node config.
        services.TryAddSingleton(new LlamaServerSupervisorOptions());
        services.TryAddSingleton(new LlamaServerExternalEndpointOptions());

        // Process-supervision seams: the OS-aware launcher (tree-kill) + the /health readiness probe.
        services.TryAddSingleton<ILlamaServerProcessLauncher, LlamaServerProcessLauncher>();
        services.TryAddSingleton<ILlamaServerHealthProbe>(static sp =>
            new LlamaServerHealthProbe(sp.GetRequiredService<HttpClient>()));

        // The supervisor owns all llama-server child processes for the node — strictly one singleton. Built via an
        // explicit factory because its ctor is internal (it takes the internal launcher/health-probe seams).
        services.TryAddSingleton(static sp => new LlamaServerProcessSupervisor(
            sp.GetRequiredService<ILlamaCppBinaryManager>(),
            sp.GetRequiredService<IGpuVariantSelector>(),
            sp.GetRequiredService<IGgufModelStore>(),
            sp.GetRequiredService<ILlamaServerProcessLauncher>(),
            sp.GetRequiredService<ILlamaServerHealthProbe>(),
            sp.GetRequiredService<LlamaServerSupervisorOptions>(),
            sp.GetRequiredService<LlamaServerExternalEndpointOptions>(),
            sp.GetService<TimeProvider>()));
        services.TryAddSingleton<ILlamaServerProcessSupervisor>(static sp =>
            sp.GetRequiredService<LlamaServerProcessSupervisor>());

        // SEAM: the llamacpp ILocalModelProvider. Registered over the supervisor + the caller-supplied
        // IGgufModelStore (the Hugging Face GGUF store). Added to the
        // ILocalModelProvider set alongside Ollama; the per-model→provider resolver dispatches across both
        // registrations. Singleton — it holds no per-request state; the deferred chat/embedding
        // clients it hands out own the cold-start.
        services.TryAddSingleton<LlamaServerLocalModelProvider>(static sp =>
            new LlamaServerLocalModelProvider(
                sp.GetRequiredService<ILlamaServerProcessSupervisor>(),
                sp.GetRequiredService<IGgufModelStore>()));
        services.AddSingleton<ILocalModelProvider>(static sp =>
            sp.GetRequiredService<LlamaServerLocalModelProvider>());

        return services;
    }
}
