namespace XE_Local_AI_Engine.Tests.Chat;

using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeChatPersistenceServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    [Test]
    public async Task Messages_FollowAcceptedLifecycleAndPartialPersistence()
    {
        await using var provider = await BuildProviderAsync("lifecycle.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", 10)).ConfigureAwait(false);
        var userMessageId = Guid.NewGuid();
        var assistantMessageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, requestId);

        var user = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userMessageId, " hello ", 11)).ConfigureAwait(false);
        var placeholder = await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, requestId, 12, "llama"))
                                       .ConfigureAwait(false);
        var streaming = await service.MarkAssistantStreamingAsync(correlation, 13).ConfigureAwait(false);
        var partial = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, "Hello", "thinking", 14)).ConfigureAwait(false);
        var appended = await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, " world", null, 15, false)).ConfigureAwait(false);
        var completed = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                                         NodeChatMessageStatusValues.Completed,
                                         16,
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
        AssertEx.Equal(16L, completed.UpdatedAtUtc);
        AssertEx.Equal(13, completed.TotalCount);

        var loaded = await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false);
        var messages = AssertEx.NotNull(loaded).Messages;
        AssertEx.Equal(2, messages.Count);
        AssertEx.Equal(userMessageId, messages[0].MessageId);
        AssertEx.Equal(assistantMessageId, messages[1].MessageId);
        AssertEx.Equal("Hello world", messages[1].Content);
        AssertEx.Equal(10, messages[1].InputCount);
        AssertEx.Equal(3, messages[1].OutputCount);
        AssertEx.Equal(13, messages[1].TotalCount);
        AssertEx.Equal(1, messages[1].ReasoningCount);
    }

    [Test]
    public async Task TerminalizeAssistantMessageAsync_WithParts_RoundTripsOrderedInterleave()
    {
        await using var provider = await BuildProviderAsync("parts-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Parts", "node", 2000)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 2001))
                     .ConfigureAwait(false);

        // reasoning -> tool -> reasoning: a tool call between two reasoning runs is the Option A interleave that
        // produces a second Thoughts block. The tool part carries args + result (the completed-phase data).
        var parts = new List<NodeChatMessagePart>
        {
            new(NodeChatMessagePartKinds.Reasoning, 0, Text: "thinking before"),
            new(NodeChatMessagePartKinds.Tool, 1, ToolCallId: "call-1", Name: "GetCurrentTime", State: NodeChatToolPartStates.Received, Args: "{\"tz\":\"UTC\"}", Result: "2026-06-01T00:00:00Z"),
            new(NodeChatMessagePartKinds.Reasoning, 2, Text: "thinking after")
        };

        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         2002,
                         "the answer",
                         "thinking before\nthinking after",
                         Parts: parts))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);
        var loadedParts = AssertEx.NotNull(assistant.Parts);

        AssertEx.Equal(3, loadedParts.Count);
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy", "node", 2100)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 2101))
                     .ConfigureAwait(false);

        // Terminalize WITHOUT parts (the pre-parts shape): the serialized metadata omits the parts key entirely.
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         2102,
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Attribution", "node", 3000)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        var agentDefinitionId = Guid.NewGuid();

        // The placeholder is stamped with the per-response agent attribution at send time; it must survive the
        // streaming/terminalize updates (which preserve it from current) and reload off the metadata blob.
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId,
                         assistantMessageId,
                         correlation.RequestId,
                         3001,
                         "model-x",
                         AgentDefinitionId: agentDefinitionId,
                         AgentName: "Backend Buddy"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, 3002).ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         3003,
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy attribution", "node", 3100)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 3101))
                     .ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, 3102, "legacy answer", "legacy reasoning"))
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Reasoning effort", "node", 3200)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        // The placeholder is stamped with the reasoning effort used to drive the turn; it must survive the
        // streaming/terminalize updates (which preserve it from current) and reload off the metadata blob.
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId,
                         assistantMessageId,
                         correlation.RequestId,
                         3201,
                         "model-x",
                         ReasoningEffort: "high"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, 3202).ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         3203,
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy reasoning effort", "node", 3300)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 3301))
                     .ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, 3302, "legacy answer", "legacy reasoning"))
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Generation duration", "node", 3400)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 3401, "model-x"))
                     .ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, 3402).ConfigureAwait(false);
        // The runner reports the whole-turn duration at terminalize; it rides the metadata blob (no DB column) and
        // must survive reload alongside the token counts that feed the tokens-per-second display.
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         3403,
                         "the answer",
                         "thinking",
                         Model: "model-x",
                         OutputCount: 42,
                         GenerationDurationMs: 2000))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var assistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);

        AssertEx.Equal(2000L, assistant.GenerationDurationMs);
        AssertEx.Equal(42, assistant.OutputCount);
        AssertEx.Equal("the answer", assistant.Content);
    }

    [Test]
    public async Task Metadata_LegacyBlobWithoutGenerationDurationMs_DeserializesNull()
    {
        await using var provider = await BuildProviderAsync("generation-duration-legacy.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Legacy generation duration", "node", 3500)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 3501))
                     .ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation, NodeChatMessageStatusValues.Completed, 3502, "legacy answer", "legacy reasoning"))
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Cancel", null, 20)).ConfigureAwait(false);
        var targetMessageId = Guid.NewGuid();
        var otherMessageId = Guid.NewGuid();
        var targetCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, targetMessageId, Guid.NewGuid());
        var otherCorrelation = new NodeChatMessageCorrelation(conversation.ConversationId, otherMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, targetMessageId, targetCorrelation.RequestId, 21))
                     .ConfigureAwait(false);
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, otherMessageId, otherCorrelation.RequestId, 22)).ConfigureAwait(false);

        var cancel = await service.CancelMessageAsync(new NodeChatCancelRequest(targetCorrelation, 23)).ConfigureAwait(false);

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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Failure", null, 24)).ConfigureAwait(false);
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversation.ConversationId, assistantMessageId, Guid.NewGuid());

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, assistantMessageId, correlation.RequestId, 25)).ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, 26).ConfigureAwait(false);
        await service.FlushAssistantPartialAsync(new NodeChatPartialFlushRequest(correlation, "partial answer", "partial reasoning", 27)).ConfigureAwait(false);

        var failed = await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
            NodeChatMessageStatusValues.Failed,
            28,
            "partial answer",
            "partial reasoning",
            "local-chat-stream-failed")).ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var loadedAssistant = loaded.Messages.Single(message => message.MessageId == assistantMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Failed, failed.Status);
        AssertEx.Equal("partial answer", loadedAssistant.Content);
        AssertEx.Equal("partial reasoning", loadedAssistant.Reasoning);
        AssertEx.Equal("local-chat-stream-failed", loadedAssistant.Error);
        AssertEx.Equal(28L, loadedAssistant.UpdatedAtUtc);
    }

    [Test]
    public async Task ListAndGet_ExcludePurgedConversationsAndOrderMessagesBySequence()
    {
        await using var provider = await BuildProviderAsync("list.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var keep = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Keep", null, 30)).ConfigureAwait(false);
        var purge = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Purge", null, 31)).ConfigureAwait(false);

        var second = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(keep.ConversationId, Guid.NewGuid(), "second visible", 33)).ConfigureAwait(false);
        var first = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(keep.ConversationId, Guid.NewGuid(), "first visible", 32)).ConfigureAwait(false);
        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(purge.ConversationId, 34)).ConfigureAwait(false);

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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Delete", null, 40)).ConfigureAwait(false);

        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, Guid.NewGuid(), Guid.NewGuid(), 41)).ConfigureAwait(false);

        var result = await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, 42)).ConfigureAwait(false);
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
                                       100,
                                       NodeChatOriginValues.Remote))
                                   .ConfigureAwait(false);

        AssertEx.Equal(conversationId, ensured.ConversationId);
        AssertEx.Equal("Remote thread", ensured.Title);
        AssertEx.Equal(NodeChatOriginValues.Remote, ensured.Origin);
        AssertEx.Equal(100L, ensured.CreatedAtUtc);

        var loaded = await service.GetConversationAsync(conversationId).ConfigureAwait(false);
        AssertEx.Equal(NodeChatOriginValues.Remote, AssertEx.NotNull(loaded).Origin);
    }

    [Test]
    public async Task EnsureConversationAsync_WhenConversationExists_ReturnsExistingRowWithoutOverwriting()
    {
        await using var provider = await BuildProviderAsync("ensure-existing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var created = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Original", "node", 200)).ConfigureAwait(false);

        var ensured = await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(created.ConversationId,
                                       "Should be ignored",
                                       "other",
                                       999,
                                       NodeChatOriginValues.Remote))
                                   .ConfigureAwait(false);

        // Existing rows are never overwritten: title/origin/timestamps from the original CreateConversationAsync persist.
        AssertEx.Equal("Original", ensured.Title);
        AssertEx.Equal(NodeChatOriginValues.Local, ensured.Origin);
        AssertEx.Equal(200L, ensured.CreatedAtUtc);
    }

    [Test]
    public async Task EnsureConversationAsync_WhenCalledTwice_IsIdempotentAndDoesNotDuplicate()
    {
        await using var provider = await BuildProviderAsync("ensure-idempotent.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversationId = Guid.NewGuid();
        var request = new NodeChatEnsureConversationRequest(conversationId, "Remote thread", "node", 300, NodeChatOriginValues.Remote);

        var first = await service.EnsureConversationAsync(request).ConfigureAwait(false);
        var second = await service.EnsureConversationAsync(request with
        {
            Title = "Different",
            CreatedAtUtc = 400
        }).ConfigureAwait(false);

        AssertEx.Equal(first.ConversationId, second.ConversationId);
        AssertEx.Equal("Remote thread", second.Title);
        AssertEx.Equal(300L, second.CreatedAtUtc);

        var summaries = await service.ListConversationsAsync(new NodeChatListConversationsRequest(true)).ConfigureAwait(false);
        AssertEx.Equal(1, summaries.Count(summary => summary.ConversationId == conversationId));
    }

    [Test]
    public async Task OriginColumn_RoundTripsLocalViaLocalPathAndRemoteViaPlatformPath()
    {
        await using var provider = await BuildProviderAsync("origin-roundtrip.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        // Local path: CreateConversationAsync defaults Origin=Local; PersistUserMessageAsync defaults Origin=Local.
        var localConversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local chat", "node", 1)).ConfigureAwait(false);
        var localMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(localConversation.ConversationId, localMessageId, "local question", 2)).ConfigureAwait(false);

        // Platform (remote) path: EnsureConversationAsync mirrors the conversation Origin=Remote; the user turn is
        // persisted with Origin=Remote, exactly as NodeChatRemotePersistenceCoordinator drives it.
        var remoteConversationId = Guid.NewGuid();
        await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(remoteConversationId, "Remote chat", "node", 3, NodeChatOriginValues.Remote)).ConfigureAwait(false);
        var remoteMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(remoteConversationId, remoteMessageId, "remote question", 4, Origin: NodeChatOriginValues.Remote))
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
    public async Task RemoteMessageContent_IsStoredAsPlaintextUtf8AtRest()
    {
        await using var provider = await BuildProviderAsync("remote-plaintext.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        const string plaintext = "remote at-rest plaintext content";

        // Persist an Origin=Remote message via the raw-SQL persistence path (PersistUserMessageAsync issues a
        // direct ADO.NET INSERT, never EF SaveChanges, so no EF interceptor touches the content column).
        var remoteConversationId = Guid.NewGuid();
        await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(remoteConversationId, "Remote", "node", 10, NodeChatOriginValues.Remote)).ConfigureAwait(false);
        var remoteMessageId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(remoteConversationId, remoteMessageId, plaintext, 11, Origin: NodeChatOriginValues.Remote)).ConfigureAwait(false);

        // Read the raw content column with a direct SQL command (bypassing the service's Decode path and any
        // interceptor) and assert it is the original UTF-8 plaintext. This DELIBERATELY documents the
        // plaintext-at-rest posture for remote-origin rows (F8 / schema sheet): it is intentional, not a bug.
        var rawContent = await ReadRawMessageContentAsync(provider, remoteMessageId).ConfigureAwait(false);

        AssertEx.True(rawContent.SequenceEqual(Encoding.UTF8.GetBytes(plaintext)), "Remote-origin content is stored as plaintext UTF-8 at rest by design.");
        AssertEx.Equal(plaintext, Encoding.UTF8.GetString(rawContent));
    }

    [Test]
    public async Task RenamePinArchive_PersistMappedColumnsAndArchivedConversationsAreHiddenUnlessRequested()
    {
        await using var provider = await BuildProviderAsync("rename-pin-archive.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Original", "node", 600)).ConfigureAwait(false);

        var renamed = AssertEx.NotNull(await service.RenameConversationAsync(new NodeChatRenameConversationRequest(conversation.ConversationId, "  Renamed  ", 601)).ConfigureAwait(false));
        AssertEx.Equal("Renamed", renamed.Title);

        var pinned = AssertEx.NotNull(await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(conversation.ConversationId, true, 602)).ConfigureAwait(false));
        AssertEx.True(pinned.IsPinned);

        // Active listing keeps a pinned, unarchived conversation visible.
        var active = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        AssertEx.Contains(active.Select(summary => summary.ConversationId), conversation.ConversationId);
        AssertEx.True(active.Single(summary => summary.ConversationId == conversation.ConversationId).IsPinned);

        // Archiving hides it from the default (active) listing but not from the include-archived listing.
        var archived = AssertEx.NotNull(await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(conversation.ConversationId, true, 603)).ConfigureAwait(false));
        AssertEx.True(archived.Archived);

        var activeAfterArchive = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        AssertEx.False(activeAfterArchive.Any(summary => summary.ConversationId == conversation.ConversationId));

        var includingArchived = await service.ListConversationsAsync(new NodeChatListConversationsRequest(true)).ConfigureAwait(false);
        var listedArchived = includingArchived.Single(summary => summary.ConversationId == conversation.ConversationId);
        AssertEx.True(listedArchived.Archived);
        AssertEx.True(listedArchived.IsPinned);

        // Unarchiving restores it to the active listing.
        await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(conversation.ConversationId, false, 604)).ConfigureAwait(false);
        var activeAfterUnarchive = await service.ListConversationsAsync(new NodeChatListConversationsRequest()).ConfigureAwait(false);
        AssertEx.Contains(activeAfterUnarchive.Select(summary => summary.ConversationId), conversation.ConversationId);
    }

    [Test]
    public async Task RenamePinArchive_WhenConversationMissing_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("rename-pin-archive-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var missingId = Guid.NewGuid();

        AssertEx.Null(await service.RenameConversationAsync(new NodeChatRenameConversationRequest(missingId, "Nope", 700)).ConfigureAwait(false));
        AssertEx.Null(await service.SetConversationPinnedAsync(new NodeChatSetConversationPinnedRequest(missingId, true, 701)).ConfigureAwait(false));
        AssertEx.Null(await service.SetConversationArchivedAsync(new NodeChatSetConversationArchivedRequest(missingId, true, 702)).ConfigureAwait(false));
    }

    [Test]
    public async Task MutationGuard_AllowsLocalRejectsRemoteAndIgnoresMissing()
    {
        await using var provider = await BuildProviderAsync("guard.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var guard = new NodeChatMutationGuard(service);

        var local = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Local", "node", 500)).ConfigureAwait(false);
        var remoteId = Guid.NewGuid();
        await service.EnsureConversationAsync(new NodeChatEnsureConversationRequest(remoteId, "Remote", "node", 501, NodeChatOriginValues.Remote)).ConfigureAwait(false);

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
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", 800)).ConfigureAwait(false);

        var first = await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, Guid.NewGuid(), "first", 801)).ConfigureAwait(false);
        var assistantId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(source.ConversationId, assistantId, Guid.NewGuid(), 802)).ConfigureAwait(false);
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, Guid.NewGuid(), "after cutoff", 803)).ConfigureAwait(false);

        // Branch at the assistant message (sequence 1): only the first two messages should be copied.
        var branch = AssertEx.NotNull(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, assistantId, 810)).ConfigureAwait(false));
        AssertEx.Equal(2, branch.CopiedMessageCount);
        AssertEx.Equal(source.ConversationId, branch.SourceConversationId);

        var branched = AssertEx.NotNull(await service.GetConversationAsync(branch.BranchedConversationId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatOriginValues.Local, branched.Origin);
        AssertEx.Equal(source.ConversationId, branched.BranchOfConversationId);
        AssertEx.Equal(2, branched.Messages.Count);
        AssertEx.Equal("first", branched.Messages[0].Content);
        // Copies are fresh rows: the branch does not reuse the source message ids.
        AssertEx.False(branched.Messages.Any(message => message.MessageId == first.MessageId || message.MessageId == assistantId));
    }

    [Test]
    public async Task BranchConversationAsync_FromChosenRevision_CopiesOnlyThatVariantAsLinearThread()
    {
        await using var provider = await BuildProviderAsync("branch-revision.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", 840)).ConfigureAwait(false);

        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(source.ConversationId, Guid.NewGuid(), "question", 841)).ConfigureAwait(false);
        var originalAssistantId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(source.ConversationId, originalAssistantId, Guid.NewGuid(), 842)).ConfigureAwait(false);

        // Regenerate => a newer sibling variant (the 2nd revision) sharing the original's variant group.
        var variant = AssertEx.NotNull(await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(source.ConversationId, originalAssistantId, Guid.NewGuid(), Guid.NewGuid(), 843))
                                                    .ConfigureAwait(false));

        // Branch from the chosen (newer) revision: the branch must be a LINEAR thread carrying only that revision
        // as the assistant turn — the sibling original must NOT be copied (otherwise variant_group_id is dropped
        // on copy and the two revisions render as duplicate stacked assistant turns). RC variant-branch fix.
        var branch = AssertEx.NotNull(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, variant.Variant.MessageId, 850)).ConfigureAwait(false));
        AssertEx.Equal(2, branch.CopiedMessageCount);

        var branched = AssertEx.NotNull(await service.GetConversationAsync(branch.BranchedConversationId).ConfigureAwait(false));
        AssertEx.Equal(2, branched.Messages.Count);
        AssertEx.Equal(1, branched.Messages.Count(message => string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)));
        AssertEx.Equal("question", branched.Messages.Single(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)).Content);
        // The branched assistant turn is no longer a variant — a fresh linear thread.
        AssertEx.True(branched.Messages.All(message => message.VariantGroupId is null));
    }

    [Test]
    public async Task BranchConversationAsync_WhenMessageNotInConversation_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("branch-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var source = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Source", "node", 820)).ConfigureAwait(false);

        AssertEx.Null(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(source.ConversationId, Guid.NewGuid(), 821)).ConfigureAwait(false));
        AssertEx.Null(await service.BranchConversationAsync(new NodeChatBranchConversationRequest(Guid.NewGuid(), Guid.NewGuid(), 822)).ConfigureAwait(false));
    }

    [Test]
    public async Task CreateMessageVariantAsync_CreatesSiblingSharingVariantGroupAndBackstampsOriginal()
    {
        await using var provider = await BuildProviderAsync("variant.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Variants", "node", 900)).ConfigureAwait(false);
        var userId = Guid.NewGuid();
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversation.ConversationId, userId, "question", 901)).ConfigureAwait(false);
        var originalAssistantId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, originalAssistantId, Guid.NewGuid(), 902)).ConfigureAwait(false);

        var variant = AssertEx.NotNull(await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversation.ConversationId,
                                                        originalAssistantId,
                                                        Guid.NewGuid(),
                                                        Guid.NewGuid(),
                                                        903))
                                                    .ConfigureAwait(false));

        AssertEx.Equal(originalAssistantId, variant.OriginalMessageId);
        AssertEx.Equal(NodeChatMessageStatusValues.Pending, variant.Variant.Status);
        AssertEx.Equal(originalAssistantId, variant.Variant.ParentMessageId);

        // Both the original and the new sibling now share one variant group, listed together.
        var variants = await service.ListMessageVariantsAsync(conversation.ConversationId, originalAssistantId).ConfigureAwait(false);
        AssertEx.Equal(2, variants.Count);
        AssertEx.True(variants.All(message => message.VariantGroupId == variant.VariantGroupId));
        AssertEx.Contains(variants.Select(message => message.MessageId), originalAssistantId);
        AssertEx.Contains(variants.Select(message => message.MessageId), variant.Variant.MessageId);
    }

    [Test]
    public async Task CreateMessageVariantAsync_WhenOriginalMissing_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("variant-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Variants", "node", 910)).ConfigureAwait(false);

        AssertEx.Null(await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversation.ConversationId,
                                       Guid.NewGuid(),
                                       Guid.NewGuid(),
                                       Guid.NewGuid(),
                                       911))
                                   .ConfigureAwait(false));
    }

    [Test]
    public async Task SetMessageFeedbackAsync_UpsertsRatingAndComment()
    {
        await using var provider = await BuildProviderAsync("feedback.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", 1000)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, messageId, Guid.NewGuid(), 1001)).ConfigureAwait(false);

        var created = await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, NodeChatFeedbackRatingValues.Up, "  great  ", 1002))
                                   .ConfigureAwait(false);
        AssertEx.Equal(NodeChatFeedbackRatingValues.Up, created.Rating);
        AssertEx.Equal("great", created.Comment);
        AssertEx.Equal(1002L, created.CreatedAtUtc);

        // Re-submitting overwrites rating/comment but preserves the first-seen created_at_utc.
        var updated = await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, NodeChatFeedbackRatingValues.Down, null, 1003))
                                   .ConfigureAwait(false);
        AssertEx.Equal(NodeChatFeedbackRatingValues.Down, updated.Rating);
        AssertEx.Null(updated.Comment);
        AssertEx.Equal(1002L, updated.CreatedAtUtc);
        AssertEx.Equal(1003L, updated.UpdatedAtUtc);

        var loaded = AssertEx.NotNull(await service.GetMessageFeedbackAsync(conversation.ConversationId, messageId).ConfigureAwait(false));
        AssertEx.Equal(NodeChatFeedbackRatingValues.Down, loaded.Rating);
    }

    [Test]
    public async Task GetConversationAsync_CarriesFeedbackStateInlineOnMessages()
    {
        await using var provider = await BuildProviderAsync("feedback-inline.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", 1200)).ConfigureAwait(false);

        // Two assistant turns: one with stored feedback, one without.
        var ratedMessageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, ratedMessageId, Guid.NewGuid(), 1201)).ConfigureAwait(false);
        var unratedMessageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, unratedMessageId, Guid.NewGuid(), 1202)).ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, ratedMessageId, NodeChatFeedbackRatingValues.Up, "spot on", 1203))
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
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", 1010)).ConfigureAwait(false);

        AssertEx.Null(await service.GetMessageFeedbackAsync(conversation.ConversationId, Guid.NewGuid()).ConfigureAwait(false));
    }

    [Test]
    public async Task DeleteConversationAsync_WhenPurging_RemovesMessageFeedbackRows()
    {
        await using var provider = await BuildProviderAsync("feedback-purge.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Feedback", "node", 1100)).ConfigureAwait(false);
        var messageId = Guid.NewGuid();
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversation.ConversationId, messageId, Guid.NewGuid(), 1101)).ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(conversation.ConversationId, messageId, NodeChatFeedbackRatingValues.Up, "keep private", 1102))
                     .ConfigureAwait(false);

        // SQLite ON DELETE CASCADE is not enforced (no PRAGMA foreign_keys=ON), so the purge must delete the
        // feedback row explicitly or plaintext feedback orphans after the conversation is gone (privacy gap).
        await service.DeleteConversationAsync(new NodeChatDeleteConversationRequest(conversation.ConversationId, 1103, PurgeImmediately: true)).ConfigureAwait(false);

        AssertEx.Null(await service.GetMessageFeedbackAsync(conversation.ConversationId, messageId).ConfigureAwait(false));
    }

    [Test]
    public async Task SelectedPath_RoundTripsMapAndClearsOnEmpty()
    {
        await using var provider = await BuildProviderAsync("selected-path.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Selected path", "node", 1300)).ConfigureAwait(false);

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

        var persisted = await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, map, 1301)).ConfigureAwait(false);
        AssertEx.Equal(2, persisted.Count);
        AssertEx.Equal(chosenA, persisted[groupA]);
        AssertEx.Equal(chosenB, persisted[groupB]);

        // Read back via the dedicated getter.
        var read = AssertEx.NotNull(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Equal(2, read.Count);
        AssertEx.Equal(chosenA, read[groupA]);
        AssertEx.Equal(chosenB, read[groupB]);

        // Read back via the conversation DTO (GetConversationAsync surfaces the parsed map).
        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false));
        var loadedPath = AssertEx.NotNull(loaded.SelectedPath);
        AssertEx.Equal(chosenA, loadedPath[groupA]);

        // An empty map clears the stored selection back to null.
        var cleared = await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, new Dictionary<Guid, Guid>(), 1302)).ConfigureAwait(false);
        AssertEx.Empty(cleared);
        AssertEx.Null(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));
        AssertEx.Null(AssertEx.NotNull(await service.GetConversationAsync(conversation.ConversationId).ConfigureAwait(false)).SelectedPath);

        // A null map is treated the same as empty (clears).
        await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, map, 1303)).ConfigureAwait(false);
        await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversation.ConversationId, null, 1304)).ConfigureAwait(false);
        AssertEx.Null(await service.GetSelectedPathAsync(conversation.ConversationId).ConfigureAwait(false));
    }

    [Test]
    public async Task GetSelectedPathAsync_WhenConversationMissing_ReturnsNull()
    {
        await using var provider = await BuildProviderAsync("selected-path-missing.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);

        AssertEx.Null(await service.GetSelectedPathAsync(Guid.NewGuid()).ConfigureAwait(false));
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        var databasePath = GetDatabasePath(fileName);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<NodeChatPersistenceWriter>();

        var provider = services.BuildServiceProvider(true);
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        await dbContext.Database.EnsureDeletedAsync().ConfigureAwait(false);
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

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
}
