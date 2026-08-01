namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint tests for the knowledge upload admission decision: a deduplicated upload whose existing document is
///     still Pending (never ingested — e.g. a prior upload the full queue rejected with 503) RETRIES admission instead of
///     reporting success while the document stays stranded; a dedupe hit that is already Indexed is left alone; and a
///     fresh upload the bounded queue cannot admit returns the retryable 503 + Retry-After busy response.
/// </summary>
public sealed class KnowledgeUploadEndpointTests
{
    private const string UploadRoute = "/api/local/v1/knowledge-base/documents";

    [Test]
    public async Task Upload_DedupeHitStillPending_ReEnqueuesForIngestion()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);

        await using var factory = CreateFactory(dispatcher, wasInserted: false, status: KnowledgeDocumentStatus.Pending, documentId);
        using var client = factory.CreateClient();

        using var response = await PostFileAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        // A stranded (never-ingested) dedupe hit must be re-admitted, not silently skipped.
        AssertEx.Contains(dispatcher.Enqueued, documentId);
    }

    [Test]
    public async Task Upload_DedupeHitAlreadyIndexed_ReturnsSuccessWithoutReEnqueue()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);

        await using var factory = CreateFactory(dispatcher, wasInserted: false, status: KnowledgeDocumentStatus.Indexed, documentId);
        using var client = factory.CreateClient();

        using var response = await PostFileAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        // An already-indexed dedupe hit is done: re-running the pipeline would be wasted work.
        AssertEx.Empty(dispatcher.Enqueued);
    }

    [Test]
    public async Task Upload_FreshDocumentButQueueFull_Returns503WithRetryAfter()
    {
        var documentId = Guid.NewGuid();
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.QueueFull);

        await using var factory = CreateFactory(dispatcher, wasInserted: true, status: KnowledgeDocumentStatus.Pending, documentId);
        using var client = factory.CreateClient();

        using var response = await PostFileAsync(factory, client).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.NotNull(response.Headers.RetryAfter, "A queue-full upload must advertise Retry-After so the client retries.");
    }

    private static TestingWebAppFactory CreateFactory(IKnowledgeIngestionDispatcher dispatcher,
        bool wasInserted,
        KnowledgeDocumentStatus status,
        Guid documentId)
    {
        return new TestingWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
                blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult(new KnowledgeDocumentAddResult(documentId, wasInserted)));

                var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
                catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<KnowledgeDocumentStatus?>(status));

                // Lifetimes mirror production (blob store singleton, catalog scoped, dispatcher singleton) so no captive
                // dependency is introduced by the override.
                services.RemoveAll<IKnowledgeDocumentBlobStore>();
                services.AddSingleton(blobStore);
                services.RemoveAll<IKnowledgeDocumentCatalogService>();
                services.AddScoped(_ => catalog);
                services.RemoveAll<IKnowledgeIngestionDispatcher>();
                services.AddSingleton(dispatcher);
            }
        };
    }

    private static async Task<HttpResponseMessage> PostFileAsync(TestingWebAppFactory factory, HttpClient client)
    {
#pragma warning disable CA2000 // MultipartFormDataContent owns the part content and disposes it when the `using content` scope ends.
        using var content = new MultipartFormDataContent
        {
            {
                new ByteArrayContent(Encoding.UTF8.GetBytes("hello knowledge base")), "file", "doc.txt"
            }
        };
#pragma warning restore CA2000

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadRoute)
        {
            Content = content
        };
        factory.AddNodeBearerToken(request);

        return await client.SendAsync(request).ConfigureAwait(false);
    }

    // Hand-written fake for the ValueTask-returning dispatcher: records enqueued ids and returns a fixed admission result,
    // avoiding the ValueTask/analyzer friction of stubbing it through NSubstitute.
    private sealed class RecordingDispatcher(KnowledgeIngestionEnqueueResult result) : IKnowledgeIngestionDispatcher
    {
        public List<Guid> Enqueued { get; } = [];

        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Enqueued.Add(documentId);
            return ValueTask.FromResult(result);
        }
    }
}
