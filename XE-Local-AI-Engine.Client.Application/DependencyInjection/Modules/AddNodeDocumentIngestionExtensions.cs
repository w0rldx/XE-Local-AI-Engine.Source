namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

internal static class AddNodeDocumentIngestionExtensions
{
    public static IHostApplicationBuilder AddNodeDocumentIngestion(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Pure-managed document text extraction (Lane B). Stateless and thread-safe, so a singleton is correct.
        builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

        // Durable per-conversation uploaded-file store. Singleton: it opens its own DbContext scope per operation and
        // depends only on singletons (data directory, sqlite key holder, time provider), so it can be injected into the
        // singleton chat persistence service that hooks conversation-delete disk cleanup.
        builder.Services.AddSingleton<IConversationUploadedFileStore, ConversationUploadedFileStore>();

        return builder;
    }
}
