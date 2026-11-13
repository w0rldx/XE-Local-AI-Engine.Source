namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The collection list the chat composer's picker and the knowledge page offer: every collection holding a document,
///     with its count, and DEFAULT first even while it is empty, so the picker always has the backwards-compatible choice.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ListKnowledgeCollectionsEndpointTests
{
    private const string ListRoute = "/api/local/v1/knowledge-base/collections";

    [Test]
    public async Task List_OnANodeWithNoDocuments_ListsOnlyAnEmptyDefault()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();

        var body = await GetListAsync(factory, client);

        AssertEx.Equal("DEFAULT:0", Describe(body));
    }

    [Test]
    public async Task List_CountsDocumentsPerCollection_DefaultFirstThenById()
    {
        await using var factory = new TestServerWebAppFactory();
        using var client = factory.CreateClient();
        var blobStore = factory.Services.GetRequiredService<IKnowledgeDocumentBlobStore>();
        await AddDocumentAsync(blobStore, "PROJECT-B");
        await AddDocumentAsync(blobStore, "PROJECT-B");
        await AddDocumentAsync(blobStore, "PROJECT-A");

        var body = await GetListAsync(factory, client);

        AssertEx.Equal("DEFAULT:0,PROJECT-A:1,PROJECT-B:2", Describe(body));
    }

    private static async Task<JsonElement> GetListAsync(TestServerWebAppFactory factory, HttpClient client)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Describe(JsonElement body)
    {
        return string.Join(',', body.GetProperty("items")
                                    .EnumerateArray()
                                    .Select(static item => $"{item.GetProperty("collectionId").GetString()}:{item.GetProperty("documentCount").GetInt32()}"));
    }

    private static async Task AddDocumentAsync(IKnowledgeDocumentBlobStore blobStore, string collectionId)
    {
        var documentId = Guid.NewGuid();
        byte[] content = [.. documentId.ToByteArray()];
        _ = await blobStore.AddAsync(new KnowledgeDocumentInput
            {
                DocumentId = documentId,
                OriginalFileName = "notes.txt",
                MimeType = "text/plain",
                Extension = ".txt",
                SizeBytes = content.Length,
                ContentHash = documentId.ToString("N"),
                Content = content,
                EmbeddingModel = "nomic-embed-text",
                CollectionId = collectionId
            },
            CancellationToken.None);
    }
}
