namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     Registers the external OpenAI-compatible provider's application-layer half: the encrypted connection store, the
///     registry projected from it, the tri-state trust resolver every policy gate consults, the write path with its
///     cross-store side effects, and the startup reconciliation pass.
/// </summary>
/// <remarks>
///     <strong>Caller contract:</strong> invoke BEFORE <c>AddNodeModelRuntime</c>. That module registers the external
///     multiplexer provider only when an <see cref="IExternalProviderRegistry" /> is already in the container — the
///     guard that keeps a node with no external store from advertising a provider that reports zero models — and it
///     reads the collection as built so far, so registering the registry afterwards would silently ship a node on which
///     no <c>ext:</c> id can route.
/// </remarks>
internal static class AddNodeExternalProvidersExtensions
{
    public static IHostApplicationBuilder AddNodeExternalProviders(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Singleton: it owns the file lock serializing every read-modify-write of the encrypted store, and the
        // compare-and-swap discipline is only meaningful if one instance holds it.
        builder.Services.AddSingleton<ExternalProviderStore>();
        builder.Services.AddSingleton<IExternalProviderStore>(static sp => sp.GetRequiredService<ExternalProviderStore>());

        // Singleton, and the SAME instance behind both faces: the read contract the provider consumes and the
        // cache-control surface the write path drives. Two instances would mean a save invalidating a snapshot that
        // the chat path never reads from.
        builder.Services.AddSingleton<ExternalProviderRegistry>();
        builder.Services.AddSingleton<IExternalProviderRegistry>(static sp => sp.GetRequiredService<ExternalProviderRegistry>());
        builder.Services.AddSingleton<IExternalProviderRegistryCache>(static sp => sp.GetRequiredService<ExternalProviderRegistry>());

        // Singleton: consumed by singleton policy seams (the tool offer, the runtime chat client) and holding no
        // per-request state of its own.
        builder.Services.AddSingleton<IModelTrustResolver, ModelTrustResolver>();

        // Scoped: the reconciliation pass reads the provider map through the scoped coordinated store, exactly like
        // the Ollama backfill coordinator it mirrors.
        builder.Services.AddScoped<IExternalProviderReconciler, ExternalProviderReconciler>();
        builder.Services.AddScoped<IExternalProviderAdministrationService, ExternalProviderAdministrationService>();

        builder.Services.AddHostedService<ExternalProviderStartupReconciler>();

        return builder;
    }
}
