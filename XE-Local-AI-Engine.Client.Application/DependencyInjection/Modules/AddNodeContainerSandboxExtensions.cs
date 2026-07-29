namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>
///     Registers the Development Mode container sandbox: options plus validation, the Docker daemon client factory,
///     the D10 attestation store, and the preflight service the capability endpoint reads.
///     <para>
///         A module of its own rather than an addition to <c>AddNodeAgentHome</c>, because under decision D2 provider
///         selection is per feature: Development Mode gets the container provider while AgentHome and Coder stay on
///         the process provider. Registering the container pieces alongside AgentHome's would imply a coupling that
///         decision explicitly rejects.
///     </para>
///     <para>
///         Note what is NOT here: <c>SandboxProviderSelector</c> is untouched, so nothing resolves
///         <c>DockerSandboxRuntimeProvider</c> as the AgentHome sandbox. Wiring Development Mode over to it is
///         per-feature selection across all thirteen injection sites, and it is a separate change.
///     </para>
/// </summary>
internal static class AddNodeContainerSandboxExtensions
{
    public static IHostApplicationBuilder AddNodeContainerSandbox(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<ContainerSandboxOptions>()
               .Bind(configuration.GetSection(ContainerSandboxOptions.SectionName))
               .ValidateDataAnnotations()
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ContainerSandboxOptions>, ContainerSandboxOptionsValidator>();

        builder.Services.AddSingleton<IDockerRuntimeClientFactory, DockerDotNetRuntimeClientFactory>();
        builder.Services.AddSingleton<IDockerDaemonAttestationStore, DockerDaemonAttestationStore>();
        builder.Services.AddSingleton<IDockerDaemonPreflightService, DockerDaemonPreflightService>();

        // Registered as a concrete type, not as ISandboxRuntimeProvider. Binding it to the interface would make it a
        // candidate for AgentHome's resolution, which D2 places on the process provider — and a container requirement
        // silently acquired by a feature that does not need one is exactly what per-feature selection prevents.
        builder.Services.AddSingleton<DockerSandboxRuntimeProvider>();

        return builder;
    }
}
