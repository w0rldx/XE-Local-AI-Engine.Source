namespace XE_Local_AI_Engine.Providers.LlamaServer;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
///     DI wiring for the llama-server local-model provider stack (binary manager + GPU probe/selector + the
///     supervisor + provider seams). Mirrors the <c>AddOllamaLocalModelProvider</c> registration shape.
/// </summary>
public static class LlamaServerServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the Lane A model-runtime-core services that exist today: the GPU vendor probe, the OS-aware
    ///     variant selector, and the llama.cpp binary manager. The supervisor (<see cref="ILlamaServerProcessSupervisor" />)
    ///     and the provider (<c>ILocalModelProvider</c> for <c>llamacpp</c>) are registered by the later Lane A tasks
    ///     (T2/T3) and wired into the multi-provider resolver by T4.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>Caller contract:</strong> the consuming application must register a named/typed
    ///         <see cref="System.Net.Http.HttpClient" /> for binary downloads via <c>AddHttpClient</c> (the
    ///         <c>Microsoft.Extensions.Http</c> package is referenced by the Application host, not this provider
    ///         project) and supply an <see cref="IGgufModelStore" /> — Lane B's real store, or
    ///         <see cref="FixedPathGgufModelStore" /> until Lane B lands.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddLlamaServerLocalModelProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IGpuVendorProbe, ProcessGpuVendorProbe>();
        services.TryAddSingleton<IGpuVariantSelector, GpuVariantSelector>();
        services.TryAddSingleton<ILlamaCppBinaryManager>(static sp =>
            new LlamaCppBinaryManager(sp.GetRequiredService<HttpClient>()));

        // Options default here so the supervisor is resolvable today; T4 overrides them from node config.
        services.TryAddSingleton(new LlamaServerSupervisorOptions());
        services.TryAddSingleton(new LlamaServerExternalEndpointOptions());

        // Process-supervision seams (T2): the OS-aware launcher (tree-kill) + the /health readiness probe.
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

        // SEAM: the llamacpp ILocalModelProvider (T3) registers here once implemented; T4 adds it to the resolver.
        return services;
    }
}
