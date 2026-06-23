namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     HTTP-layer enforcement of the read-only (Origin=Remote) boundary. The guard + persistence layers are
///     unit-tested elsewhere; these drive the actual FastEndpoints pipeline and assert the authoritative wire
///     contract: 409 Conflict with body {code:"conversation-read-only", reason:"remote-origin"}. This is the
///     security-relevant boundary that stops a node-local operator from mutating a platform-owned conversation.
/// </summary>
public sealed class NodeChatReadOnlyEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Test]
    public async Task RenameConversation_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        using var request = CreateJsonRequest(factory,
            HttpMethod.Patch,
            $"/api/local/v1/chat/conversations/{conversationId}/rename",
            new
            {
                Title = "renamed by operator"
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    [Test]
    public async Task PinConversation_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        using var request = CreateJsonRequest(factory,
            HttpMethod.Patch,
            $"/api/local/v1/chat/conversations/{conversationId}/pin",
            new
            {
                IsPinned = true
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    [Test]
    public async Task ArchiveConversation_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        using var request = CreateJsonRequest(factory,
            HttpMethod.Patch,
            $"/api/local/v1/chat/conversations/{conversationId}/archive",
            new
            {
                Archived = true
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    [Test]
    public async Task BranchConversation_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        // The guard rejects on conversation origin before any message lookup, so an arbitrary message id
        // exercises the read-only boundary without seeding a message. The empty JSON body satisfies the POST
        // content-type binding (ConversationId/MessageId bind from the route); without it FastEndpoints rejects
        // with 415 before the handler/guard runs.
        using var request = CreateJsonRequest(factory,
            HttpMethod.Post,
            $"/api/local/v1/chat/conversations/{conversationId}/branch/{Guid.NewGuid()}",
            new
            {
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    [Test]
    public async Task CreateMessageRevision_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        // POST revisions creates a sibling variant placeholder — a mutation, so it is guarded. Empty JSON body
        // satisfies the POST content-type binding (ids bind from the route); without it the pipeline returns 415
        // before the guard runs.
        using var request = CreateJsonRequest(factory,
            HttpMethod.Post,
            $"/api/local/v1/chat/conversations/{conversationId}/messages/{Guid.NewGuid()}/revisions",
            new
            {
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    [Test]
    public async Task SetMessageFeedback_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        using var request = CreateJsonRequest(factory,
            HttpMethod.Put,
            $"/api/local/v1/chat/conversations/{conversationId}/messages/{Guid.NewGuid()}/feedback",
            new
            {
                Rating = "up"
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    [Test]
    public async Task SetSelectedPath_WhenOriginRemote_Returns409ReadOnly()
    {
        await using var factory = new TestingWebAppFactory();
        using var client = factory.CreateClient();
        var conversationId = await SeedRemoteConversationAsync(factory).ConfigureAwait(false);

        // Persisting a selection is a mutation of conversation metadata, so it is guarded on a remote-mirror
        // (view-only) conversation, mirroring the rename/pin/branch/feedback boundary.
        using var request = CreateJsonRequest(factory,
            HttpMethod.Put,
            $"/api/local/v1/chat/conversations/{conversationId}/selected-path",
            new
            {
                SelectedPath = new Dictionary<Guid, Guid>
                {
                    [Guid.NewGuid()] = Guid.NewGuid()
                }
            });
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        await AssertReadOnlyConflictAsync(response).ConfigureAwait(false);
    }

    private static async Task<Guid> SeedRemoteConversationAsync(TestingWebAppFactory factory)
    {
        var persistence = factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversationId = Guid.NewGuid();
        await persistence.EnsureConversationAsync(new NodeChatEnsureConversationRequest(conversationId,
                             "Platform conversation",
                             "client-node",
                             CreatedAtUtc: 10,
                             NodeChatOriginValues.Remote))
                         .ConfigureAwait(false);
        return conversationId;
    }

    private static async Task AssertReadOnlyConflictAsync(HttpResponseMessage response)
    {
        AssertEx.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var body = await ReadJsonAsync<NodeChatConflictResponse>(response).ConfigureAwait(false);
        AssertEx.Equal("conversation-read-only", body.Code);
        AssertEx.Equal("remote-origin", body.Reason);
    }

    private static HttpRequestMessage CreateJsonRequest<T>(TestingWebAppFactory factory, HttpMethod method, string uri, T content)
    {
        var request = CreateRequest(factory, method, uri);
        request.Content = JsonContent.Create(content);
        return request;
    }

    private static HttpRequestMessage CreateRequest(TestingWebAppFactory factory, HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions).ConfigureAwait(false));
    }
}
