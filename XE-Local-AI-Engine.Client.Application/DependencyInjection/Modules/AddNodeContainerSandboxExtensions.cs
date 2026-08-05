namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>
///     Registers the Development Mode container sandbox: options plus validation, the Docker daemon client factory,
///     the daemon-attestation store, and the preflight service the capability endpoint reads.
///     <para>
///         A module of its own rather than an addition to <c>AddNodeAgentHome</c>, because provider
///         selection is per feature: Development Mode gets the container provider while AgentHome and Coder stay on
///         the process provider. Registering the container pieces alongside AgentHome's would imply a coupling this
///         design explicitly rejects.
///     </para>
///     <para>
///         Note what is NOT here: no binding of <c>DockerSandboxRuntimeProvider</c> to a role. The two role
///         interfaces are registered by <c>AddNodeAgentHome</c>, which runs BEFORE this module — safely, because they
///         are lazy factory delegates. <c>SandboxProviderSelector.ResolveDevelopment</c> reaches the concrete type
///         registered below only when <c>Development:Sandbox:Provider</c> names it; nothing here can hand it to
///         AgentHome, which no longer needs a convention to guarantee because the type system does.
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

        // Registered as a concrete type. The role factory in AddNodeAgentHome resolves it from here, which is what
        // makes it a DI singleton rather than a fresh instance per resolution. It could not be bound to the agent role
        // even deliberately: it implements IDevelopmentSandboxRuntimeProvider only, so a container requirement
        // silently acquired by a feature that does not need one is a compile error, not a review catch.
        builder.Services.AddSingleton<DockerSandboxRuntimeProvider>();

        return builder;
    }
}
