namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;
using XE_Local_AI_Engine.Client.Services.Connection;
using XE_Local_AI_Engine.Client.Services.DeadLetter;
using XE_Local_AI_Engine.Client.Services.DeadLetter.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope;
using XE_Local_AI_Engine.Client.Services.Invocation.Envelope.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage;
using XE_Local_AI_Engine.Client.Services.Invocation.RuntimePackage.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Client.Services.Validation.Implementation;

internal static class AddNodeInvocationExtensions
{
    public static IHostApplicationBuilder AddNodeInvocation(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        builder.Services.AddSingleton(sp => new Lazy<IHubMessageSender>(() => sp.GetRequiredService<IHubMessageSender>()));
        builder.Services.AddSingleton(sp => new Lazy<IWorkerEventDispatcher>(() => sp.GetRequiredService<IWorkerEventDispatcher>()));
        builder.Services.AddSingleton<ModelNameValidator>();
        builder.Services.AddSingleton<IRuntimePackageValidator, RuntimePackageValidator>();
        builder.Services.AddSingleton<INodeAeadCipher, AesGcmNodeAeadCipher>();
        builder.Services.AddSingleton<IEnvelopeCryptoService, EnvelopeCryptoService>();
        builder.Services.AddSingleton<IRuntimePackageEnvelopeAssembler, RuntimePackageEnvelopeAssembler>();
        builder.Services.AddSingleton<IInvocationRunner, InvocationRunner>();
        builder.Services.AddSingleton<IInvocationHistory, InvocationHistory>();
        builder.Services.AddSingleton<IWorkerEventDispatcher, WorkerEventDispatcher>();
        builder.Services.AddSingleton<ModelCapabilityProber>();
        builder.Services.AddSingleton<CapabilityReportComposer>();
        builder.Services.AddSingleton<ICapabilityReporter, CapabilityReporter>();
        builder.Services.AddSingleton(sp => new Lazy<ICapabilityReporter>(() => sp.GetRequiredService<ICapabilityReporter>()));
        builder.Services.AddSingleton<IDeadLetterStore, FileDeadLetterStore>();
        builder.Services.AddSingleton<INodeSqliteKeyHolder, NodeSqliteKeyHolder>();
        builder.Services.AddSingleton<NodeEncryptionSaveChangesInterceptor>();
        builder.Services.AddSingleton<NodeEncryptionMaterializationInterceptor>();
        builder.Services.AddScoped<INodeRetentionStore, NodeRetentionStore>();

        return builder;
    }
}
