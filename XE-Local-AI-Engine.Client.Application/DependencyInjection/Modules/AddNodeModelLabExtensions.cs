namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Registers Model Lab: the checkpoint preflight, the experiment service, its arm runner and its startup reaper.</summary>
/// <remarks>
///     <strong>Caller contract:</strong> invoke before <c>AddNodeTrainingRuns</c>. The job shares the training runtime, the event buffer
///     and the process reaper, and its hosted services start ahead of the training queue's.
/// </remarks>
internal static class AddNodeModelLabExtensions
{
    /// <summary>One prompt on a loaded arm server. Generous beside the seconds it takes, because a timeout fails the arm and a wedged server has no other bound here.</summary>
    private static readonly TimeSpan ArmRequestTimeout = TimeSpan.FromMinutes(5);

    public static IHostApplicationBuilder AddNodeModelLab(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddScoped<IModelLabExperimentStore, ModelLabExperimentStore>();

        // Stateless over three singletons: the checkpoint store, the device audit and the GGUF inspection service.
        builder.Services.AddSingleton<ModelLabCheckpointPreflightService>();

        // A singleton for the same reason the export service is one: its job outlives the request that started it.
        builder.Services.AddOptions<ModelLabJobOptions>();
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental; used deliberately: Aspire's standard pipeline times a generation out at seconds and retries it.
        builder.Services.AddHttpClient(ModelLabArmRunner.HttpClientName, static client => client.Timeout = ArmRequestTimeout)
               .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
               {
                   // Loopback only: a proxy from the environment must not carry an arm's request, and its server has no reason to redirect one.
                   AllowAutoRedirect = false,
                   UseProxy = false
               })
               .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<ModelLabArmRunner>();
        builder.Services.AddSingleton<ModelLabExperimentService>();
        builder.Services.AddSingleton<IModelLabActiveJob>(static provider => provider.GetRequiredService<ModelLabExperimentService>());

        // A singleton for its single-flight flag. Its jobs run inside the request and share the work root the startup reaper sweeps.
        builder.Services.AddSingleton<ModelLabPyTorchSourceService>();
        builder.Services.AddHostedService<ModelLabStartupReaper>();
        builder.Services.AddHostedService(static provider => provider.GetRequiredService<ModelLabExperimentService>());
        return builder;
    }
}
