namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Containers;
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

        return builder;
    }
}
