namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Configuration.Validation;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Compute.Implementation;

/// <summary>
///     DI wiring for the sandboxed <c>run_python</c> compute tool. Registered unconditionally: the node kill-switch
///     (off by default) is enforced in the gateway rather than by skipping registration, so the tool answers a disabled
///     node with a clear sentence instead of vanishing from the resolution seam and surfacing as an unknown-tool error.
/// </summary>
/// <remarks>
///     Depends on the sandbox provider roles registered by <see cref="AddNodeAgentHomeExtensions.AddNodeAgentHome" />
///     (<c>IAgentSandboxRuntimeProvider</c>, and the AgentHome identity provider the attach key is built from), so it
///     must run after that module.
/// </remarks>
internal static class AddNodeComputeExtensions
{
    public static IHostApplicationBuilder AddNodeCompute(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddOptions<ComputeOptions>()
               .Bind(configuration.GetSection(ComputeOptions.SectionName))
               .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ComputeOptions>, ComputeOptionsValidator>();

        // Named client for the pinned uv download, matching how the training runtime takes its own.
        builder.Services.AddHttpClient(nameof(ComputePythonEnvironment));
        builder.Services.AddSingleton<IComputePythonEnvironment>(static sp =>
            new ComputePythonEnvironment(sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(ComputePythonEnvironment)),
                sp.GetRequiredService<ILogger<ComputePythonEnvironment>>()));
        builder.Services.AddSingleton<IComputeToolGateway, ComputeToolGateway>();
        builder.Services.AddSingleton<IClientLocalToolHandler, RunPythonToolHandler>();

        return builder;
    }
}
