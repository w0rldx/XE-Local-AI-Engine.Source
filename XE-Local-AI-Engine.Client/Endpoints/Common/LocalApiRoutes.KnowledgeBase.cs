namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Local knowledge-base (offline RAG) document management, search, reindex, and hub routes.
    /// </summary>
    /// <remarks>
    ///     Every route is Operator-gated; none is <c>IDesktopOnlyEndpoint</c>, so all survive the headless OpenAPI
    ///     regen. The document collection route carries the multipart upload (POST) and the list (GET); the individual
    ///     document resource is GET/DELETE with the server-generated <c>{documentId}</c> (never a client-supplied path).
    /// </remarks>
    public static class KnowledgeBase
    {
        // Document collection (POST multipart upload, GET list) and the individual document resource (GET detail, DELETE).
        public const string Documents = "knowledge-base/documents";
        public const string DocumentById = "knowledge-base/documents/{documentId}";

        // Re-run the ingestion pipeline for one document. The literal "reindex" segment follows the id so it cannot be
        // parsed as one.
        public const string DocumentReindex = "knowledge-base/documents/{documentId}/reindex";

        // Corpus-wide reindex of every stale-model document. A distinct top-level literal keeps it off the document-id
        // surface.
        public const string Reindex = "knowledge-base/reindex";

        // Hybrid retrieval over the indexed corpus (POST body: query + options).
        public const string Search = "knowledge-base/search";

        // Imports supported tracked/unignored files from an already-registered local Development repository.
        public const string RepositoryImport = "knowledge-base/repositories/import";

        // One-click download of the recommended cross-encoder reranker so an operator can enable KB reranking without
        // hunting for a repo/quant. Body-less POST; a distinct "reranker" literal keeps it off the document-id surface.
        public const string RerankerDownloadRecommended = "knowledge-base/reranker/download-recommended";

        // One-click download of the recommended embedding model. Same shape as the reranker route above, but this one is
        // load-bearing rather than optional: with no embedding model installed the knowledge base cannot index anything.
        public const string EmbeddingDownloadRecommended = "knowledge-base/embedding/download-recommended";

        // SignalR push hub for indexing status changes: a full path (mapped via MapHub, not the FastEndpoints prefix), mirroring the other local hubs. Each push carries the
        // sanitized document id + status; Operator-gated because subscribers see which documents are being indexed.
        public const string Hub = "/api/local/v1/knowledge-base/hub";
    }
}
