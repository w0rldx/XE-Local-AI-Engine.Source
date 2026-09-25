namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction.State;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Endpoint contract test for the read-only context-state view: Operator-gated, 404 for an unknown conversation, and
///     the persisted state (superseded entries included), synopsis and watermarks round-trip unchanged.
/// </summary>
[Category(TestCategories.Integration)]
public sealed class NodeChatContextStateEndpointTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task GetContextState_WhenNoBearerToken_ReturnsUnauthorized()
    {
        using var client = Factory.CreateClient();

        using var response = await client.GetAsync($"/api/local/v1/chat/conversations/{Guid.NewGuid()}/context-state");

        AssertEx.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Test]
    public async Task GetContextState_WhenConversationMissing_ReturnsNotFound()
    {
        using var response = await SendAsync(Guid.NewGuid());

        AssertEx.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Test]
    public async Task GetContextState_WhenNothingDistilled_ReturnsEmptyEntries()
    {
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Fresh",
            UserId = null,
            CreatedAtUtc = 10
        });

        using var response = await SendAsync(conversation.ConversationId);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        AssertEx.Equal(0, root.GetProperty("entries").GetArrayLength());
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("synopsis").ValueKind);
        AssertEx.Equal(JsonValueKind.Null, root.GetProperty("stateCoversToSequence").ValueKind);
        AssertEx.Equal(1, root.GetProperty("nextEntryNumber").GetInt32());
    }

    [Test]
    public async Task GetContextState_RoundTripsEntriesSynopsisAndWatermarks()
    {
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var conversation = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = "Distilled",
            UserId = null,
            CreatedAtUtc = 10
        });
        var state = new ConversationStateDocument
        {
            NextEntryNumber = 3,
            Entries =
            [
                new ConversationStateEntry
                {
                    Id = "e1",
                    Category = ConversationStateCategory.Decision,
                    Value = "Use PostgreSQL",
                    SourceSequences = [2],
                    SupersededById = "e2",
                    CreatedAtSequence = 2
                },
                new ConversationStateEntry
                {
                    Id = "e2",
                    Category = ConversationStateCategory.Correction,
                    Value = "Use SQLite",
                    SourceSequences = [4, 5],
                    CreatedAtSequence = 5
                }
            ]
        };
        AssertEx.NotNull(await persistence.SetConversationStateAsync(new NodeChatSetConversationStateRequest
        {
            ConversationId = conversation.ConversationId,
            State = ConversationStateSerializer.Serialize(state),
            CoversToSequence = 5,
            UpdatedAtUtc = 1_000
        }));
        AssertEx.NotNull(await persistence.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest
        {
            ConversationId = conversation.ConversationId,
            Summary = "The user picked SQLite.",
            CoversToSequence = 4,
            UpdatedAtUtc = 900
        }));

        using var response = await SendAsync(conversation.ConversationId);

        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        AssertEx.Equal(5, root.GetProperty("stateCoversToSequence").GetInt32());
        AssertEx.Equal(1_000L, root.GetProperty("stateUpdatedAtUtc").GetInt64());
        AssertEx.Equal("The user picked SQLite.", root.GetProperty("synopsis").GetString());
        AssertEx.Equal(4, root.GetProperty("synopsisCoversToSequence").GetInt32());
        AssertEx.Equal(900L, root.GetProperty("synopsisUpdatedAtUtc").GetInt64());
        AssertEx.Equal(3, root.GetProperty("nextEntryNumber").GetInt32());

        var entries = root.GetProperty("entries");
        AssertEx.Equal(2, entries.GetArrayLength());

        var superseded = entries[0];
        AssertEx.Equal("e1", superseded.GetProperty("id").GetString());
        AssertEx.Equal("Decision", superseded.GetProperty("category").GetString());
        AssertEx.Equal("Use PostgreSQL", superseded.GetProperty("value").GetString());
        AssertEx.Equal("e2", superseded.GetProperty("supersededById").GetString());
        AssertEx.False(superseded.GetProperty("isLive").GetBoolean(), "A superseded entry is not live.");

        var live = entries[1];
        AssertEx.Equal("e2", live.GetProperty("id").GetString());
        AssertEx.Equal("Correction", live.GetProperty("category").GetString());
        AssertEx.Equal(JsonValueKind.Null, live.GetProperty("supersededById").ValueKind);
        AssertEx.Equal(5, live.GetProperty("createdAtSequence").GetInt32());
        AssertEx.Equal("4,5", string.Join(',', live.GetProperty("sourceSequences").EnumerateArray().Select(static s => s.GetInt32())));
        AssertEx.True(live.GetProperty("isLive").GetBoolean(), "The replacing entry is live.");
    }

    private async Task<HttpResponseMessage> SendAsync(Guid conversationId)
    {
        using var client = Factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/local/v1/chat/conversations/{conversationId}/context-state");
        Factory.AddNodeBearerToken(request);
        return await client.SendAsync(request);
    }
}
