namespace XE_Local_AI_Engine.Tests.Chat;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPersistenceServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    [Test]
    public async Task Messages_FollowAcceptedLifecycleAndPartialPersistence()
    {
        await using var provider = await BuildProviderAsync("lifecycle.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var userMessageId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, requestId);

        var user = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userMessageId, " hello ", CreatedAtUtc: 11)).ConfigureAwait(false);
        var placeholder = await service
                                .CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, requestId, CreatedAtUtc: 12, "llama"))
                                .ConfigureAwait(false);
        var streaming = await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 13).ConfigureAwait(false);
        var partial = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, "Hello", "thinking", UpdatedAtUtc: 14)).ConfigureAwait(false);
        var appended = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, " world", Reasoning: null, UpdatedAtUtc: 15, ReplaceContent: false)).ConfigureAwait(false);
        var completed = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                                         NodeChatMessageStatusValues.Completed,
                                         UpdatedAtUtc: 16,
                                         appended.Content,
                                         Model: "llama",
                                         InputCount: 10,
                                         OutputCount: 3,
                                         TotalCount: 13,
                                         ReasoningCount: 1))
                                     .ConfigureAwait(false);

        AssertEx.Equal(NodeChatMessageStatusValues.Completed, user.Status);
        AssertEx.Equal("hello", user.Content);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, placeholder.Status);
        AssertEx.Equal(requestId, placeholder.RequestId);
        AssertEx.Equal(NodeChatMessageStatusValues.Streaming, streaming.Status);
        AssertEx.Equal("Hello", partial.Content);
        AssertEx.Equal("thinking", partial.Reasoning);
        AssertEx.Equal("Hello world", appended.Content);
        AssertEx.Equal(NodeChatMessageStatusValues.Completed, completed.Status);
        AssertEx.Equal(expected: 16L, completed.UpdatedAtUtc);
        AssertEx.Equal(expected: 13, completed.TotalCount);

        var loaded = await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);
        var messages = AssertEx.NotNull(loaded).Messages;
        AssertEx.Equal(expected: 2, messages.Count);
        AssertEx.Equal(userMessageId, messages[0].MessageId);
        AssertEx.Equal(assistantMessageId, messages[1].MessageId);
        AssertEx.Equal("Hello world", messages[1].Content);
        AssertEx.Equal(expected: 10, messages[1].InputCount);
        AssertEx.Equal(expected: 3, messages[1].OutputCount);
        AssertEx.Equal(expected: 13, messages[1].TotalCount);
        AssertEx.Equal(expected: 1, messages[1].ReasoningCount);
    }

    [Test]
    public async Task TerminalizeAssistantMessageAsync_WithParts_RoundTripsOrderedInterleave()
    {
        await using var provider = await BuildProviderAsync("parts-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Parts", "node", CreatedAtUtc: 2000)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 2001))
                     .ConfigureAwait(false);

        // reasoning -> tool -> reasoning: a tool call between two reasoning runs is the Option A interleave that
        // produces a second Thoughts block. The tool part carries args + result (the completed-phase data).
        var parts = new List<NodeChatMessagePart>
        {
            new(NodeChatMessagePartKinds.Reasoning, Sequence: 0, "thinking before"),
            new(NodeChatMessagePartKinds.Tool, Sequence: 1, ToolCallId: "call-1", Name: "GetCurrentTime", State: NodeChatToolPartStates.Received, Args: "{\"tz\":\"UTC\"}",
                Result: "2026-06-01T00:00:00Z"),
            new(NodeChatMessagePartKinds.Reasoning, Sequence: 2, "thinking after")
        };

        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 2002,
                         "the answer",
                         "thinking before\nthinking after",
                         Parts: parts))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);
        var loadedParts = AssertEx.NotNull(assistant.Parts);

        AssertEx.Equal(expected: 3, loadedParts.Count);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, loadedParts[0].Kind);
        AssertEx.Equal("thinking before", loadedParts[0].Text);
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, loadedParts[1].Kind);
        AssertEx.Equal("call-1", loadedParts[1].ToolCallId);
        AssertEx.Equal("GetCurrentTime", loadedParts[1].Name);
        AssertEx.Equal(NodeChatToolPartStates.Received, loadedParts[1].State);
        AssertEx.Equal("{\"tz\":\"UTC\"}", loadedParts[1].Args);
        AssertEx.Equal("2026-06-01T00:00:00Z", loadedParts[1].Result);
        AssertEx.Equal(NodeChatMessagePartKinds.Reasoning, loadedParts[2].Kind);
        AssertEx.Equal("thinking after", loadedParts[2].Text);
        // The flattened Reasoning is still persisted for backward-compat + token counts.
        AssertEx.Equal("thinking before\nthinking after", assistant.Reasoning);
    }

    [Test]
    public async Task GetConversationAsync_WhenMetadataHasNoParts_ReturnsNullPartsWithoutError()
    {
        await using var provider = await BuildProviderAsync("parts-legacy.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy", "node", CreatedAtUtc: 2100)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 2101))
                     .ConfigureAwait(false);

        // Terminalize WITHOUT parts (the pre-parts shape): the serialized metadata omits the parts key entirely.
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 2102,
                         "legacy answer",
                         "legacy reasoning"))
                     .ConfigureAwait(false);

        // Simulate an even older blob with no parts key by overwriting the raw metadata column with a parts-free JSON.
        await OverwriteMetadataJsonAsync(provider, assistantMessageId, "{\"Reasoning\":\"legacy reasoning\",\"Model\":null}").ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Null(assistant.Parts);
        AssertEx.Equal("legacy reasoning", assistant.Reasoning);
        AssertEx.Equal("legacy answer", assistant.Content);
    }

    [Test]
    public async Task Metadata_RoundTripsAgentIdAndName()
    {
        await using var provider = await BuildProviderAsync("agent-attribution-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Attribution", "node", CreatedAtUtc: 3000)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        var agentDefinitionId = Guid.NewGuid();

        // The placeholder is stamped with the per-response agent attribution at send time; it must survive the
        // streaming/terminalize updates (which preserve it from current) and reload off the metadata blob.
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId,
                         assistantMessageId,
                         correlation.RequestId,
                         CreatedAtUtc: 3001,
                         "model-x",
                         AgentDefinitionId: agentDefinitionId,
                         AgentName: "Backend Buddy"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 3002).ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 3003,
                         "the answer",
                         "thinking",
                         Model: "model-x"))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Equal(agentDefinitionId, assistant.AgentDefinitionId);
        AssertEx.Equal("Backend Buddy", assistant.AgentName);
        // The terminalize update must NOT drop the attribution while it rewrites the rest of the blob.
        AssertEx.Equal("the answer", assistant.Content);
        AssertEx.Equal("thinking", assistant.Reasoning);
    }

    [Test]
    public async Task Metadata_LegacyBlobWithoutAgentFields_DeserializesNull()
    {
        await using var provider = await BuildProviderAsync("agent-attribution-legacy.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy attribution", "node", CreatedAtUtc: 3100)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 3101))
                     .ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 3102, "legacy answer",
                         "legacy reasoning"))
                     .ConfigureAwait(false);

        // Simulate a blob written before agent mode existed: the AgentDefinitionId/AgentName keys are absent entirely
        // (no migration), so they must deserialize to null without error.
        await OverwriteMetadataJsonAsync(provider, assistantMessageId, "{\"Reasoning\":\"legacy reasoning\",\"Model\":null}").ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Null(assistant.AgentDefinitionId);
        AssertEx.Null(assistant.AgentName);
        AssertEx.Equal("legacy reasoning", assistant.Reasoning);
    }

    [Test]
    public async Task Metadata_RoundTripsReasoningEffort()
    {
        await using var provider = await BuildProviderAsync("reasoning-effort-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Reasoning effort", "node", CreatedAtUtc: 3200)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        // The placeholder is stamped with the reasoning effort used to drive the turn; it must survive the
        // streaming/terminalize updates (which preserve it from current) and reload off the metadata blob.
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId,
                         assistantMessageId,
                         correlation.RequestId,
                         CreatedAtUtc: 3201,
                         "model-x",
                         ReasoningEffort: "high"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 3202).ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 3203,
                         "the answer",
                         "thinking",
                         Model: "model-x"))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Equal("high", assistant.ReasoningEffort);
        // The terminalize update must NOT drop the effort while it rewrites the rest of the blob.
        AssertEx.Equal("the answer", assistant.Content);
        AssertEx.Equal("thinking", assistant.Reasoning);
    }

    [Test]
    public async Task Metadata_LegacyBlobWithoutReasoningEffort_DeserializesNull()
    {
        await using var provider = await BuildProviderAsync("reasoning-effort-legacy.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy reasoning effort", "node", CreatedAtUtc: 3300)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 3301))
                     .ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 3302, "legacy answer",
                         "legacy reasoning"))
                     .ConfigureAwait(false);

        // Simulate a blob written before the reasoning-effort field existed: the ReasoningEffort key is absent entirely
        // (no migration), so it must deserialize to null without error.
        await OverwriteMetadataJsonAsync(provider, assistantMessageId, "{\"Reasoning\":\"legacy reasoning\",\"Model\":null}").ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Null(assistant.ReasoningEffort);
        AssertEx.Equal("legacy reasoning", assistant.Reasoning);
    }

    [Test]
    public async Task Metadata_RoundTripsGenerationDurationMs()
    {
        await using var provider = await BuildProviderAsync("generation-duration-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Generation duration", "node", CreatedAtUtc: 3400)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 3401,
                         "model-x"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 3402).ConfigureAwait(false);
        // The runner reports the whole-turn duration at terminalize; it rides the metadata blob (no DB column) and
        // must survive reload alongside the token counts that feed the tokens-per-second display.
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 3403,
                         "the answer",
                         "thinking",
                         Model: "model-x",
                         OutputCount: 42,
                         GenerationDurationMs: 2000))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Equal(expected: 2000L, assistant.GenerationDurationMs);
        AssertEx.Equal(expected: 42, assistant.OutputCount);
        AssertEx.Equal("the answer", assistant.Content);
    }

    [Test]
    public async Task Metadata_LegacyBlobWithoutGenerationDurationMs_DeserializesNull()
    {
        await using var provider = await BuildProviderAsync("generation-duration-legacy.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy generation duration", "node", CreatedAtUtc: 3500)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 3501))
                     .ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, UpdatedAtUtc: 3502, "legacy answer",
                         "legacy reasoning"))
                     .ConfigureAwait(false);

        // Simulate a blob written before the generation-duration field existed: the GenerationDurationMs key is absent
        // entirely (no migration), so it must deserialize to null without error.
        await OverwriteMetadataJsonAsync(provider, assistantMessageId, "{\"Reasoning\":\"legacy reasoning\",\"Model\":null}").ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Null(assistant.GenerationDurationMs);
        AssertEx.Equal("legacy reasoning", assistant.Reasoning);
    }

    [Test]
    public async Task CancelMessageAsync_TerminalizesOnlyMatchingCorrelation()
    {
        await using var provider = await BuildProviderAsync("cancel.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Cancel", UserId: null, CreatedAtUtc: 20)).ConfigureAwait(false);
        var targetMessageId = Guid.NewGuid();
        var otherMessageId = Guid.NewGuid();
        var targetCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, targetMessageId, Guid.NewGuid());
        var otherCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, otherMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, targetMessageId, targetCorrelation.RequestId, CreatedAtUtc: 21))
                     .ConfigureAwait(false);
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, otherMessageId, otherCorrelation.RequestId, CreatedAtUtc: 22))
                     .ConfigureAwait(false);

        var cancel = await service.CancelMessageAsync(new NodeChatCancelRequest(targetCorrelation, CancelledAtUtc: 23)).ConfigureAwait(false);

        AssertEx.True(cancel.Cancelled);
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, cancel.Status);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatMessageStatusValues.Cancelled, loaded.Messages.Single(message => message.MessageId == targetMessageId).Status);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, loaded.Messages.Single(message => message.MessageId == otherMessageId).Status);
    }

    [Test]
    public async Task TerminalizeAssistantMessageAsync_WhenFailed_PersistsPartialContentAndRedactedError()
    {
        await using var provider = await BuildProviderAsync("failed-terminal.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Failure", UserId: null, CreatedAtUtc: 24)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, CreatedAtUtc: 25))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 26).ConfigureAwait(false);
        await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, "partial answer", "partial reasoning", UpdatedAtUtc: 27)).ConfigureAwait(false);

        var failed = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
            NodeChatMessageStatusValues.Failed,
            UpdatedAtUtc: 28,
            "partial answer",
            "partial reasoning",
            "local-chat-stream-failed")).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var loadedAssistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, failed.Status);
        AssertEx.Equal("partial answer", loadedAssistant.Content);
        AssertEx.Equal("partial reasoning", loadedAssistant.Reasoning);
        AssertEx.Equal("local-chat-stream-failed", loadedAssistant.Error);
        AssertEx.Equal(expected: 28L, loadedAssistant.UpdatedAtUtc);
    }

    [Test]
    public async Task ListAndGet_ExcludePurgedConversationsAndOrderMessagesBySequence()
    {
        await using var provider = await BuildProviderAsync("list.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var keep = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Keep", UserId: null, CreatedAtUtc: 30)).ConfigureAwait(false);
        var purge = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Purge", UserId: null, CreatedAtUtc: 31)).ConfigureAwait(false);

        var second = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(keep.ConversationId, Guid.NewGuid(), "second visible", CreatedAtUtc: 33)).ConfigureAwait(false);
        var first = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(keep.ConversationId, Guid.NewGuid(), "first visible", CreatedAtUtc: 32)).ConfigureAwait(false);
        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(purge.ConversationId, DeletedAtUtc: 34)).ConfigureAwait(false);

        var summaries = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(keep.ConversationId).ConfigureAwait(false));
        var purged = await service.GetConversationAsync(purge.ConversationId).ConfigureAwait(false);

        AssertEx.Contains(summaries.Select(summary => summary.ConversationId), keep.ConversationId);
        AssertEx.False(summaries.Any(summary => summary.ConversationId == purge.ConversationId));
        AssertEx.Null(purged);
        AssertEx.Equal(second.MessageId, loaded.Messages[0].MessageId);
        AssertEx.Equal(first.MessageId, loaded.Messages[1].MessageId);
    }

    [Test]
    public async Task DeleteConversationAsync_CancelsActiveMessagesBeforeHidingConversation()
    {
        await using var provider = await BuildProviderAsync("delete.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Delete", UserId: null, CreatedAtUtc: 40)).ConfigureAwait(false);

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, Guid.NewGuid(), Guid.NewGuid(), CreatedAtUtc: 41))
                     .ConfigureAwait(false);

        var result = await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 42)).ConfigureAwait(false);
        var loaded = await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);

        AssertEx.True(result.CancelRequested);
        AssertEx.False(result.Purged);
        AssertEx.Null(loaded);
    }

    [Test]
    public async Task EnsureConversationAsync_WhenConversationIsNew_InsertsRemoteOriginRow()
    {
        await using var provider = await BuildProviderAsync("ensure-new.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversationId = Guid.NewGuid();

        var ensured = await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(conversationId,
                                       "Remote thread",
                                       "node",
                                       CreatedAtUtc: 100,
                                       NodeChatOriginValues.Remote))
                                   .ConfigureAwait(false);

        AssertEx.Equal(conversationId, ensured.ConversationId);
        AssertEx.Equal("Remote thread", ensured.Title);
        AssertEx.Equal(NodeChatOriginValues.Remote, ensured.Origin);
        AssertEx.Equal(expected: 100L, ensured.CreatedAtUtc);

        var loaded = await service.GetConversationAsync(conversationId).ConfigureAwait(false);
        AssertEx.Equal(NodeChatOriginValues.Remote, AssertEx.NotNull(loaded).Origin);
        // Title is encrypted at rest; the single-conversation read path must decrypt it back to plaintext.
        AssertEx.Equal("Remote thread", AssertEx.NotNull(loaded).Title);
    }

    [Test]
    public async Task EnsureConversationAsync_WhenConversationExists_ReturnsExistingRowWithoutOverwriting()
    {
        await using var provider = await BuildProviderAsync("ensure-existing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Original", "node", CreatedAtUtc: 200)).ConfigureAwait(false);

        var ensured = await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(created.ConversationId,
                                       "Should be ignored",
                                       "other",
                                       CreatedAtUtc: 999,
                                       NodeChatOriginValues.Remote))
                                   .ConfigureAwait(false);

        // Existing rows are never overwritten: title/origin/timestamps from the original CreateConversationAsync persist.
        AssertEx.Equal("Original", ensured.Title);
        AssertEx.Equal(NodeChatOriginValues.Local, ensured.Origin);
        AssertEx.Equal(expected: 200L, ensured.CreatedAtUtc);
    }

    [Test]
    public async Task EnsureConversationAsync_WhenCalledTwice_IsIdempotentAndDoesNotDuplicate()
    {
        await using var provider = await BuildProviderAsync("ensure-idempotent.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversationId = Guid.NewGuid();
        var request = new NodeChatEnsureConversationRequest(conversationId, "Remote thread", "node", CreatedAtUtc: 300, NodeChatOriginValues.Remote);

        var first = await service.EnsureConversationAsync(request).ConfigureAwait(false);
        var second = await service.EnsureConversationAsync(request with
        {
            Title = "Different",
            CreatedAtUtc = 400
        }).ConfigureAwait(false);

        AssertEx.Equal(first.ConversationId, second.ConversationId);
        AssertEx.Equal("Remote thread", second.Title);
        AssertEx.Equal(expected: 300L, second.CreatedAtUtc);

        var summaries = await service.ListConversationsAsync(new NodeChatListConversationsRequest(true)).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, summaries.Count(summary => summary.ConversationId == conversationId));
    }

    [Test]
    public async Task OriginColumn_RoundTripsLocalViaLocalPathAndRemoteViaPlatformPath()
    {
        await using var provider = await BuildProviderAsync("origin-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        // Local path: CreateConversationAsync defaults Origin=Local; PersistUserMessageAsync defaults Origin=Local.
        var localConversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var localMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(localConversation.ConversationId, localMessageId, "local question", CreatedAtUtc: 2)).ConfigureAwait(false);

        // Platform (remote) path: EnsureConversationAsync mirrors the conversation Origin=Remote; the user turn is
        // persisted with Origin=Remote, exactly as NodeChatRemotePersistenceCoordinator drives it.
        var remoteConversationId = Guid.NewGuid();
        await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(remoteConversationId, "Remote chat", "node", CreatedAtUtc: 3, NodeChatOriginValues.Remote)).ConfigureAwait(false);
        var remoteMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(remoteConversationId, remoteMessageId, "remote question", CreatedAtUtc: 4, Origin: NodeChatOriginValues.Remote))
                     .ConfigureAwait(false);

        // Read both back and assert the persisted origin column for each conversation and message.
        var loadedLocal = AssertEx.NotNull(await service.GetConversationAsync(localConversation.ConversationId).ConfigureAwait(false));
        var loadedRemote = AssertEx.NotNull(await service.GetConversationAsync(remoteConversationId).ConfigureAwait(false));

        AssertEx.Equal(NodeChatOriginValues.Local, loadedLocal.Origin);
        AssertEx.Equal(NodeChatOriginValues.Local, await ReadMessageOriginAsync(provider, localMessageId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatOriginValues.Remote, loadedRemote.Origin);
        AssertEx.Equal(NodeChatOriginValues.Remote, await ReadMessageOriginAsync(provider, remoteMessageId).ConfigureAwait(false));
    }

    [Test]
    public async Task RemoteMessageContent_IsEncryptedAtRest()
    {
        await using var provider = await BuildProviderAsync("remote-encrypted.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        const string plaintext = "remote at-rest encrypted content";

        // Persist an Origin=Remote message via the raw-SQL persistence path (PersistUserMessageAsync issues a
        // direct ADO.NET INSERT). The content column must be written as the versioned encrypted envelope.
        var remoteConversationId = Guid.NewGuid();
        await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(remoteConversationId, "Remote", "node", CreatedAtUtc: 10, NodeChatOriginValues.Remote)).ConfigureAwait(false);
        var remoteMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(remoteConversationId, remoteMessageId, plaintext, CreatedAtUtc: 11, Origin: NodeChatOriginValues.Remote))
                     .ConfigureAwait(false);

        // Read the raw content column with a direct SQL command (bypassing the service's decrypt path): it must carry
        // the 0xFE 0x01 envelope header and must NOT contain the recognizable plaintext bytes.
        var rawContent = await ReadRawMessageContentAsync(provider, remoteMessageId).ConfigureAwait(false);
        AssertEx.True(rawContent.Length >= 2 && rawContent[0] == 0xFE && rawContent[1] == 0x01, "Content must carry the encrypted-envelope header at rest.");
        AssertEx.False(ContainsSubsequence(rawContent, Encoding.UTF8.GetBytes(plaintext)), "Encrypted content must not contain recognizable plaintext at rest.");

        // The service round-trips it back to plaintext through the read path.
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(remoteConversationId).ConfigureAwait(false));
        AssertEx.Equal(plaintext, loaded.Messages.Single(message => message.MessageId == remoteMessageId).Content);
    }

    [Test]
    public async Task StreamingLifecycle_ContentAndMetadataAreEncryptedAtRestAndRoundTrip()
    {
        await using var provider = await BuildProviderAsync("streaming-encrypted.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        const string secret = "supercalifragilistic-secret-token";

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, requestId);

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, requestId, CreatedAtUtc: 2, "llama"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, updatedAtUtc: 3).ConfigureAwait(false);

        // Partial streaming flush: content column stays encrypted mid-stream.
        await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, secret, "thinking", UpdatedAtUtc: 4)).ConfigureAwait(false);
        var partialRaw = await ReadRawMessageContentAsync(provider, assistantMessageId).ConfigureAwait(false);
        AssertEx.True(partialRaw.Length >= 2 && partialRaw[0] == 0xFE && partialRaw[1] == 0x01, "Partial-flush content must be encrypted at rest.");
        AssertEx.False(ContainsSubsequence(partialRaw, Encoding.UTF8.GetBytes(secret)), "Partial-flush content must not leak plaintext.");

        // Terminalize: content + metadata still encrypted, and the whole turn round-trips through the read path.
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 5,
                         secret,
                         Model: "llama",
                         InputCount: 1,
                         OutputCount: 2,
                         TotalCount: 3,
                         ReasoningCount: 1))
                     .ConfigureAwait(false);

        var terminalRaw = await ReadRawMessageContentAsync(provider, assistantMessageId).ConfigureAwait(false);
        AssertEx.True(terminalRaw.Length >= 2 && terminalRaw[0] == 0xFE && terminalRaw[1] == 0x01, "Terminalized content must be encrypted at rest.");
        AssertEx.False(ContainsSubsequence(terminalRaw, Encoding.UTF8.GetBytes(secret)), "Terminalized content must not leak plaintext.");

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);
        AssertEx.Equal(secret, assistant.Content);
        AssertEx.Equal("thinking", assistant.Reasoning);
        AssertEx.Equal(expected: 3, assistant.TotalCount);
    }

    [Test]
    public async Task RawDiskFile_ContainsNoRecognizablePromptText()
    {
        var fileName = "raw-disk-absence.sqlite";
        const string prompt = "zzq-unique-user-prompt-marker-9182";
        await using (var provider = await BuildProviderAsync(fileName).ConfigureAwait(false))
        {
            var service = CreateService(provider);
            var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
            await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, Guid.NewGuid(), prompt, CreatedAtUtc: 2)).ConfigureAwait(false);
        }

        // Open the closed SQLite file bytes directly and assert the prompt text is absent from the entire file.
        var fileBytes = await File.ReadAllBytesAsync(GetDatabasePath(fileName)).ConfigureAwait(false);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(prompt)), "The SQLite file must not contain recognizable prompt plaintext.");
    }

    [Test]
    public async Task LegacyPlaintextContentRow_RemainsReadableAndMigratesToCiphertext()
    {
        await using var provider = await BuildProviderAsync("legacy-plaintext.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        const string legacyText = "legacy plaintext user question";

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "placeholder", CreatedAtUtc: 2)).ConfigureAwait(false);

        // Simulate a pre-encryption row: overwrite the content column with raw plaintext UTF-8 (no envelope header).
        await WriteRawMessageContentAsync(provider, messageId, Encoding.UTF8.GetBytes(legacyText)).ConfigureAwait(false);

        // Read-both: the service still returns the plaintext for the legacy row.
        var loadedBefore = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(legacyText, loadedBefore.Messages.Single(message => message.MessageId == messageId).Content);

        // The migration upgrades it to the encrypted envelope, and it still round-trips.
        using var migrationService = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);
        var migrated = await migrationService.MigrateAllAsync(batchSize: 50, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(migrated >= 1, "The legacy plaintext row must be migrated.");

        var rawAfter = await ReadRawMessageContentAsync(provider, messageId).ConfigureAwait(false);
        AssertEx.True(rawAfter.Length >= 2 && rawAfter[0] == 0xFE && rawAfter[1] == 0x01, "Migrated content must carry the envelope header.");
        AssertEx.False(ContainsSubsequence(rawAfter, Encoding.UTF8.GetBytes(legacyText)), "Migrated content must not contain plaintext.");

        var loadedAfter = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(legacyText, loadedAfter.Messages.Single(message => message.MessageId == messageId).Content);
    }

    [Test]
    public async Task ContentEncryptionMigration_IsIdempotentAndResumable()
    {
        await using var provider = await BuildProviderAsync("migration-idempotent.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);

        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, firstId, "one", CreatedAtUtc: 2)).ConfigureAwait(false);
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, secondId, "two", CreatedAtUtc: 3)).ConfigureAwait(false);
        await WriteRawMessageContentAsync(provider, firstId, Encoding.UTF8.GetBytes("legacy one")).ConfigureAwait(false);
        await WriteRawMessageContentAsync(provider, secondId, Encoding.UTF8.GetBytes("legacy two")).ConfigureAwait(false);

        using var migration = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);

        // Resumable: process one row per batch, and a re-run migrates only the rows still remaining.
        var firstBatch = await migration.MigrateBatchAsync(batchSize: 1, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, firstBatch);
        var remainder = await migration.MigrateAllAsync(batchSize: 50, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 1, remainder);

        // Idempotent: everything is now encrypted, so a further run migrates nothing.
        var rerun = await migration.MigrateAllAsync(batchSize: 50, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, rerun);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal("legacy one", loaded.Messages.Single(message => message.MessageId == firstId).Content);
        AssertEx.Equal("legacy two", loaded.Messages.Single(message => message.MessageId == secondId).Content);
    }

    [Test]
    public async Task ContentEncryptionMigration_CheckpointAndVacuum_ReclaimsPlaintextResidueFromFile()
    {
        const string fileName = "backfill-vacuum-residue.sqlite";
        const string legacyText = "zzq-legacy-plaintext-residue-marker-7731";
        await using var provider = await BuildProviderAsync(fileName).ConfigureAwait(false);
        var service = CreateService(provider);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "placeholder", CreatedAtUtc: 2)).ConfigureAwait(false);

        // Simulate a pre-encryption row: raw plaintext UTF-8 in the content column (no envelope header).
        await WriteRawMessageContentAsync(provider, messageId, Encoding.UTF8.GetBytes(legacyText)).ConfigureAwait(false);

        using var backfill = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);
        var migrated = await backfill.MigrateAllAsync(batchSize: 50, CancellationToken.None).ConfigureAwait(false);
        AssertEx.True(migrated >= 1, "The legacy plaintext row must be migrated.");

        // The row is now encrypted, but the old plaintext still lingers in freed pages / the journal until reclaimed.
        AssertEx.True(await backfill.CheckpointAndVacuumAsync(CancellationToken.None).ConfigureAwait(false),
            "A successful checkpoint/vacuum must report success so the caller can clear the reclamation marker.");

        // The whole main DB file — not just the row's current bytes — must be free of the migrated plaintext.
        var fileBytes = await File.ReadAllBytesAsync(GetDatabasePath(fileName)).ConfigureAwait(false);
        AssertEx.False(ContainsSubsequence(fileBytes, Encoding.UTF8.GetBytes(legacyText)),
            "After the post-backfill checkpoint/vacuum, no migrated plaintext may remain anywhere in the main DB file.");

        // And the encrypted row still round-trips back to plaintext through the read path.
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(legacyText, loaded.Messages.Single(message => message.MessageId == messageId).Content);
    }

    [Test]
    public async Task ContentEncryptionBackfill_ReclamationRetriedOnRestart_WhenMarkerSetButNoCandidatesRemain()
    {
        // Reproduce the state a failed/interrupted reclamation leaves behind: rows already encrypted (so no candidates
        // remain to re-trigger cleanup), plaintext residue still on disk, and the durable "reclamation pending" marker
        // set from the previous run. A restart must honour the marker and retry the reclamation to completion.
        const string fileName = "backfill-reclaim-retry.sqlite";
        const string legacyText = "zzq-reclaim-retry-marker-4460";
        await using var provider = await BuildProviderAsync(fileName).ConfigureAwait(false);
        var service = CreateService(provider);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "placeholder", CreatedAtUtc: 2)).ConfigureAwait(false);
        await WriteRawMessageContentAsync(provider, messageId, Encoding.UTF8.GetBytes(legacyText)).ConfigureAwait(false);

        using var backfill = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);

        // Encrypt the row WITHOUT reclaiming, then set the durable marker by hand to stand in for a previous run whose
        // reclamation never completed. There are now zero migration candidates left.
        AssertEx.True(await backfill.MigrateAllAsync(batchSize: 50, CancellationToken.None).ConfigureAwait(false) >= 1, "The legacy row must migrate.");
        await SetReclamationMarkerRawAsync(provider).ConfigureAwait(false);

        // Restart: no candidates remain, but the marker forces the reclamation to be retried. The marker is cleared only
        // if the checkpoint/VACUUM pass actually ran — so a cleared marker is the deterministic proof the retry fired
        // (the pre-fix behaviour skipped reclamation entirely when no candidates remained, leaving the marker set).
        await backfill.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(await IsReclamationMarkerSetRawAsync(provider).ConfigureAwait(false), "A successful retry must clear the reclamation-pending marker.");
        AssertEx.False(ContainsSubsequence(await File.ReadAllBytesAsync(GetDatabasePath(fileName)).ConfigureAwait(false), Encoding.UTF8.GetBytes(legacyText)),
            "After the retried reclamation, no migrated plaintext may remain in the main DB file.");
    }

    [Test]
    public async Task ContentEncryptionBackfill_MarkerClearedAfterSuccess_NoRedundantReclamationNextStartup()
    {
        const string fileName = "backfill-marker-cleared.sqlite";
        const string legacyText = "zzq-marker-cleared-marker-9013";
        await using var provider = await BuildProviderAsync(fileName).ConfigureAwait(false);
        var service = CreateService(provider);

        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "placeholder", CreatedAtUtc: 2)).ConfigureAwait(false);
        await WriteRawMessageContentAsync(provider, messageId, Encoding.UTF8.GetBytes(legacyText)).ConfigureAwait(false);

        using var backfill = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);

        // First startup: migrate + reclaim in one pass; the marker (set before migrating) must be cleared on success.
        await backfill.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.False(ContainsSubsequence(await File.ReadAllBytesAsync(GetDatabasePath(fileName)).ConfigureAwait(false), Encoding.UTF8.GetBytes(legacyText)),
            "The plaintext residue must be reclaimed on the first startup.");
        AssertEx.False(await IsReclamationMarkerSetRawAsync(provider).ConfigureAwait(false), "A successful reclamation must clear the marker.");

        // Second startup: no candidates and no marker → reclamation is skipped and the marker stays clear.
        await backfill.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);
        AssertEx.False(await IsReclamationMarkerSetRawAsync(provider).ConfigureAwait(false),
            "With nothing to migrate and the marker cleared, the next startup must not re-arm or run reclamation.");
    }

    [Test]
    public async Task ContentEncryptionBackfill_CancellationDuringCleanup_LeavesMarkerSet()
    {
        await using var provider = await BuildProviderAsync("backfill-cancel-cleanup.sqlite").ConfigureAwait(false);

        // Arrange the durable marker as if a run had just set it before reclaiming.
        await SetReclamationMarkerRawAsync(provider).ConfigureAwait(false);

        using var backfill = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);

        // A cancelled cleanup is swallowed and reports failure, so the caller never clears the marker.
        AssertEx.False(await backfill.CheckpointAndVacuumAsync(new CancellationToken(canceled: true)).ConfigureAwait(false),
            "A cancelled checkpoint/vacuum must report failure so the marker is left set.");
        AssertEx.True(await IsReclamationMarkerSetRawAsync(provider).ConfigureAwait(false),
            "Cancellation mid-cleanup must leave the reclamation-pending marker set for the next startup to retry.");
    }

    [Test]
    public async Task ContentEncryptionBackfill_RealCleanupFailureThenRestart_RetriesUntilResidueReclaimed()
    {
        // End-to-end failure injection: a first run migrates the legacy row but its checkpoint fails FOR REAL (a
        // concurrent WAL reader makes wal_checkpoint(TRUNCATE) report busy), leaving the marker set. A genuine restart
        // (new provider + service, fresh connections) with the blocker gone and zero candidates must retry and finish.
        const string fileName = "backfill-real-failure-restart.sqlite";
        const string legacyText = "zzq-real-failure-restart-marker-6621";
        var databasePath = GetDatabasePath(fileName);

        var provider = await BuildProviderAsync(fileName).ConfigureAwait(false);
        try
        {
            // WAL mode is required for a reader to be able to block the truncate.
            await SetJournalModeWalAsync(provider).ConfigureAwait(false);

            var service = CreateService(provider);
            var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Chat", "node", CreatedAtUtc: 1)).ConfigureAwait(false);
            var messageId = Guid.NewGuid();
            await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, messageId, "placeholder", CreatedAtUtc: 2)).ConfigureAwait(false);
            await WriteRawMessageContentAsync(provider, messageId, Encoding.UTF8.GetBytes(legacyText)).ConfigureAwait(false);

            // Capture the service's logs so the test can prove the failure was specifically a busy/incomplete checkpoint
            // (RunOnceAsync swallows migration AND cleanup errors, so marker-set alone would not distinguish them).
            var capturingLogger = new XE_Local_AI_Engine.Tests.CodexOAuth.CapturingLogger<NodeChatContentEncryptionBackfillService>();
            using var failingBackfill = new NodeChatContentEncryptionBackfillService(provider.GetRequiredService<IServiceScopeFactory>(), capturingLogger);

            await using (await OpenBlockingReaderAsync(databasePath).ConfigureAwait(false))
            {
                // Migration succeeds and the marker is set, but the blocked checkpoint reports busy -> cleanup fails.
                await failingBackfill.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

                // (a) Migration itself succeeded: the row now carries the encrypted envelope, so ZERO plaintext
                // candidates remain — the restart retry below therefore exercises marker-only recovery, not a
                // re-migration that would happen to clean up as a side effect.
                var rawContent = await ReadRawMessageContentAsync(provider, messageId).ConfigureAwait(false);
                AssertEx.True(rawContent.Length >= 2 && rawContent[0] == 0xFE && rawContent[1] == 0x01,
                    "After the first run the legacy row must be encrypted (migration succeeded; only the cleanup failed).");

                // (b) The cleanup failed for the RIGHT reason: the WAL checkpoint reported busy/incomplete.
                AssertEx.Contains(capturingLogger.AllText, "did not fully truncate", StringComparison.Ordinal,
                    "The first run's cleanup must fail specifically because the WAL checkpoint reported busy/incomplete.");

                AssertEx.True(await IsReclamationMarkerSetRawAsync(provider).ConfigureAwait(false),
                    "A checkpoint blocked by a concurrent reader (busy) must leave the reclamation-pending marker set.");
            }
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }

        // Genuine restart: fresh connections against the same on-disk DB, blocker released, zero candidates remaining.
        // Clear only THIS database's pooled connections (scoped by connection string — NOT the process-global
        // ClearAllPools, which would disrupt other tests running in parallel) so the restart truly reconnects and no idle
        // pooled reader left over from the failed run contends with the retry's VACUUM.
        using (var poolKey = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(poolKey);
        }

        await using var restarted = await BuildProviderAsync(fileName, resetDatabase: false).ConfigureAwait(false);
        var restartedService = CreateService(restarted);
        using var retryBackfill = new NodeChatContentEncryptionBackfillService(restarted.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NodeChatContentEncryptionBackfillService>.Instance);

        await retryBackfill.RunOnceAsync(CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(await IsReclamationMarkerSetRawAsync(restarted).ConfigureAwait(false),
            "The restart retry must complete the reclamation and clear the marker.");
        AssertEx.False(ContainsSubsequence(await File.ReadAllBytesAsync(databasePath).ConfigureAwait(false), Encoding.UTF8.GetBytes(legacyText)),
            "After the restart retry reclaims, no migrated plaintext may remain in the main DB file.");
        // The migrated row still round-trips through the read path after the reclamation.
        var loaded = AssertEx.NotNull(await restartedService.GetConversationAsync(
            (await restartedService.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false)).Single().ConversationId).ConfigureAwait(false));
        AssertEx.Equal(legacyText, loaded.Messages.Single().Content);
    }

    [Test]
    public async Task RenamePinArchive_PersistMappedColumnsAndArchivedConversationsAreHiddenUnlessRequested()
    {
        await using var provider = await BuildProviderAsync("rename-pin-archive.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Original", "node", CreatedAtUtc: 600)).ConfigureAwait(false);

        var renamed = AssertEx.NotNull(
            await service.RenameConversationAsync(new NodeChatRenameConversationRequest(conversation.ConversationId, "  Renamed  ", UpdatedAtUtc: 601)).ConfigureAwait(false));
        AssertEx.Equal("Renamed", renamed.Title);

        var pinned = AssertEx.NotNull(await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(conversation.ConversationId, IsPinned: true, UpdatedAtUtc: 602))
                                                   .ConfigureAwait(false));
        AssertEx.True(pinned.IsPinned);

        // Active listing keeps a pinned, unarchived conversation visible.
        var active = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        AssertEx.Contains(active.Select(summary => summary.ConversationId), conversation.ConversationId);
        AssertEx.True(active.Single(summary => summary.ConversationId == conversation.ConversationId).IsPinned);

        // Archiving hides it from the default (active) listing but not from the include-archived listing.
        var archived = AssertEx.NotNull(await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(conversation.ConversationId, Archived: true, UpdatedAtUtc: 603))
                                                     .ConfigureAwait(false));
        AssertEx.True(archived.Archived);

        var activeAfterArchive = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        AssertEx.False(activeAfterArchive.Any(summary => summary.ConversationId == conversation.ConversationId));

        var includingArchived = await service.ListConversationsAsync(new NodeChatListConversationsRequest(true)).ConfigureAwait(false);
        var listedArchived = includingArchived.Single(summary => summary.ConversationId == conversation.ConversationId);
        AssertEx.True(listedArchived.Archived);
        AssertEx.True(listedArchived.IsPinned);

        // Unarchiving restores it to the active listing.
        await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(conversation.ConversationId, Archived: false, UpdatedAtUtc: 604)).ConfigureAwait(false);
        var activeAfterUnarchive = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        AssertEx.Contains(activeAfterUnarchive.Select(summary => summary.ConversationId), conversation.ConversationId);
    }

    [Test]
    public async Task RenamePinArchive_WhenConversationMissing_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("rename-pin-archive-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var missingId = Guid.NewGuid();

        AssertEx.Null(await service.RenameConversationAsync(new NodeChatRenameConversationRequest(missingId, "Nope", UpdatedAtUtc: 700)).ConfigureAwait(false));
        AssertEx.Null(await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(missingId, IsPinned: true, UpdatedAtUtc: 701)).ConfigureAwait(false));
        AssertEx.Null(await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(missingId, Archived: true, UpdatedAtUtc: 702)).ConfigureAwait(false));
    }

    [Test]
    public async Task MutationGuard_AllowsLocalRejectsRemoteAndIgnoresMissing()
    {
        await using var provider = await BuildProviderAsync("guard.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var guard = new NodeChatMutationGuard(service);

        var local = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local", "node", CreatedAtUtc: 500)).ConfigureAwait(false);
        var remoteId = Guid.NewGuid();
        await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(remoteId, "Remote", "node", CreatedAtUtc: 501, NodeChatOriginValues.Remote)).ConfigureAwait(false);

        // Local origin: no-op (no throw).
        await guard.EnsureMutableAsync(local.ConversationId).ConfigureAwait(false);

        // Missing conversation: no-op (guard never masks NotFound).
        await guard.EnsureMutableAsync(Guid.NewGuid()).ConfigureAwait(false);

        // Remote origin: rejected.
        var rejection = await AssertEx.ThrowsAsync<NodeChatReadOnlyConversationException>(() => guard.EnsureMutableAsync(remoteId)).ConfigureAwait(false);
        AssertEx.Equal(remoteId, rejection.ConversationId);
    }

    [Test]
    public async Task BranchConversationAsync_ClonesMessagesUpToCutoffIntoNewLocalConversation()
    {
        await using var provider = await BuildProviderAsync("branch.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", CreatedAtUtc: 800)).ConfigureAwait(false);

        var first = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, Guid.NewGuid(), "first", CreatedAtUtc: 801)).ConfigureAwait(false);
        var assistantId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(source.ConversationId, assistantId, Guid.NewGuid(), CreatedAtUtc: 802)).ConfigureAwait(false);
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, Guid.NewGuid(), "after cutoff", CreatedAtUtc: 803)).ConfigureAwait(false);

        // Branch at the assistant message (sequence 1): only the first two messages should be copied.
        var branch = AssertEx.NotNull(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, assistantId, CreatedAtUtc: 810)).ConfigureAwait(false));
        AssertEx.Equal(expected: 2, branch.CopiedMessageCount);
        AssertEx.Equal(source.ConversationId, branch.SourceConversationId);

        var branched = AssertEx.NotNull(await service.GetConversationAsync(branch.BranchedConversationId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatOriginValues.Local, branched.Origin);
        AssertEx.Equal(source.ConversationId, branched.BranchOfConversationId);
        AssertEx.Equal(expected: 2, branched.Messages.Count);
        AssertEx.Equal("first", branched.Messages[0].Content);
        // Copies are fresh rows: the branch does not reuse the source message ids.
        AssertEx.False(branched.Messages.Any(message => message.MessageId == first.MessageId || message.MessageId == assistantId));
    }

    [Test]
    public async Task BranchConversationAsync_FromChosenRevision_CopiesOnlyThatVariantAsLinearThread()
    {
        await using var provider = await BuildProviderAsync("branch-revision.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", CreatedAtUtc: 840)).ConfigureAwait(false);

        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, Guid.NewGuid(), "question", CreatedAtUtc: 841)).ConfigureAwait(false);
        var originalAssistantId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(source.ConversationId, originalAssistantId, Guid.NewGuid(), CreatedAtUtc: 842))
                     .ConfigureAwait(false);

        // Regenerate => a newer sibling variant (the 2nd revision) sharing the original's variant group.
        var variant = AssertEx.NotNull(await service
                                             .CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(source.ConversationId, originalAssistantId, Guid.NewGuid(), Guid.NewGuid(),
                                                 CreatedAtUtc: 843))
                                             .ConfigureAwait(false));

        // Branch from the chosen (newer) revision: the branch must be a LINEAR thread carrying only that revision
        // as the assistant turn — the sibling original must NOT be copied (otherwise variant_group_id is dropped
        // on copy and the two revisions render as duplicate stacked assistant turns). RC variant-branch fix.
        var branch = AssertEx.NotNull(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, variant.Variant.MessageId, CreatedAtUtc: 850))
                                                   .ConfigureAwait(false));
        AssertEx.Equal(expected: 2, branch.CopiedMessageCount);

        var branched = AssertEx.NotNull(await service.GetConversationAsync(branch.BranchedConversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 2, branched.Messages.Count);
        AssertEx.Equal(expected: 1, branched.Messages.Count(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)));
        AssertEx.Equal("question", branched.Messages.Single(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)).Content);
        // The branched assistant turn is no longer a variant — a fresh linear thread.
        AssertEx.True(branched.Messages.All(message => message.VariantGroupId is null));
    }

    [Test]
    public async Task BranchConversationAsync_WhenMessageNotInConversation_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("branch-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", CreatedAtUtc: 820)).ConfigureAwait(false);

        AssertEx.Null(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, Guid.NewGuid(), CreatedAtUtc: 821)).ConfigureAwait(false));
        AssertEx.Null(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(Guid.NewGuid(), Guid.NewGuid(), CreatedAtUtc: 822)).ConfigureAwait(false));
    }

    [Test]
    public async Task BranchConversationAsync_WhenCancelledMidCopy_LeavesNoPartialBranch()
    {
        await using var provider = await BuildProviderAsync("branch-cancel.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", CreatedAtUtc: 900)).ConfigureAwait(false);

        // Enough copies that a short-fused cancellation can land partway through the copy loop, where — without a
        // wrapping transaction — the conversation row plus a prefix of its messages would already be autocommitted.
        const int messageCount = 150;
        var cutoffId = Guid.Empty;
        for (var index = 0; index < messageCount; index++)
        {
            var id = Guid.NewGuid();
            await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, id, $"message {index}", CreatedAtUtc: 901 + index))
                         .ConfigureAwait(false);
            cutoffId = id;
        }

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(2));
        try
        {
            await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, cutoffId, CreatedAtUtc: 2000), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when the cancellation lands before the branch commits.
        }

        // Atomicity invariant: either the branch fully committed (all copies present) or nothing was written — never a
        // partial branch conversation carrying only a prefix of its messages.
        var branchedConversationIds = await ListBranchedConversationIdsAsync(provider, source.ConversationId).ConfigureAwait(false);
        foreach (var branchedId in branchedConversationIds)
        {
            var copiedCount = await CountMessagesAsync(provider, branchedId).ConfigureAwait(false);
            AssertEx.Equal(messageCount, copiedCount,
                "A surviving branch conversation must carry every copied message: a partial branch means the copy loop is not atomic.");
        }
    }

    [Test]
    public async Task CreateMessageVariantAsync_CreatesSiblingSharingVariantGroupAndBackstampsOriginal()
    {
        await using var provider = await BuildProviderAsync("variant.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Variants", "node", CreatedAtUtc: 900)).ConfigureAwait(false);
        var userId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userId, "question", CreatedAtUtc: 901)).ConfigureAwait(false);
        var originalAssistantId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalAssistantId, Guid.NewGuid(), CreatedAtUtc: 902))
                     .ConfigureAwait(false);

        var variant = AssertEx.NotNull(await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversation.ConversationId,
                                                        originalAssistantId,
                                                        Guid.NewGuid(),
                                                        Guid.NewGuid(),
                                                        CreatedAtUtc: 903))
                                                    .ConfigureAwait(false));

        AssertEx.Equal(originalAssistantId, variant.OriginalMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, variant.Variant.Status);
        AssertEx.Equal(originalAssistantId, variant.Variant.ParentMessageId);

        // Both the original and the new sibling now share one variant group, listed together.
        var variants = await service.ListMessageVariantsAsync(conversation.ConversationId, originalAssistantId).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, variants.Count);
        AssertEx.True(variants.All(message => message.VariantGroupId == variant.VariantGroupId));
        AssertEx.Contains(variants.Select(message => message.MessageId), originalAssistantId);
        AssertEx.Contains(variants.Select(message => message.MessageId), variant.Variant.MessageId);
    }

    [Test]
    public async Task CreateMessageVariantAsync_WhenOriginalMissing_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("variant-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Variants", "node", CreatedAtUtc: 910)).ConfigureAwait(false);

        AssertEx.Null(await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversation.ConversationId,
                                       Guid.NewGuid(),
                                       Guid.NewGuid(),
                                       Guid.NewGuid(),
                                       CreatedAtUtc: 911))
                                   .ConfigureAwait(false));
    }

    [Test]
    public async Task SetMessageFeedbackAsync_UpsertsRatingAndComment()
    {
        await using var provider = await BuildProviderAsync("feedback.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", CreatedAtUtc: 1000)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, messageId, Guid.NewGuid(), CreatedAtUtc: 1001)).ConfigureAwait(false);

        var created = await service
                            .SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, NodeChatFeedbackRatingValues.Up, "  great  ", UpdatedAtUtc: 1002))
                            .ConfigureAwait(false);
        AssertEx.Equal(NodeChatFeedbackRatingValues.Up, created.Rating);
        AssertEx.Equal("great", created.Comment);
        AssertEx.Equal(expected: 1002L, created.CreatedAtUtc);

        // Re-submitting overwrites rating/comment but preserves the first-seen created_at_utc.
        var updated = await service
                            .SetMessageFeedbackAsync(
                                new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, NodeChatFeedbackRatingValues.Down, Comment: null, UpdatedAtUtc: 1003))
                            .ConfigureAwait(false);
        AssertEx.Equal(NodeChatFeedbackRatingValues.Down, updated.Rating);
        AssertEx.Null(updated.Comment);
        AssertEx.Equal(expected: 1002L, updated.CreatedAtUtc);
        AssertEx.Equal(expected: 1003L, updated.UpdatedAtUtc);

        var loaded = AssertEx.NotNull(await service.GetMessageFeedbackAsync(conversation.ConversationId, messageId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatFeedbackRatingValues.Down, loaded.Rating);
    }

    [Test]
    public async Task GetConversationAsync_CarriesFeedbackStateInlineOnMessages()
    {
        await using var provider = await BuildProviderAsync("feedback-inline.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", CreatedAtUtc: 1200)).ConfigureAwait(false);

        // Two assistant turns: one with stored feedback, one without.
        var ratedMessageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, ratedMessageId, Guid.NewGuid(), CreatedAtUtc: 1201))
                     .ConfigureAwait(false);
        var unratedMessageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, unratedMessageId, Guid.NewGuid(), CreatedAtUtc: 1202))
                     .ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, ratedMessageId, NodeChatFeedbackRatingValues.Up, "spot on", UpdatedAtUtc: 1203))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));

        var rated = loaded.Messages.Single(message => message.MessageId == ratedMessageId);
        AssertEx.Equal(NodeChatFeedbackRatingValues.Up, rated.FeedbackRating);
        AssertEx.Equal("spot on", rated.FeedbackComment);

        var unrated = loaded.Messages.Single(message => message.MessageId == unratedMessageId);
        AssertEx.Null(unrated.FeedbackRating);
        AssertEx.Null(unrated.FeedbackComment);
    }

    [Test]
    public async Task GetMessageFeedbackAsync_WhenNone_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("feedback-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", CreatedAtUtc: 1010)).ConfigureAwait(false);

        AssertEx.Null(await service.GetMessageFeedbackAsync(conversation.ConversationId, Guid.NewGuid()).ConfigureAwait(false));
    }

    [Test]
    public async Task DeleteConversationAsync_WhenPurging_RemovesMessageFeedbackRows()
    {
        await using var provider = await BuildProviderAsync("feedback-purge.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", CreatedAtUtc: 1100)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, messageId, Guid.NewGuid(), CreatedAtUtc: 1101)).ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, NodeChatFeedbackRatingValues.Up, "keep private", UpdatedAtUtc: 1102))
                     .ConfigureAwait(false);

        // SQLite ON DELETE CASCADE is not enforced (no PRAGMA foreign_keys=ON), so the purge must delete the
        // feedback row explicitly or plaintext feedback orphans after the conversation is gone (privacy gap).
        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, DeletedAtUtc: 1103, PurgeImmediately: true)).ConfigureAwait(false);

        AssertEx.Null(await service.GetMessageFeedbackAsync(conversation.ConversationId, messageId).ConfigureAwait(false));
    }

    [Test]
    public async Task SelectedPath_RoundTripsMapAndClearsOnEmpty()
    {
        await using var provider = await BuildProviderAsync("selected-path.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Selected path", "node", CreatedAtUtc: 1300)).ConfigureAwait(false);

        // No selection persisted yet -> read returns null.
        AssertEx.Null(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));

        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();
        var chosenA = Guid.NewGuid();
        var chosenB = Guid.NewGuid();
        var map = new Dictionary<Guid, Guid>
        {
            [groupA] = chosenA,
            [groupB] = chosenB
        };

        var persisted = await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, map, UpdatedAtUtc: 1301)).ConfigureAwait(false);
        AssertEx.Equal(expected: 2, persisted.Count);
        AssertEx.Equal(chosenA, persisted[groupA]);
        AssertEx.Equal(chosenB, persisted[groupB]);

        // Read back via the dedicated getter.
        var read = AssertEx.NotNull(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(expected: 2, read.Count);
        AssertEx.Equal(chosenA, read[groupA]);
        AssertEx.Equal(chosenB, read[groupB]);

        // Read back via the conversation DTO (GetConversationAsync surfaces the parsed map).
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var loadedPath = AssertEx.NotNull(loaded.SelectedPath);
        AssertEx.Equal(chosenA, loadedPath[groupA]);

        // An empty map clears the stored selection back to null.
        var cleared = await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, new Dictionary<Guid, Guid>(), UpdatedAtUtc: 1302)).ConfigureAwait(false);
        AssertEx.Empty(cleared);
        AssertEx.Null(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Null(AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false)).SelectedPath);

        // A null map is treated the same as empty (clears).
        await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, map, UpdatedAtUtc: 1303)).ConfigureAwait(false);
        await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, SelectedPath: null, UpdatedAtUtc: 1304)).ConfigureAwait(false);
        AssertEx.Null(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));
    }

    [Test]
    public async Task GetSelectedPathAsync_WhenConversationMissing_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("selected-path-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        AssertEx.Null(await service.GetSelectedPathAsync(Guid.NewGuid()).ConfigureAwait(false));
    }

    // resetDatabase:false reopens an existing DB file with fresh connections without wiping it — used to model a genuine
    // process restart against a database left behind by a previous provider/service instance.
    private async Task<ServiceProvider> BuildProviderAsync(string fileName, bool resetDatabase = true)
    {
        var databasePath = GetDatabasePath(fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        if (resetDatabase)
        {
            await using var scope = provider.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
            await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
            await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);
        }

        return provider;
    }

    private static NodeChatPersistenceService CreateService(ServiceProvider provider)
    {
        return new NodeChatPersistenceService(provider.GetRequiredService<NodeChatPersistenceWriter>());
    }

    private string GetDatabasePath(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        return Path.Combine(_rootPath, fileName);
    }

    private static async Task<string> ReadMessageOriginAsync(ServiceProvider provider, Guid messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT origin FROM messages WHERE message_id = $message_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$message_id";
        parameter.Value = messageId;
        command.Parameters.Add(parameter);
        var origin = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (string)origin!;
    }

    private static async Task OverwriteMetadataJsonAsync(ServiceProvider provider, Guid messageId, string metadataJson)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET metadata_json = $metadata_json WHERE message_id = $message_id;";
        // The metadata_json column is written/read as raw UTF-8 bytes via ADO.NET (no EF interceptor on this path),
        // so writing plaintext UTF-8 here mirrors how the service persists the blob under the null key holder.
        var metadataParameter = command.CreateParameter();
        metadataParameter.ParameterName = "$metadata_json";
        metadataParameter.Value = Encoding.UTF8.GetBytes(metadataJson);
        command.Parameters.Add(metadataParameter);
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$message_id";
        idParameter.Value = messageId;
        command.Parameters.Add(idParameter);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadRawMessageContentAsync(ServiceProvider provider, Guid messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT content FROM messages WHERE message_id = $message_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$message_id";
        parameter.Value = messageId;
        command.Parameters.Add(parameter);
        var content = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return (byte[])content!;
    }

    private static async Task WriteRawMessageContentAsync(ServiceProvider provider, Guid messageId, byte[] content)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE messages SET content = $content WHERE message_id = $message_id;";
        var contentParameter = command.CreateParameter();
        contentParameter.ParameterName = "$content";
        contentParameter.Value = content;
        command.Parameters.Add(contentParameter);
        var idParameter = command.CreateParameter();
        idParameter.ParameterName = "$message_id";
        idParameter.Value = messageId;
        command.Parameters.Add(idParameter);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Guid>> ListBranchedConversationIdsAsync(ServiceProvider provider, Guid sourceConversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id FROM conversations WHERE branch_of_conversation_id = $source_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$source_id";
        parameter.Value = sourceConversationId;
        command.Parameters.Add(parameter);

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<int> CountMessagesAsync(ServiceProvider provider, Guid conversationId)
    {
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM messages WHERE conversation_id = $conversation_id;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$conversation_id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    // The backfill service's durable reclamation marker lives in the modeled chat_maintenance_state table (created by
    // EnsureCreated here / by migration in prod). These helpers poke it directly by its stable contract (table + marker
    // name), independent of the service's private members.
    private const string ReclamationMarkerName = "content_encryption_reclaim_pending";

    private static async Task SetReclamationMarkerRawAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO chat_maintenance_state (name, value) VALUES ($name, '1') ON CONFLICT(name) DO UPDATE SET value = '1';";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = ReclamationMarkerName;
        command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<bool> IsReclamationMarkerSetRawAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM chat_maintenance_state WHERE name = $name);";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = ReclamationMarkerName;
        command.Parameters.Add(parameter);
        var result = await command.ExecuteScalarAsync().ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    // Switches the DB to WAL journal mode (persisted in the file header), so a concurrent reader can hold a snapshot
    // that makes wal_checkpoint(TRUNCATE) report busy.
    private static async Task SetJournalModeWalAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var connection = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>().Database.GetDbConnection();
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    // Opens a dedicated connection holding an open read transaction. In WAL mode this pins a read snapshot, so a
    // wal_checkpoint(TRUNCATE) on another connection cannot truncate the log and returns busy. Dispose to release it.
    private static async Task<Microsoft.Data.Sqlite.SqliteConnection> OpenBlockingReaderAsync(string databasePath)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "BEGIN; SELECT COUNT(*) FROM messages;";
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        return connection;
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (var start = 0; start <= haystack.Length - needle.Length; start++)
        {
            var match = true;
            for (var offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
