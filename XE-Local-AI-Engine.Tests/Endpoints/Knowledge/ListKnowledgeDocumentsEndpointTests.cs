namespace XE_Local_AI_Engine.Tests.Endpoints.Knowledge;

using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The document list carries the embedding-model verdict the INGESTION lane resolves, which is what decides whether
///     an upload can be indexed. Classifying the installed models by name is a different truth.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class ListKnowledgeDocumentsEndpointTests
{
    private const string ListRoute = "/api/local/v1/knowledge-base/documents";

    [Test]
    public async Task List_WhenResolutionIsConfident_ReportsTheResolvedModelAsAvailable()
    {
        // A custom name no name-based classifier would call an embedding model; the resolver matched it, so it works.
        var body = await GetListAsync(new EmbeddingModelResolution
        {
            Name = "house-vectors:Q4_K_M",
            IsConfident = true
        });

        AssertEx.Equal("house-vectors:Q4_K_M", body.GetProperty("embeddingModel").GetString());
        AssertEx.True(body.GetProperty("embeddingModelAvailable").GetBoolean(),
            "A confident resolution means ingestion can embed, so the upload gate must be open.");
    }

    [Test]
    public async Task List_WhenResolutionIsNotConfident_ReportsTheConfiguredModelAsUnavailable()
    {
        var body = await GetListAsync(new EmbeddingModelResolution
        {
            Name = "nomic-embed-text",
            IsConfident = false
        });

        AssertEx.Equal("nomic-embed-text", body.GetProperty("embeddingModel").GetString());
        AssertEx.False(body.GetProperty("embeddingModelAvailable").GetBoolean(),
            "Nothing installed matched, so an accepted upload would fail later in the background embedder.");
    }

    /// <summary>
    ///     The page has a ceiling: a repository import can hold tens of thousands of documents, and the list must never
    ///     be asked to decrypt and ship them all at once. The availability poll asks for one row.
    /// </summary>
    [Test]
    [Arguments("?limit=201", HttpStatusCode.BadRequest)]
    [Arguments("?limit=0", HttpStatusCode.BadRequest)]
    [Arguments("?offset=-1", HttpStatusCode.BadRequest)]
    [Arguments("?limit=1", HttpStatusCode.OK)]
    public async Task List_BoundsThePageSize(string query, HttpStatusCode expected)
    {
        var response = await SendListAsync(new EmbeddingModelResolution
            {
                Name = "nomic-embed-text",
                IsConfident = false
            },
            query);

        AssertEx.Equal(expected, response.Status);
        if (expected == HttpStatusCode.OK)
        {
            AssertEx.Equal(expected: 0, response.Body.GetProperty("totalCount").GetInt32(), "an empty node reports an empty total, not a missing one.");
        }
    }

    /// <summary>
    ///     The repeated <c>documentIds</c> key binds to the request's id list over HTTP, written exactly as the generated
    ///     client writes it (form style, one key per id), so a by-id read returns those documents and no others.
    /// </summary>
    [Test]
    public async Task List_ByRepeatedDocumentIds_ReturnsExactlyThoseDocuments()
    {
        await using var factory = new TestServerWebAppFactory();
        Guid[] documentIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            foreach (var documentId in documentIds)
            {
                await SeedDocumentAsync(context, documentId);
            }
        }

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ListRoute}?documentIds={documentIds[0]}&documentIds={documentIds[2]}");
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var listed = body.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("documentId").GetGuid()).Order().ToArray();
        AssertEx.True(listed.SequenceEqual(new[] { documentIds[0], documentIds[2] }.Order()),
            $"both repeated ids bind and nothing else comes back; listed: {string.Join(", ", listed)}.");
        AssertEx.Equal(expected: 2, body.GetProperty("totalCount").GetInt32());
    }

    private static async Task SeedDocumentAsync(NodeChatDbContext context, Guid documentId)
    {
        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO knowledge_documents (document_id, original_file_name, mime_type, extension, size_bytes, content_hash, storage_path,
                                             status, chunk_count, embedding_model, vector_identity, vector_dim, created_at_utc, updated_at_utc,
                                             parser_version, chunker_version)
            VALUES ($id, $name, 'text/plain', '.txt', 10, $hash, $path, 'Pending', 0, 'nomic-embed-text', $identity, 0, 1, 1,
                    $parser, $chunker);
            """;
        AddParameter(command, "$id", documentId);
        AddParameter(command, "$name", context.EncryptKnowledgeFileName("document.txt", documentId));
        AddParameter(command, "$hash", "hash-" + documentId.ToString("N"));
        AddParameter(command, "$path", documentId.ToString("D") + ".txt");
        AddParameter(command, "$identity", KnowledgeEmbeddingVectorPolicy.LegacyIdentity);
        AddParameter(command, "$parser", KnowledgeIndexVersions.Parser);
        AddParameter(command, "$chunker", KnowledgeIndexVersions.Chunker);
        _ = await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<JsonElement> GetListAsync(EmbeddingModelResolution resolution)
    {
        var response = await SendListAsync(resolution, query: "");
        AssertEx.Equal(HttpStatusCode.OK, response.Status);
        return response.Body;
    }

    private sealed class ListResponse
    {
        public required HttpStatusCode Status { get; init; }

        public required JsonElement Body { get; init; }
    }

    private static async Task<ListResponse> SendListAsync(EmbeddingModelResolution resolution, string query)
    {
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                var resolver = Substitute.For<IEmbeddingModelResolver>();
                resolver.ResolveAsync(Arg.Any<ILocalModelProvider>(), Arg.Any<CancellationToken>())
                        .Returns(Task.FromResult(resolution));
                services.RemoveAll<IEmbeddingModelResolver>();
                services.AddSingleton(resolver);
            }
        };
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, ListRoute + query);
        factory.AddNodeBearerToken(request);
        using var response = await client.SendAsync(request);

        return new ListResponse
        {
            Status = response.StatusCode,
            Body = await response.Content.ReadFromJsonAsync<JsonElement>()
        };
    }
}
