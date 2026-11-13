namespace XE_Local_AI_Engine.Tests.Chat;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The in-memory index on its own: matching, ordering, the archived filter, stale stamps and the per-conversation text ceiling.</summary>
[Category(TestCategories.Unit)]
public sealed class ConversationSearchIndexTests
{
    [Test]
    public void Search_RequiresEveryTerm_OrdersNewestFirst_AndStopsAtTheLimit()
    {
        var index = new ConversationSearchIndex();
        var oldest = Guid.NewGuid();
        var middle = Guid.NewGuid();
        var newest = Guid.NewGuid();
        index.Store(oldest, Document("Trip", "paris and rome", lastSeenUtc: 1));
        index.Store(middle, Document("Paris notes", "only one city", lastSeenUtc: 2));
        index.Store(newest, Document(title: null, "ROME, then Paris", lastSeenUtc: 3));

        var both = index.Search("paris rome", includeArchived: false, limit: 10);
        var capped = index.Search("paris", includeArchived: false, limit: 2);

        AssertEx.Equal($"{newest},{oldest}", string.Join(',', both.Select(static hit => hit.ConversationId)), "A title term and a content term may combine; every term must match.");
        AssertEx.Equal($"{newest},{middle}", string.Join(',', capped.Select(static hit => hit.ConversationId)));
        AssertEx.Empty(index.Search("   ", includeArchived: true, limit: 10), "A blank query matches nothing rather than everything.");
    }

    [Test]
    public void Search_HidesArchivedConversationsUnlessTheyAreIncluded()
    {
        var index = new ConversationSearchIndex();
        var archived = Guid.NewGuid();
        index.Store(archived, Document("Archived", "needle", lastSeenUtc: 1, archived: true));

        AssertEx.Empty(index.Search("needle", includeArchived: false, limit: 10));
        AssertEx.Equal(archived, index.Search("needle", includeArchived: true, limit: 10)[0].ConversationId);
    }

    [Test]
    public void Search_ReportsWhereTheHitWas_AndCutsASnippetAroundTheFirstContentHit()
    {
        var index = new ConversationSearchIndex();
        var conversation = Guid.NewGuid();
        var content = new string('a', 100) + " the\n\n  needle   sits here " + new string('b', 100);
        index.Store(conversation, Document("Title with needle", content, lastSeenUtc: 1));
        var contentOnly = Guid.NewGuid();
        index.Store(contentOnly, Document("Other", "a needle", lastSeenUtc: 0));

        var hits = index.Search("needle", includeArchived: false, limit: 10);

        AssertEx.Equal(NodeChatSearchMatchValues.Title, hits[0].MatchedIn);
        var snippet = AssertEx.NotNull(hits[0].Snippet);
        AssertEx.Contains(snippet, "the needle sits here", StringComparison.Ordinal, "Whitespace in the excerpt collapses to single spaces.");
        AssertEx.True(snippet.StartsWith('…') && snippet.EndsWith('…'), "An excerpt cut on both sides says so.");
        AssertEx.Equal(NodeChatSearchMatchValues.Content, hits[1].MatchedIn);
        AssertEx.Equal("a needle", hits[1].Snippet);
    }

    [Test]
    public void ClearStale_KeepsTheMark_WhenALaterWriteReMarkedTheConversation()
    {
        var index = new ConversationSearchIndex();
        var conversation = Guid.NewGuid();
        index.MarkStale(conversation);
        var firstStamp = index.SnapshotStale().Single().Value;

        index.MarkStale(conversation);
        index.ClearStale(conversation, firstStamp);

        AssertEx.Equal(1, index.SnapshotStale().Length,
            "A refresh that read the conversation before the second write must not clear the mark that write left, or the search would miss it.");
        index.ClearStale(conversation, index.SnapshotStale()[0].Value);
        AssertEx.Empty(index.SnapshotStale());
    }

    [Test]
    public void MarkStale_IgnoresTheWritersCrossConversationKey()
    {
        var index = new ConversationSearchIndex();

        index.MarkStale(Guid.Empty);

        AssertEx.Empty(index.SnapshotStale());
    }

    [Test]
    public void Store_WithNoDocument_RemovesTheConversation()
    {
        var index = new ConversationSearchIndex();
        var conversation = Guid.NewGuid();
        index.Store(conversation, Document("Gone soon", "text", lastSeenUtc: 1));

        index.Store(conversation, document: null);

        AssertEx.False(index.Contains(conversation));
        AssertEx.Empty(index.Search("gone", includeArchived: true, limit: 10));
    }

    [Test]
    public void JoinContent_SkipsBlankMessages_RestoresChronologicalOrder_AndKeepsTheNewestTextAtTheCeiling()
    {
        var oversized = new string('x', ConversationSearchIndex.MaxIndexedCharsPerConversation);

        AssertEx.Equal("first\nsecond", ConversationSearchIndex.JoinContent(["second", "  ", "first"]), "The read hands texts newest first; the content reads oldest first.");
        var capped = ConversationSearchIndex.JoinContent(["newest", oversized, "oldest"]);
        AssertEx.Equal(ConversationSearchIndex.MaxIndexedCharsPerConversation, capped.Length);
        AssertEx.True(capped.EndsWith("\nnewest", StringComparison.Ordinal), "The cap must drop the OLDEST text, so a long chat's latest messages stay searchable.");
        AssertEx.False(capped.Contains("oldest", StringComparison.Ordinal));
    }

    [Test]
    public void Search_NeverCutsTheSnippetInsideASurrogatePair()
    {
        var index = new ConversationSearchIndex();
        // The 40-char lead before the hit starts on the emoji's low half; the 80-char tail ends on the next emoji's high half.
        var content = "\U0001F600" + new string('a', 39) + "needle" + new string('b', 73) + "\U0001F680" + "tail";
        index.Store(Guid.NewGuid(), Document("Emoji", content, lastSeenUtc: 1));

        var snippet = AssertEx.NotNull(index.Search("needle", includeArchived: false, limit: 1)[0].Snippet);

        AssertEx.True(snippet.Contains("\U0001F600", StringComparison.Ordinal) && snippet.Contains("\U0001F680", StringComparison.Ordinal),
            "A cut that lands inside an emoji widens to keep both halves.");
        _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetByteCount(snippet);
    }

    private static ConversationSearchDocument Document(string? title, string content, long lastSeenUtc, bool archived = false)
    {
        return new ConversationSearchDocument
        {
            Title = title,
            Content = content,
            CreatedAtUtc = 0,
            LastSeenUtc = lastSeenUtc,
            Origin = NodeChatOriginValues.Local,
            IsPinned = false,
            Archived = archived
        };
    }
}
