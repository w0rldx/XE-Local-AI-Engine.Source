namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Containers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.Containers.Implementation;

/// <summary>
///     Registers the application-container runtime layer (ADR 0010): its options plus validation, the runtime factory
///     and the resolver every application-container operation passes through.
///     <para>
///         A module of its own rather than an addition to <c>AddNodeContainerSandbox</c>, even though both end up
///         talking to the same daemon. Development Mode's sandbox and a user-managed application have opposite time
///         budgets and different failure prose, and the one thing they genuinely share — the daemon attestation — is
///         registered by that module and taken from the container here.
///     </para>
///     <para>
///         Everything registered here is inert until an application instance resolves a runtime: the resolver probes
///         no daemon at startup, so a node with no Docker installed pays nothing for these registrations.
///     </para>
/// </summary>
internal static class AddNodeContainerRuntimeExtensions
{
    public static IHostApplicationBuilder AddNodeContainerRuntime(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // No ValidateDataAnnotations: the options record carries no annotations, and the bounds that matter are
        // cross-field ones the validator expresses instead.
        builder.Services.AddOptions<ContainerRuntimeOptions>()
               .Bind(configuration.GetSection(ContainerRuntimeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ContainerRuntimeOptions>, ContainerRuntimeOptionsValidator>();

        builder.Services.AddSingleton<IContainerRuntimeFactory, DockerContainerRuntimeFactory>();
        builder.Services.AddSingleton<IContainerRuntimeResolver, ContainerRuntimeResolver>();

        // The container bridge (the one deliberately non-loopback listener). Registered here, with the rest of the
        // runtime layer, and NOT with External Apps: the bridge must never depend on that feature, only be opened
        // alongside it. The annotation on the port is the only bound the options carry.
        builder.Services.AddOptions<ContainerBridgeOptions>()
               .BindConfiguration(ContainerBridgeOptions.SectionName)
               .ValidateDataAnnotations()
               .ValidateOnStart();

        // TryAdd, and the default says "this node has no bridge". The composition root resolves the real listener
        // during host construction and registers it BEFORE this module runs, so its value wins; the default is what
        // keeps the module standing up on its own, for a host that composes the services without opening a listener.
        builder.Services.TryAddSingleton(new ContainerBridgeEndpointSource(endpoint: null));

        // ONE watcher in two roles: the hosted service that keeps this computer's own addresses current, and the
        // object the peer guard asks. A second instance would answer from a set nothing refreshes.
        builder.Services.AddSingleton<ContainerBridgeAddressWatcher>();
        builder.Services.AddHostedService(static services => services.GetRequiredService<ContainerBridgeAddressWatcher>());

        // IMiddleware, so each is resolved from the container rather than closed over at pipeline-build time. The
        // peer guard is a singleton because it asks only the watcher; the token middleware is SCOPED because its
        // verifier reads the node database through a scoped store, and a singleton would capture one context for the
        // life of the process.
        builder.Services.AddSingleton<ContainerBridgePeerGuardMiddleware>();
        builder.Services.AddScoped<ContainerBridgeTokenMiddleware>();

        return builder;
    }
}
