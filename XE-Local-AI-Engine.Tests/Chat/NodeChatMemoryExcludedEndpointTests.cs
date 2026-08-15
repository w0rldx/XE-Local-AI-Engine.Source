namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint contract test for the per-conversation temporary-chat (<c>memory_excluded</c>) override added by the
///     adaptive-memory feature. The PATCH toggle rides the existing conversation-mutation surface (Operator-gated like
///     rename/pin/archive) and round-trips through the read path.
/// </summary>
public sealed class NodeChatMemoryExcludedEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task SetMemoryExcluded_WhenNoBearerToken_ReturnsUnauthorized()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/local/v1/chat/conversations/{Guid.NewGuid()}/memory-excluded")
        {
            Content = JsonContent.Create(new
            {
                memoryExcluded = true
            })
        };
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task SetMemoryExcluded_Toggle_RoundTrips()
    {
        var factory = Factory;
        using var client = factory.CreateClient();
        var persistence = factory.Services.GetRequiredService<INodeChatPersistenceService>();

        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest("Toggle API", UserId: null, CreatedAtUtc: 10)).ConfigureAwait(false);
        AssertEx.False(conversation.MemoryExcluded, "A fresh unbound conversation starts non-temporary.");

        // Toggle on via the PATCH endpoint.
        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/local/v1/chat/conversations/{conversation.ConversationId}/memory-excluded")
        {
            Content = JsonContent.Create(new
            {
                memoryExcluded = true
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);

        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(payload);
        AssertEx.True(document.RootElement.GetProperty("memoryExcluded").GetBoolean(), "The response reflects the toggled flag.");

        // Round-trip through the read path proves the column write persisted.
        var reloaded = AssertEx.NotNull(await persistence.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.True(reloaded.MemoryExcluded, "Toggling memory_excluded should round-trip through the read path.");
    }

    [Test]
    public async Task SetMemoryExcluded_WhenConversationMissing_ReturnsNotFound()
    {
        var factory = Factory;
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"/api/local/v1/chat/conversations/{Guid.NewGuid()}/memory-excluded")
        {
            Content = JsonContent.Create(new
            {
                memoryExcluded = true
            })
        };
        factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        using var response = await client.SendAsync(request).ConfigureAwait(false);

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
