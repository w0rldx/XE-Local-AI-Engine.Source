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
[Category(TestCategories.Integration)]
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

        using var response = await PostFileAsync(factory, client);

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

        using var response = await PostFileAsync(factory, client);

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

        using var response = await PostFileAsync(factory, client);

        AssertEx.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertEx.NotNull(response.Headers.RetryAfter, "A queue-full upload must advertise Retry-After so the client retries.");
    }

    [Test]
    public async Task Upload_InvalidCollectionIdOnDedupeHit_Returns400AndNeverReachesTheStore()
    {
        // REGRESSION (live QA F-19): "bad collection!" answered 200 deduplicated:true because the bytes already existed in
        // DEFAULT. The blob store answers a dedupe hit here, so a 200 would prove the id bypassed validation.
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        await using var factory = CreateFactory(dispatcher, wasInserted: false, status: KnowledgeDocumentStatus.Indexed, Guid.NewGuid());
        using var client = factory.CreateClient();

        using var response = await PostFileAsync(factory, client, collectionId: "bad collection!");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var blobStore = factory.Services.GetRequiredService<IKnowledgeDocumentBlobStore>();
        await blobStore.DidNotReceive().AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_InvalidCollectionIdInQueryString_Returns400AndNeverReachesTheStore()
    {
        // The same id sent as a query parameter must not be ignored and silently deduped into DEFAULT either.
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        await using var factory = CreateFactory(dispatcher, wasInserted: false, status: KnowledgeDocumentStatus.Indexed, Guid.NewGuid());
        using var client = factory.CreateClient();

        using var response = await PostFileAsync(factory, client, query: "?collectionId=bad%20collection!");

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var blobStore = factory.Services.GetRequiredService<IKnowledgeDocumentBlobStore>();
        await blobStore.DidNotReceive().AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Upload_ValidCollectionIdFormField_ReachesTheStoreNormalized()
    {
        // The multipart form field must bind: an unbound field silently falls back to DEFAULT and dedupes there.
        var dispatcher = new RecordingDispatcher(KnowledgeIngestionEnqueueResult.Accepted);
        await using var factory = CreateFactory(dispatcher, wasInserted: true, status: KnowledgeDocumentStatus.Pending, Guid.NewGuid());
        using var client = factory.CreateClient();

        using var response = await PostFileAsync(factory, client, collectionId: "project-a");

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var blobStore = factory.Services.GetRequiredService<IKnowledgeDocumentBlobStore>();
        await blobStore.Received(1).AddAsync(Arg.Is<KnowledgeDocumentInput>(input => input.CollectionId == "PROJECT-A"), Arg.Any<CancellationToken>());
    }

    private static TestServerWebAppFactory CreateFactory(IKnowledgeIngestionDispatcher dispatcher,
        bool wasInserted,
        KnowledgeDocumentStatus status,
        Guid documentId)
    {
        return new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
                blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult(new KnowledgeDocumentAddResult
                         {
                             DocumentId = documentId,
                             WasInserted = wasInserted
                         }));

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

    private static async Task<HttpResponseMessage> PostFileAsync(TestServerWebAppFactory factory, HttpClient client, string? collectionId = null, string query = "")
    {
#pragma warning disable CA2000 // MultipartFormDataContent owns the part content and disposes it when the `using content` scope ends.
        using var content = new MultipartFormDataContent
        {
            {
                new ByteArrayContent(Encoding.UTF8.GetBytes("hello knowledge base")), "file", "doc.txt"
            }
        };
        if (collectionId is not null)
        {
            content.Add(new StringContent(collectionId), "collectionId");
        }
#pragma warning restore CA2000

        using var request = new HttpRequestMessage(HttpMethod.Post, UploadRoute + query)
        {
            Content = content
        };
        factory.AddNodeBearerToken(request);

        return await client.SendAsync(request);
    }

    // Hand-written fake for the ValueTask-returning dispatcher: records enqueued ids and returns a fixed admission result,
    // avoiding the ValueTask/analyzer friction of stubbing it through NSubstitute.
    private sealed class RecordingDispatcher : IKnowledgeIngestionDispatcher
    {
        private readonly KnowledgeIngestionEnqueueResult _result;

        public RecordingDispatcher(KnowledgeIngestionEnqueueResult result)
        {
            _result = result;
        }

        public List<Guid> Enqueued { get; } = [];

        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            Enqueued.Add(documentId);
            return ValueTask.FromResult(_result);
        }
    }
}
