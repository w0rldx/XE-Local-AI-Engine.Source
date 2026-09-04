namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Guards the load-side cap behind <see cref="INodeChatPersistenceService.GetConversationForTurnAsync" />: it may
///     skip decrypting payloads the turn provably discards, and nothing else. The equivalence claim is exercised on the
///     hardest shape — a BRANCHED conversation (a variant group whose siblings straddle the compaction boundary, with
///     the OLDER sibling pinned) that also carries a compaction synopsis — because that is where a naive
///     "drop everything below the boundary before resolving" cap would silently change the selected path.
/// </summary>
public sealed class NodeChatTurnReadCapTests : IDisposable
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
    public async Task TurnRead_OnBranchedCompactedConversation_BuildsTheIdenticalContextAsTheFullRead()
    {
        await using var provider = await BuildProviderAsync("turn-read-cap-branched.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var built = await BuildBranchedConversationAsync(service).ConfigureAwait(false);

        // The synopsis covers up to sequence 3, so the turn drops sequences 1-3 from the verbatim history. Set it LAST:
        // both SetSelectedPathAsync and CreateMessageVariantAsync deliberately invalidate a stored synopsis.
        await service.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest(built.ConversationId, "SYNOPSIS", CoversToSequence: 3, UpdatedAtUtc: 60))
                     .ConfigureAwait(false);

        var full = AssertEx.NotNull(await service.GetConversationAsync(built.ConversationId).ConfigureAwait(false));
        var turn = AssertEx.NotNull(await service.GetConversationForTurnAsync(built.ConversationId).ConfigureAwait(false));

        AssertEx.True(full.CompactionSummaryCoversToSequence == 3, "The full read must report the covered sequence.");
        AssertEx.True(turn.CompactionSummaryCoversToSequence == 3, "The turn read must report the same covered sequence.");

        // 1. STRUCTURE is byte-identical, so every input the selected-path resolver reads is intact.
        AssertEx.Equal(full.Messages.Count, turn.Messages.Count);
        for (var index = 0; index < full.Messages.Count; index++)
        {
            var expected = full.Messages[index];
            var actual = turn.Messages[index];
            AssertEx.Equal(expected.MessageId, actual.MessageId);
            AssertEx.Equal(expected.Sequence, actual.Sequence);
            AssertEx.Equal(expected.Role, actual.Role);
            AssertEx.Equal(expected.Status, actual.Status);
            AssertEx.Equal(expected.VariantGroupId, actual.VariantGroupId);
            AssertEx.Equal(expected.CreatedAtUtc, actual.CreatedAtUtc);
        }

        // 2. The resolver therefore picks the SAME messages — including honouring the pin on the older sibling, so the
        //    newest-sibling default is genuinely overridden and the cap did not quietly restore it.
        var fullPath = SelectedPathResolver.Resolve(full.Messages, full.SelectedPath).Select(message => message.MessageId).ToArray();
        var turnPath = SelectedPathResolver.Resolve(turn.Messages, turn.SelectedPath).Select(message => message.MessageId).ToArray();
        AssertEx.Equal(fullPath.Length, turnPath.Length);
        for (var index = 0; index < fullPath.Length; index++)
        {
            AssertEx.Equal(fullPath[index], turnPath[index]);
        }

        AssertEx.Contains(turnPath, built.PinnedOldSiblingId, "The pinned older sibling must stay on the selected path.");
        AssertEx.False(turnPath.Contains(built.NewerSiblingId), "The deselected newer sibling must stay off the selected path.");

        // 3. THE GUARANTEE: what the turn actually sends is identical either way.
        var fullContext = ProjectSentHistory(full);
        var turnContext = ProjectSentHistory(turn);
        AssertEx.Equal(fullContext.Count, turnContext.Count);
        for (var index = 0; index < fullContext.Count; index++)
        {
            AssertEx.Equal(fullContext[index], turnContext[index]);
        }

        // 4. Memory extraction reads user turns at ANY sequence, so user payloads are never capped.
        foreach (var user in turn.Messages.Where(message => string.Equals(message.Role, "user", StringComparison.Ordinal)))
        {
            var reference = full.Messages.Single(message => message.MessageId == user.MessageId);
            AssertEx.Equal(reference.Content, user.Content);
        }

        // 5. Non-vacuity: the cap must actually have skipped something, or every assertion above is trivially true.
        var cappedFull = full.Messages.Single(message => message.MessageId == built.PinnedOldSiblingId);
        var cappedTurn = turn.Messages.Single(message => message.MessageId == built.PinnedOldSiblingId);
        AssertEx.Equal("assistant-one", cappedFull.Content);
        AssertEx.Equal("reasoning-one", cappedFull.Reasoning);
        AssertEx.Equal(string.Empty, cappedTurn.Content);
        AssertEx.Null(cappedTurn.Reasoning);
        AssertEx.Null(cappedTurn.Model);
    }

    [Test]
    public async Task TurnRead_WithoutACompactionSynopsis_LoadsEverythingExactlyAsTheFullRead()
    {
        // The cap is gated on a non-empty synopsis AND a covered sequence — the same pair ConversationContextBuilder.Build gates
        // its own drop on — so an uncompacted conversation (the overwhelming majority) must be untouched.
        await using var provider = await BuildProviderAsync("turn-read-cap-uncompacted.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var built = await BuildBranchedConversationAsync(service).ConfigureAwait(false);

        var full = AssertEx.NotNull(await service.GetConversationAsync(built.ConversationId).ConfigureAwait(false));
        var turn = AssertEx.NotNull(await service.GetConversationForTurnAsync(built.ConversationId).ConfigureAwait(false));

        AssertEx.Null(turn.CompactionSummary);
        AssertEx.Equal(full.Messages.Count, turn.Messages.Count);
        for (var index = 0; index < full.Messages.Count; index++)
        {
            AssertEx.Equal(full.Messages[index].Content, turn.Messages[index].Content);
            AssertEx.Equal<string?>(full.Messages[index].Reasoning, turn.Messages[index].Reasoning);
            AssertEx.Equal<string?>(full.Messages[index].Model, turn.Messages[index].Model);
        }

        AssertEx.Equal("assistant-one", turn.Messages.Single(message => message.MessageId == built.PinnedOldSiblingId).Content);
    }

    [Test]
    public async Task TurnRead_KeepsPayloadsOfASelectedSiblingAboveTheBoundary()
    {
        // The newest sibling sits at sequence 5, ABOVE the boundary, so with no pin it is both selected and its payload
        // is loaded — it must survive even though its group's other sibling is below the boundary. What the turn SENDS
        // is decided in anchor space: the sibling's group anchors at sequence 2, which the synopsis covers, so the
        // synopsis stands in for it. The cap is deliberately the more conservative of the two (it keys on each row's own
        // sequence, and a group's anchor is never above a member's sequence), so it can only ever over-load, never
        // blank a payload the turn still needs.
        await using var provider = await BuildProviderAsync("turn-read-cap-newest.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var built = await BuildBranchedConversationAsync(service, pinOldSibling: false).ConfigureAwait(false);

        // Sequences are 0-based: 0 user-one, 1 assistant-one (sibling A), 2 user-two, 3 assistant-two, 4 assistant-one-variant
        // (sibling B). The boundary sits at 2, so the cap blanks sibling A's payload and keeps assistant-two's — the one
        // message the turn still sends verbatim.
        await service.SetCompactionSummaryAsync(new NodeChatSetCompactionSummaryRequest(built.ConversationId, "SYNOPSIS", CoversToSequence: 2, UpdatedAtUtc: 60))
                     .ConfigureAwait(false);

        var full = AssertEx.NotNull(await service.GetConversationAsync(built.ConversationId).ConfigureAwait(false));
        var turn = AssertEx.NotNull(await service.GetConversationForTurnAsync(built.ConversationId).ConfigureAwait(false));

        var turnPath = SelectedPathResolver.Resolve(turn.Messages, turn.SelectedPath).Select(message => message.MessageId).ToArray();
        AssertEx.Contains(turnPath, built.NewerSiblingId, "With no pin the newest sibling is the default selection.");

        var fullContext = ProjectSentHistory(full);
        var turnContext = ProjectSentHistory(turn);
        AssertEx.Equal(fullContext.Count, turnContext.Count);
        for (var index = 0; index < fullContext.Count; index++)
        {
            AssertEx.Equal(fullContext[index], turnContext[index]);
        }

        // The payload above the boundary is intact in the READ — a cap keyed on the group instead of the row would have
        // blanked it here, and a later boundary shift would then surface an empty assistant answer.
        var siblingFromTurn = turn.Messages.Single(message => message.MessageId == built.NewerSiblingId);
        AssertEx.Equal("assistant-one-variant", siblingFromTurn.Content);
        AssertEx.Equal(full.Messages.Single(message => message.MessageId == built.NewerSiblingId).Content, siblingFromTurn.Content);

        // And the turn still sends the one exchange the synopsis does not cover, verbatim, from a payload the cap kept.
        AssertEx.Contains(turnContext, entry => entry.Contains("assistant-two", StringComparison.Ordinal));
    }

    [Test]
    public async Task ReadMessageAsync_ReturnsTheSameMessageTheFullReadProjects()
    {
        // ReadMessageAsync used to materialize the WHOLE conversation and pick one entry out of it, on the ~10/s
        // partial-flush path. It now filters in SQL. This pins the equivalence: for every message, the single-message
        // read must project exactly what the full read projects for it — including the LEFT JOIN'd feedback row.
        await using var provider = await BuildProviderAsync("read-message-targeted.sqlite").ConfigureAwait(false);
        var service = CreateService(provider);
        var built = await BuildBranchedConversationAsync(service).ConfigureAwait(false);
        await service.SetMessageFeedbackAsync(new NodeChatSetMessageFeedbackRequest(built.ConversationId, built.NewerSiblingId, "up", "nice", UpdatedAtUtc: 40))
                     .ConfigureAwait(false);
        var other = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Other", "node", CreatedAtUtc: 10)).ConfigureAwait(false);

        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var all = await NodeChatPersistenceSql.ReadMessagesAsync(dbContext, built.ConversationId, CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 5, all.Count);

        foreach (var expected in all)
        {
            var actual = AssertEx.NotNull(await NodeChatPersistenceSql.ReadMessageAsync(dbContext, built.ConversationId, expected.MessageId, CancellationToken.None).ConfigureAwait(false));

            // Parts/Sources are collections, which record equality compares by reference; normalise them out and check
            // them separately so this stays meaningful if a fixture later persists parts.
            AssertEx.Equal(expected with
                {
                    Parts = null,
                    Sources = null
                },
                actual with
                {
                    Parts = null,
                    Sources = null
                });
            AssertEx.Equal(expected.Parts?.Count ?? 0, actual.Parts?.Count ?? 0);
            AssertEx.Equal(expected.Sources?.Count ?? 0, actual.Sources?.Count ?? 0);
        }

        // The feedback join must survive the narrowed WHERE.
        var rated = AssertEx.NotNull(await NodeChatPersistenceSql.ReadMessageAsync(dbContext, built.ConversationId, built.NewerSiblingId, CancellationToken.None).ConfigureAwait(false));
        AssertEx.Equal("up", rated.FeedbackRating);
        AssertEx.Equal("nice", rated.FeedbackComment);

        AssertEx.Null(await NodeChatPersistenceSql.ReadMessageAsync(dbContext, built.ConversationId, Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false));

        // The conversation guard still applies: a real message id under the WRONG conversation must not resolve.
        AssertEx.Null(await NodeChatPersistenceSql.ReadMessageAsync(dbContext, other.ConversationId, built.NewerSiblingId, CancellationToken.None).ConfigureAwait(false));
    }

    /// <summary>
    ///     Mirrors what <c>ConversationContextBuilder.Build</c> sends: resolve the selected path, drop
    ///     everything the synopsis covers, then keep completed non-empty messages in sequence order. Any divergence
    ///     between the full and turn reads shows up as a difference in this projection.
    /// </summary>
    private static IReadOnlyList<string> ProjectSentHistory(NodeChatConversationDto conversation)
    {
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(conversation.Messages);
        var selected = SelectedPathResolver.Resolve(conversation.Messages, conversation.SelectedPath);
        if (conversation.CompactionSummary is { Length: > 0 } && conversation.CompactionSummaryCoversToSequence is { } coveredSequence)
        {
            selected = [.. selected.Where(message => anchorSequence(message) > coveredSequence)];
        }

        return selected
               .Where(message => !string.IsNullOrWhiteSpace(message.Content)
                                 && string.Equals(message.Status, NodeChatMessageStatusValues.Completed, StringComparison.Ordinal))
               .OrderBy(anchorSequence)
               .Select(message => $"{message.MessageId}|{message.Role}|{message.Content}|{message.Reasoning}|{message.Model}")
               .ToArray();
    }

    /// <summary>
    ///     Builds: 1 user, 2 assistant (group sibling A), 3 user, 4 assistant, 5 assistant (group sibling B, minted from
    ///     message 2 — variants take the next free sequence, so the group straddles a boundary drawn at 3).
    /// </summary>
    private static async Task<BranchedConversation> BuildBranchedConversationAsync(NodeChatPersistenceService service, bool pinOldSibling = true)
    {
        var conversation = await service.CreateConversationAsync(new NodeChatCreateConversationRequest("Branched", "node", CreatedAtUtc: 10)).ConfigureAwait(false);
        var conversationId = conversation.ConversationId;

        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId, Guid.NewGuid(), "user-one", CreatedAtUtc: 11)).ConfigureAwait(false);
        var oldSiblingId = await CompleteAssistantAsync(service, conversationId, "assistant-one", "reasoning-one", createdAtUtc: 12).ConfigureAwait(false);
        await service.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId, Guid.NewGuid(), "user-two", CreatedAtUtc: 13)).ConfigureAwait(false);
        await CompleteAssistantAsync(service, conversationId, "assistant-two", "reasoning-two", createdAtUtc: 14).ConfigureAwait(false);

        var newerSiblingId = Guid.NewGuid();
        var variantRequestId = Guid.NewGuid();
        await service.CreateMessageVariantAsync(new NodeChatCreateMessageVariantRequest(conversationId, oldSiblingId, newerSiblingId, variantRequestId, CreatedAtUtc: 15))
                     .ConfigureAwait(false);
        var variantCorrelation = new NodeChatMessageCorrelation(conversationId, newerSiblingId, variantRequestId);
        await service.MarkAssistantStreamingAsync(variantCorrelation, updatedAtUtc: 16).ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(variantCorrelation,
                         NodeChatMessageStatusValues.Completed,
                         UpdatedAtUtc: 17,
                         "assistant-one-variant",
                         "reasoning-one-variant",
                         Model: "llama"))
                     .ConfigureAwait(false);

        var loaded = AssertEx.NotNull(await service.GetConversationAsync(conversationId).ConfigureAwait(false));
        var variantGroupId = loaded.Messages.Single(message => message.MessageId == oldSiblingId).VariantGroupId;
        AssertEx.True(variantGroupId.HasValue, "Minting a variant must back-stamp the original with a variant group.");

        if (pinOldSibling)
        {
            // Pin the OLDER sibling so the resolver's newest-wins default is overridden — the case where a cap that
            // pre-filtered by sequence would change which message is sent.
            await service.SetSelectedPathAsync(new NodeChatSetSelectedPathRequest(conversationId,
                             new Dictionary<Guid, Guid>
                             {
                                 [variantGroupId!.Value] = oldSiblingId
                             },
                             UpdatedAtUtc: 18))
                         .ConfigureAwait(false);
        }

        return new BranchedConversation(conversationId, oldSiblingId, newerSiblingId);
    }

    private static async Task<Guid> CompleteAssistantAsync(NodeChatPersistenceService service,
        Guid conversationId,
        string content,
        string reasoning,
        long createdAtUtc)
    {
        var messageId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(conversationId, messageId, requestId);
        await service.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, messageId, requestId, createdAtUtc, "llama")).ConfigureAwait(false);
        await service.MarkAssistantStreamingAsync(correlation, createdAtUtc).ConfigureAwait(false);
        await service.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                         NodeChatMessageStatusValues.Completed,
                         createdAtUtc,
                         content,
                         reasoning,
                         Model: "llama"))
                     .ConfigureAwait(false);
        return messageId;
    }

    private async Task<ServiceProvider> BuildProviderAsync(string fileName)
    {
        Directory.CreateDirectory(_rootPath);
        var services = new ServiceCollection();
        services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_rootPath, fileName)}"));
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

    private sealed record BranchedConversation(Guid ConversationId, Guid PinnedOldSiblingId, Guid NewerSiblingId);
}
