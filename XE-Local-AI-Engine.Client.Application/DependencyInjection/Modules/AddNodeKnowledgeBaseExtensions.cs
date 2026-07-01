namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.Knowledge;

internal static class AddNodeKnowledgeBaseExtensions
{
    public static IHostApplicationBuilder AddNodeKnowledgeBase(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Durable knowledge-base document store. Singleton: it opens its own DbContext scope per operation and depends
        // only on singletons (data directory, sqlite key holder, time provider), mirroring the conversation uploaded-file
        // store, so it can be injected into the singleton ingestion/cleanup surfaces that reach it.
        builder.Services.AddSingleton<IKnowledgeDocumentBlobStore, KnowledgeDocumentBlobStore>();

        return builder;
    }
}
