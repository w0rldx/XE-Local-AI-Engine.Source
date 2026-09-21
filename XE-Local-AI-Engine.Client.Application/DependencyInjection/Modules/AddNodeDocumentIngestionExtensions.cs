namespace XE_Local_AI_Engine.Client.DependencyInjection.Modules;

using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;

internal static class AddNodeDocumentIngestionExtensions
{
    public static IHostApplicationBuilder AddNodeDocumentIngestion(this IHostApplicationBuilder builder, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configuration);

        // Pure-managed document text extraction. Stateless and thread-safe, so a singleton is correct.
        builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();

        // Process-wide admission gate that bounds concurrent synchronous (in-request) conversation extractions so many
        // simultaneous uploads cannot aggregate to an out-of-memory condition. Singleton — the semaphore is shared.
        builder.Services.AddSingleton<IDocumentExtractionAdmissionGate, DocumentExtractionAdmissionGate>();

        // Persistence boundary for the metadata rows, including the display-name encryption. Scoped: it owns a
        // NodeChatDbContext per operation.
        builder.Services.AddScoped<IConversationUploadedFileRowStore, ConversationUploadedFileRowStore>();

        // Durable per-conversation uploaded-file store: the encrypted on-disk bytes plus the staging snapshot. Singleton: it opens a scope per
        // row operation and depends only on singletons, so the singleton chat persistence service can hook delete cleanup.
        builder.Services.AddSingleton<IConversationUploadedFileStore, ConversationUploadedFileStore>();

        // Gate → buffer → extract → persist orchestration behind the conversation upload endpoint. Singleton: stateless
        // apart from the three singletons above.
        builder.Services.AddSingleton<IConversationUploadIngestor, ConversationUploadIngestor>();

        return builder;
    }
}
