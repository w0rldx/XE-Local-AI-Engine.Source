namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;

/// <summary>
///     Registers the Development Mode container sandbox: options plus validation, the Docker daemon client factory,
///     the daemon-attestation store, and the preflight service the capability endpoint reads.
/// </summary>
/// <remarks>
///     A module of its own rather than part of <c>AddNodeAgentHome</c>: provider selection is per feature, Development
///     Mode taking the container provider while AgentHome and Coder stay on the process provider. Nothing here binds
///     <c>DockerSandboxRuntimeProvider</c> to a role — the lazy role factories come from <c>AddNodeAgentHome</c>, which
///     runs BEFORE this module, and <c>SandboxProviderSelector.ResolveDevelopment</c> reaches it only when named by
///     <c>Development:Sandbox:Provider</c>. The startup sweep, counterpart to <c>SandboxOrphanReaper</c>, is here too.
/// </remarks>
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

        // Registered as a concrete type so the role factory in AddNodeAgentHome resolves THIS instance, making it a DI singleton
        // rather than a fresh one per resolution. It implements IDevelopmentSandboxRuntimeProvider only, so the agent role cannot take it.
        builder.Services.AddSingleton<DockerSandboxRuntimeProvider>();

        // Registered unconditionally and gated at run time: provider selection is a configuration-bound factory nothing here can
        // evaluate (unset means "follow the agent role"), so the sweeper resolves the selector and never touches the daemon unopted.
        builder.Services.AddHostedService<DockerSandboxOrphanSweeper>();

        return builder;
    }
}
