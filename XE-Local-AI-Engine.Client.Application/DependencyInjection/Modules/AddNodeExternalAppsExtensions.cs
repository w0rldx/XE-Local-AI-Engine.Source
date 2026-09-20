namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.ExternalApps;
using XE_Local_AI_Engine.Client.Services.ExternalApps.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Registers the External Apps runtime (ADR 0010): the instance store, the admission gates, the service every
///     command goes through, and the two hosted services that keep the rows honest across a restart.
/// </summary>
/// <remarks>
///     <b>Registration is not the feature flag.</b> Everything here registers whether or not <c>ExternalApps:Enabled</c>
///     is set, so the composition root has one shape; the flag is checked inside each entry point, nothing here is
///     resolved on the startup path, and both hosted services return immediately while the flag is off, so a node with
///     no Docker pays nothing. Last in the container-feature order — <c>AddNodeContainerSandbox</c> (daemon attestation)
///     → <c>AddNodeContainerRuntime</c> (resolver) → <c>AddNodeExternalAppsCatalog</c> (manifests) → this.
/// </remarks>
internal static class AddNodeExternalAppsExtensions
{
    public static IHostApplicationBuilder AddNodeExternalApps(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // The annotations carry the four ranges; the validator carries the two free-form members no annotation can express.
        // ValidateOnStart, so a misspelt instance root is a startup error rather than a storage failure on somebody's first install.
        builder.Services.AddOptions<ExternalAppsOptions>()
               .Bind(configuration.GetSection(ExternalAppsOptions.SectionName))
               .ValidateDataAnnotations()
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ExternalAppsOptions>, ExternalAppsOptionsValidator>();

        // Scoped, like every other store: it holds the node database context.
        builder.Services.AddScoped<IExternalAppInstanceStore, ExternalAppInstanceStore>();

        // The container bridge's token seam, implemented HERE and consumed there — the bridge must never reference this feature, and
        // an architecture test holds that direction. Scoped because it reads the instance store, like the middleware calling it.
        builder.Services.AddScoped<IContainerBridgeTokenVerifier, ExternalAppBridgeTokenVerifier>();

        // Singletons, each for a reason that would break otherwise: the storage layout and resource gate are stateless readers, the
        // instance gate IS the per-instance mutual exclusion, and the operation runner owns the in-flight entries and their cancellation.
        builder.Services.AddSingleton<ExternalAppStorageLayout>();

        // The free-space probe the resource gate measures with. TryAdd because the model-runtime module registers the same
        // implementation for the models directory; registered here too, so this module stands up without it and can still measure a disk.
        builder.Services.TryAddSingleton<IFreeSpaceProbe, DriveInfoFreeSpaceProbe>();
        builder.Services.AddSingleton<ExternalAppResourceGate>();
        builder.Services.AddSingleton<ExternalAppInstanceGate>();
        builder.Services.AddSingleton<ExternalAppOperationRunner>();

        // A singleton over IServiceScopeFactory rather than a scoped service holding a store: its pipelines outlive the request that
        // admitted them, and a request-scoped DbContext disposed by a browser navigating away is one an install still swaps against.
        builder.Services.AddSingleton<ExternalAppService>();
        builder.Services.AddSingleton<IExternalAppService>(static services => services.GetRequiredService<ExternalAppService>());

        // TryAdd: S3's hub-backed publisher supersedes this wherever the hub is mapped, and a host that composes the
        // services without the API surface still gets a publisher rather than a missing dependency.
        builder.Services.TryAddSingleton<IExternalAppEventPublisher, NoOpExternalAppEventPublisher>();

        // ONE reconciler instance in three roles: the hosted service that runs the boot pass, and the interface the operator-facing
        // runtime refresh re-runs it through. Registering it twice would give the refresh a second object with its own view.
        builder.Services.AddSingleton<ExternalAppStartupReconciler>();
        builder.Services.AddSingleton<IExternalAppStartupReconciler>(static services => services.GetRequiredService<ExternalAppStartupReconciler>());
        builder.Services.AddHostedService(static services => services.GetRequiredService<ExternalAppStartupReconciler>());

        // After the reconciler, because hosted services start in registration order: the observer must not report an
        // instance stopped while the boot pass is still deciding what that instance is.
        builder.Services.AddHostedService<ExternalAppStateObserver>();

        return builder;
    }
}
