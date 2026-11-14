namespace XE_Local_AI_Engine.Tests.Chat;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Endpoints.LocalChat.V1;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The chat export file end to end: an export is chats only and their selected path, an import writes NEW chats
///     through the ordinary writer (fresh ids, ciphertext at rest), and a file the trust boundary refuses writes nothing.
/// </summary>
/// <remarks>The host is shared, so every test finds its own chats by a unique title, never by a count.</remarks>
[Category(TestCategories.Integration)]
public sealed class NodeChatExportImportEndpointTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public async Task ExportThenImport_CreatesNewChatsWithTheSelectedPathText()
    {
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var title = $"Round trip {Guid.NewGuid():N}";
        var source = await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = title,
            UserId = null,
            CreatedAtUtc = 1_000
        });
        await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest
        {
            ConversationId = source.ConversationId,
            MessageId = Guid.NewGuid(),
            Content = "What is the capital of France?",
            CreatedAtUtc = 1_001
        });
        var chosen = await AnswerAsync(persistence, source.ConversationId, "Paris.", 1_002, withToolCall: true);
        var discarded = await RegenerateAsync(persistence, source.ConversationId, chosen, "Lyon, surely.", 1_003);
        await persistence.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest
        {
            ConversationId = source.ConversationId,
            SelectedPath = new Dictionary<Guid, Guid>
            {
                [discarded.VariantGroupId] = chosen
            },
            UpdatedAtUtc = 1_004
        });
        await Factory.Services.GetRequiredService<IConversationUploadedFileStore>().AddAsync(new ConversationUploadedFileInput
            {
                ConversationId = source.ConversationId,
                FileId = Guid.NewGuid(),
                OriginalFileName = "notes.txt",
                MimeType = "text/plain",
                Extension = ".txt",
                SizeBytes = 4,
                Content = Encoding.UTF8.GetBytes("data"),
                ExtractionStatus = DocumentExtractionStatus.Extracted,
                ExtractedMarkdown = "data",
                ExtractedChars = 4
            },
            CancellationToken.None);

        using var client = Factory.CreateClient();
        var exported = await ExportAsync(client, source.ConversationId);
        var exportedChat = exported.Conversations.Single();
        AssertEx.Equal(NodeChatExportFile.Format, exported.Format);
        AssertEx.Equal(NodeChatExportFile.FormatVersion, exported.FormatVersion);
        AssertEx.Equal("What is the capital of France?|Paris.", string.Join('|', exportedChat.Messages.Select(static message => message.Content)),
            "Only the selected branch is exported; the deselected regeneration is not.");
        AssertEx.Equal(expected: 1, exportedChat.Messages[1].ToolCallCount, "A tool call is recorded as a count, never its arguments or result.");
        AssertEx.Equal("notes.txt", exportedChat.Attachments.Single().FileName, "An attachment is recorded by name only.");

        using var importRequest = JsonRequest(HttpMethod.Post, "/api/local/v1/chat/import", exported);
        using var importResponse = await client.SendAsync(importRequest);
        var imported = (await ReadJsonAsync<NodeChatImportResponse>(importResponse)).Conversations.Single();

        AssertEx.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        AssertEx.NotEqual(source.ConversationId, imported.ConversationId, "An import mints a new conversation id, never the source's.");
        var loaded = AssertEx.NotNull(await persistence.GetConversationAsync(imported.ConversationId));
        AssertEx.Equal<string?>(title, loaded.Title);
        AssertEx.Equal(1_000L, loaded.CreatedAtUtc);
        AssertEx.Equal("What is the capital of France?|Paris.", string.Join('|', loaded.Messages.Select(static message => message.Content)));
        AssertEx.Equal("1001|1002", string.Join('|', loaded.Messages.Select(static message => message.CreatedAtUtc)), "Message times survive the round trip.");
        AssertEx.False(loaded.Messages.Any(message => message.MessageId == chosen), "Every imported message gets a fresh id.");
        AssertEx.Equal(NodeConversationKind.Chat, await persistence.GetConversationKindAsync(imported.ConversationId));
        AssertEx.Empty(await Factory.Services.GetRequiredService<IConversationUploadedFileStore>().ListAsync(imported.ConversationId, CancellationToken.None),
            "Attachments are not exported as content, so an import restores none.");
    }

    [Test]
    public async Task Import_StoresTheTextAsCiphertextAndTheNormalReadDecryptsIt()
    {
        const string secret = "The vault combination is 4-8-15-16-23-42.";
        using var client = Factory.CreateClient();

        using var request = JsonRequest(HttpMethod.Post, "/api/local/v1/chat/import", Document(Chat($"Cipher {Guid.NewGuid():N}", Message("user", secret))));
        using var response = await client.SendAsync(request);
        var imported = (await ReadJsonAsync<NodeChatImportResponse>(response)).Conversations.Single();

        await using var scope = Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await using var command = dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT content FROM messages WHERE conversation_id = $id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$id";
        parameter.Value = imported.ConversationId;
        command.Parameters.Add(parameter);
        await dbContext.Database.OpenConnectionAsync();
        var stored = AssertEx.NotNull(await command.ExecuteScalarAsync() as byte[], "The imported message row must exist under the new conversation id.");

        AssertEx.False(Encoding.UTF8.GetString(stored).Contains("vault combination", StringComparison.Ordinal), "Imported text must be encrypted at rest.");
        var loaded = AssertEx.NotNull(await Factory.Services.GetRequiredService<INodeChatPersistenceService>().GetConversationAsync(imported.ConversationId));
        AssertEx.Equal(secret, loaded.Messages.Single().Content);
    }

    [Test]
    public async Task ExportAll_HoldsChatsOnly_WithArchivedOnesFlagged()
    {
        var persistence = Factory.Services.GetRequiredService<INodeChatPersistenceService>();
        var suffix = Guid.NewGuid().ToString("N");
        _ = await CreateAsync(persistence, $"Chat {suffix}", NodeConversationKind.Chat);
        var archived = await CreateAsync(persistence, $"Archived {suffix}", NodeConversationKind.Chat);
        await persistence.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest
        {
            ConversationId = archived,
            Archived = true,
            UpdatedAtUtc = 20
        });
        var workSession = await CreateAsync(persistence, $"Work session {suffix}", NodeConversationKind.WorkSession);
        await CreateAsync(persistence, $"Integration {suffix}", NodeConversationKind.Integration);
        var scheduled = await CreateAsync(persistence, $"Scheduled {suffix}", NodeConversationKind.Scheduled);
        using var client = Factory.CreateClient();

        var exported = (await ExportAsync(client, conversationId: null)).Conversations.Where(conversation => conversation.Title?.EndsWith(suffix, StringComparison.Ordinal) == true)
                                                                             .ToDictionary(static conversation => conversation.Title!);
        using var byIdRequest = Request(HttpMethod.Get, $"/api/local/v1/chat/export?conversationId={workSession}");
        using var byIdResponse = await client.SendAsync(byIdRequest);
        using var scheduledByIdRequest = Request(HttpMethod.Get, $"/api/local/v1/chat/export?conversationId={scheduled}");
        using var scheduledByIdResponse = await client.SendAsync(scheduledByIdRequest);

        AssertEx.Equal($"Archived {suffix}|Chat {suffix}", string.Join('|', exported.Keys.Order(StringComparer.Ordinal)),
            "Work-session, integration and scheduled-run transcripts are not chats the operator started, so they are never exported.");
        AssertEx.True(exported[$"Archived {suffix}"].Archived, "An archived chat is exported, flagged.");
        AssertEx.False(exported[$"Chat {suffix}"].Archived);
        AssertEx.Equal(HttpStatusCode.NotFound, byIdResponse.StatusCode, "A by-id export of a non-chat transcript answers like a missing chat.");
        AssertEx.Equal(HttpStatusCode.NotFound, scheduledByIdResponse.StatusCode, "A scheduled run's answer is not exportable by id either.");
    }

    [Test]
    [Arguments("other-format", 1, "user", "This is not a chat export file")]
    [Arguments(NodeChatExportFile.Format, 2, "user", "format version 1 only")]
    [Arguments(NodeChatExportFile.Format, 1, "tool", "Message role 'tool' cannot be imported")]
    [Arguments(NodeChatExportFile.Format, 1, "system", "Message role 'system' cannot be imported")]
    public async Task Import_RefusesAFileOutsideTheFormat_AndWritesNothing(string format, int formatVersion, string role, string expectedError)
    {
        var title = $"Refused {Guid.NewGuid():N}";
        var document = new NodeChatExportDocument
        {
            Format = format,
            FormatVersion = formatVersion,
            Conversations = [Chat(title, Message("user", "Hello"), Message(role, "Injected"))]
        };
        using var client = Factory.CreateClient();

        using var request = JsonRequest(HttpMethod.Post, "/api/local/v1/chat/import", document);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), expectedError);
        AssertEx.False((await ExportAsync(client, conversationId: null)).Conversations.Any(conversation => conversation.Title == title), "A refused file writes nothing.");
    }

    [Test]
    [Arguments(-1L, 11L, 5, "A chat's creation time must be neither negative nor in the future")]
    [Arguments(253_402_300_799_999L, 11L, 5, "A chat's creation time must be neither negative nor in the future")]
    [Arguments(10L, -1L, 5, "A message's creation time must be neither negative nor in the future")]
    [Arguments(10L, 253_402_300_799_999L, 5, "A message's creation time must be neither negative nor in the future")]
    [Arguments(10L, 11L, NodeChatExportFile.MaxTitleLength + 1, "A chat title may be at most 200 characters")]
    public async Task Import_RefusesAnOutOfRangeTimeOrTitle_AndWritesNothing(long chatCreatedAtUtc, long messageCreatedAtUtc, int titleLength, string expectedError)
    {
        var marker = Guid.NewGuid().ToString("N");
        var title = (marker + new string('t', titleLength))[..titleLength];
        var document = Document(new NodeChatExportConversation
        {
            Title = title,
            CreatedAtUtc = chatCreatedAtUtc,
            Messages = [new NodeChatExportMessage { Role = "user", Content = marker, CreatedAtUtc = messageCreatedAtUtc }]
        });
        using var client = Factory.CreateClient();

        using var request = JsonRequest(HttpMethod.Post, "/api/local/v1/chat/import", document);
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), expectedError);
        AssertEx.False((await ExportAsync(client, conversationId: null)).Conversations.Any(conversation => conversation.Messages.Any(message => message.Content == marker)),
            "A refused file writes nothing.");
    }

    [Test]
    [Arguments("null")]
    [Arguments("[]")]
    [Arguments("[null]")]
    public async Task Import_RefusesAMissingOrEmptyChatList_AsABadRequest(string conversationsJson)
    {
        using var client = Factory.CreateClient();
        using var request = Request(HttpMethod.Post, "/api/local/v1/chat/import");
        request.Content = new StringContent($$"""{"format":"{{NodeChatExportFile.Format}}","formatVersion":{{NodeChatExportFile.FormatVersion}},"conversations":{{conversationsJson}}}""",
            System.Text.Encoding.UTF8, "application/json");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode, $"conversations={conversationsJson} must be refused, never a 500.");
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), $"between 1 and {NodeChatExportFile.MaxConversations} chats");
    }

    [Test]
    public async Task Import_RefusesMoreMessagesThanTheCap()
    {
        var messages = Enumerable.Range(0, NodeChatExportFile.MaxMessages + 1).Select(static _ => Message("user", "x")).ToArray();
        using var client = Factory.CreateClient();

        using var request = JsonRequest(HttpMethod.Post, "/api/local/v1/chat/import", Document(Chat("Too long", messages)));
        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), $"at most {NodeChatExportFile.MaxMessages} messages");
    }

    [Test]
    public async Task Import_RefusesABodyOverTheByteCapBeforeReadingIt()
    {
        using var client = Factory.CreateClient();
        using var request = Request(HttpMethod.Post, "/api/local/v1/chat/import");
        request.Content = new ByteArrayContent(new byte[NodeChatExportFile.MaxImportBytes + 1]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request);

        AssertEx.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        AssertEx.Contains(await response.Content.ReadAsStringAsync(), "MB this node imports at once");
    }

    private static NodeChatExportDocument Document(params NodeChatExportConversation[] conversations) => new()
    {
        Format = NodeChatExportFile.Format,
        FormatVersion = NodeChatExportFile.FormatVersion,
        Conversations = conversations
    };

    private static NodeChatExportConversation Chat(string title, params NodeChatExportMessage[] messages) => new()
    {
        Title = title,
        CreatedAtUtc = 10,
        Messages = messages
    };

    private static NodeChatExportMessage Message(string role, string content) => new()
    {
        Role = role,
        Content = content,
        CreatedAtUtc = 11
    };

    private static async Task<Guid> CreateAsync(INodeChatPersistenceService persistence, string title, string kind) =>
        (await persistence.CreateConversationAsync(new NodeChatCreateConversationRequest
        {
            Title = title,
            UserId = null,
            CreatedAtUtc = 10,
            Kind = kind
        })).ConversationId;

    private static async Task<Guid> AnswerAsync(INodeChatPersistenceService persistence, Guid conversationId, string content, long createdAtUtc, bool withToolCall)
    {
        var correlation = new NodeChatMessageCorrelation
        {
            ConversationId = conversationId,
            MessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid()
        };
        await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest
        {
            ConversationId = conversationId,
            MessageId = correlation.MessageId,
            RequestId = correlation.RequestId,
            CreatedAtUtc = createdAtUtc
        });
        await TerminalizeAsync(persistence, correlation, content, createdAtUtc, withToolCall);
        return correlation.MessageId;
    }

    private static async Task<NodeChatMessageVariantDto> RegenerateAsync(INodeChatPersistenceService persistence, Guid conversationId, Guid originalMessageId, string content,
        long createdAtUtc)
    {
        var variant = await persistence.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest
        {
            ConversationId = conversationId,
            OriginalMessageId = originalMessageId,
            NewMessageId = Guid.NewGuid(),
            RequestId = Guid.NewGuid(),
            CreatedAtUtc = createdAtUtc
        });
        await TerminalizeAsync(persistence,
            new NodeChatMessageCorrelation
            {
                ConversationId = conversationId,
                MessageId = variant.Variant.MessageId,
                RequestId = variant.Variant.RequestId ?? throw new InvalidOperationException("A regenerated variant carries its request id.")
            },
            content,
            createdAtUtc,
            withToolCall: false);
        return variant;
    }

    private static Task TerminalizeAsync(INodeChatPersistenceService persistence, NodeChatMessageCorrelation correlation, string content, long updatedAtUtc, bool withToolCall) =>
        persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
        {
            Correlation = correlation,
            Status = NodeChatMessageStatusValues.Completed,
            UpdatedAtUtc = updatedAtUtc,
            Content = content,
            Parts = withToolCall
                ? [new NodeChatMessagePart(NodeChatMessagePartKinds.Tool, 0, ToolCallId: "call-1", Name: "web_search", State: "completed", Args: "{}", Result: "secret result"),
                    new NodeChatMessagePart(NodeChatMessagePartKinds.Text, 1, Text: content)]
                : null
        });

    private async Task<NodeChatExportDocument> ExportAsync(HttpClient client, Guid? conversationId)
    {
        using var request = Request(HttpMethod.Get, conversationId is { } id ? $"/api/local/v1/chat/export?conversationId={id}" : "/api/local/v1/chat/export");
        using var response = await client.SendAsync(request);
        AssertEx.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync<NodeChatExportDocument>(response);
    }

    private HttpRequestMessage JsonRequest<T>(HttpMethod method, string uri, T content)
    {
        var request = Request(method, uri);
        request.Content = JsonContent.Create(content, options: JsonOptions);
        return request;
    }

    private HttpRequestMessage Request(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        Factory.AddNodeBearerToken(request);
        request.Headers.Add("Origin", "http://localhost");
        return request;
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response)
        where T : class
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return AssertEx.NotNull(await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions));
    }
}
