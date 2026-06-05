namespace XE_Local_AI_Engine.Client.Services.ModelFit;

using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Services.ModelFit.Fake;
using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Configuration-bound resolver for the <see cref="IModelFitUtilityRunner" />. Registered once as a singleton so a
///     runner change requires a restart. It reuses the SAME provider-name config key the sandbox uses
///     (<c>AgentHome:Sandbox:Provider</c> via <see cref="SandboxOptions" />): both ride the HostAgent local-container
///     path, so selecting <c>"local-container"</c> for the sandbox also selects the HostAgent-backed model-fit runner.
///     The MVP default is the deterministic fake.
/// </summary>
internal static class ModelFitUtilityRunnerSelector
{
    public const string LocalContainerRunner = "local-container";

    public static IModelFitUtilityRunner Resolve(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var providerName = services.GetRequiredService<IOptions<SandboxOptions>>().Value.Provider;
        return providerName switch
        {
            LocalContainerRunner => ActivatorUtilities.CreateInstance<GrpcModelFitUtilityRunner>(services),
            FakeModelFitUtilityRunner.Name => ActivatorUtilities.CreateInstance<FakeModelFitUtilityRunner>(services),
            // Any other configured provider (incl. the default fake) gets the deterministic fake runner: a model-fit run
            // is never silently routed to an unknown/unimplemented runner.
            _ => ActivatorUtilities.CreateInstance<FakeModelFitUtilityRunner>(services)
        };
    }
}
