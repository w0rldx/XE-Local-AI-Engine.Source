namespace XE_Local_AI_Engine.Tests.Chat;

using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class SelectedPathResolverTests
{
    [Test]
    public void Resolve_WithMultipleGroups_HonoursExplicitSelectionPerGroup()
    {
        var groupA = Guid.NewGuid();
        var groupB = Guid.NewGuid();

        var a1 = Message(seq: 1, groupA, createdAt: 100);
        var a2 = Message(seq: 1, groupA, createdAt: 200);
        var b1 = Message(seq: 2, groupB, createdAt: 300);
        var b2 = Message(seq: 2, groupB, createdAt: 400);

        var selection = new Dictionary<Guid, Guid>
        {
            [groupA] = a1.MessageId,
            [groupB] = b2.MessageId
        };

        var path = SelectedPathResolver.Resolve(new[]
        {
            a2,
            b1,
            a1,
            b2
        }, selection);

        AssertEx.Equal(expected: 2, path.Count);
        AssertEx.Equal(a1.MessageId, path[0].MessageId);
        AssertEx.Equal(b2.MessageId, path[1].MessageId);
    }

    [Test]
    public void Resolve_WithoutSelection_DefaultsToNewestSibling()
    {
        var group = Guid.NewGuid();
        var oldest = Message(seq: 1, group, createdAt: 100);
        var middle = Message(seq: 1, group, createdAt: 200);
        var newest = Message(seq: 1, group, createdAt: 300);

        var path = SelectedPathResolver.Resolve(new[]
        {
            middle,
            newest,
            oldest
        }, selection: null);

        AssertEx.Equal(expected: 1, path.Count);
        AssertEx.Equal(newest.MessageId, path[0].MessageId);
    }

    [Test]
    public void Resolve_WhenSelectionChangesMidThread_ProducesDifferentDownstreamPath()
    {
        var userGroup = Guid.NewGuid();
        var prompt = Message(seq: 1, group: null, createdAt: 50);

        // Two assistant variants for the same turn, each with its own follow-up downstream.
        var assistantA = Message(seq: 2, userGroup, createdAt: 100);
        var assistantB = Message(seq: 2, userGroup, createdAt: 200);
        var followUp = Message(seq: 3, group: null, createdAt: 300);

        var messages = new[]
        {
            prompt,
            assistantA,
            assistantB,
            followUp
        };

        var selectA = SelectedPathResolver.Resolve(messages,
            new Dictionary<Guid, Guid>
            {
                [userGroup] = assistantA.MessageId
            });
        var selectB = SelectedPathResolver.Resolve(messages,
            new Dictionary<Guid, Guid>
            {
                [userGroup] = assistantB.MessageId
            });

        AssertEx.Equal(assistantA.MessageId, selectA[1].MessageId);
        AssertEx.Equal(assistantB.MessageId, selectB[1].MessageId);
        AssertEx.NotEqual(selectA[1].MessageId, selectB[1].MessageId);

        // Deselected sibling is excluded, never present in either path.
        AssertEx.False(selectA.Any(message => message.MessageId == assistantB.MessageId));
        AssertEx.False(selectB.Any(message => message.MessageId == assistantA.MessageId));
    }

    [Test]
    public void Resolve_AlwaysIncludesMessagesWithoutVariantGroup()
    {
        var group = Guid.NewGuid();
        var user = Message(seq: 1, group: null, createdAt: 100);
        var variant1 = Message(seq: 2, group, createdAt: 200);
        var variant2 = Message(seq: 2, group, createdAt: 300);
        var trailing = Message(seq: 3, group: null, createdAt: 400);

        var path = SelectedPathResolver.Resolve(new[]
            {
                user,
                variant1,
                variant2,
                trailing
            },
            new Dictionary<Guid, Guid>
            {
                [group] = variant1.MessageId
            });

        AssertEx.Equal(expected: 3, path.Count);
        AssertEx.Contains(path, message => message.MessageId == user.MessageId);
        AssertEx.Contains(path, message => message.MessageId == trailing.MessageId);
        AssertEx.Contains(path, message => message.MessageId == variant1.MessageId);
    }

    [Test]
    public void Resolve_WithNoVariants_PassesThroughOrderedBySequence()
    {
        var first = Message(seq: 1, group: null, createdAt: 100);
        var second = Message(seq: 2, group: null, createdAt: 200);
        var third = Message(seq: 3, group: null, createdAt: 300);

        var path = SelectedPathResolver.Resolve(new[]
        {
            third,
            first,
            second
        }, selection: null);

        AssertEx.Equal(expected: 3, path.Count);
        AssertEx.Equal(first.MessageId, path[0].MessageId);
        AssertEx.Equal(second.MessageId, path[1].MessageId);
        AssertEx.Equal(third.MessageId, path[2].MessageId);
    }

    [Test]
    public void Resolve_WithEmptyConversation_ReturnsEmpty()
    {
        var path = SelectedPathResolver.Resolve(Array.Empty<TestMessage>(), selection: null);

        AssertEx.Empty(path);
    }

    [Test]
    public void Resolve_WhenSelectedIdMissing_FallsBackToNewestSibling()
    {
        var group = Guid.NewGuid();
        var older = Message(seq: 1, group, createdAt: 100);
        var newer = Message(seq: 1, group, createdAt: 200);

        var path = SelectedPathResolver.Resolve(new[]
            {
                older,
                newer
            },
            new Dictionary<Guid, Guid>
            {
                [group] = Guid.NewGuid()
            });

        AssertEx.Equal(expected: 1, path.Count);
        AssertEx.Equal(newer.MessageId, path[0].MessageId);
    }

    /// <summary>
    ///     A sibling minted by regenerating an EARLY turn AFTER later turns exist takes the next free sequence, so its
    ///     raw sequence lands past those later turns. It still belongs to the early turn: the anchor resolver must place
    ///     it at its group's earliest member sequence so callers order/filter it into the early position.
    /// </summary>
    [Test]
    public void CreateAnchorResolver_ForLateMintedSiblingOfEarlyTurn_AnchorsAtTheGroupsEarliestMember()
    {
        // U1(1) A1(2,G) U2(3) A2(4) U3(5) A3(6), then regenerate A1 -> A1'(7,G).
        var group = Guid.NewGuid();
        var u1 = Message(seq: 1, group: null, createdAt: 10);
        var a1 = Message(seq: 2, group, createdAt: 20);
        var u2 = Message(seq: 3, group: null, createdAt: 30);
        var a2 = Message(seq: 4, group: null, createdAt: 40);
        var u3 = Message(seq: 5, group: null, createdAt: 50);
        var a3 = Message(seq: 6, group: null, createdAt: 60);
        var a1Prime = Message(seq: 7, group, createdAt: 70);

        TestMessage[] all = [u1, a1, u2, a2, u3, a3, a1Prime];
        var anchorSequence = SelectedPathResolver.CreateAnchorResolver(all);

        AssertEx.Equal(expected: 2, anchorSequence(a1Prime));
        AssertEx.Equal(expected: 2, anchorSequence(a1));
        AssertEx.Equal(a3.Sequence, anchorSequence(a3));

        // The whole point: ordering the RESOLVED path by anchor puts the selected late sibling back at the early turn.
        var path = SelectedPathResolver.Resolve(all,
            new Dictionary<Guid, Guid>
            {
                [group] = a1Prime.MessageId
            });
        var anchored = path.OrderBy(anchorSequence).Select(message => message.MessageId).ToArray();

        AssertEx.Equal(a1Prime.MessageId, path[^1].MessageId, "Raw-sequence resolver output puts the late sibling last — that is what the anchor must correct.");
        AssertEx.Equal(expected: 6, anchored.Length);
        AssertEx.Equal(u1.MessageId, anchored[0]);
        AssertEx.Equal(a1Prime.MessageId, anchored[1]);
        AssertEx.Equal(u2.MessageId, anchored[2]);
        AssertEx.Equal(a2.MessageId, anchored[3]);
        AssertEx.Equal(u3.MessageId, anchored[4]);
        AssertEx.Equal(a3.MessageId, anchored[5]);
    }

    private static TestMessage Message(int seq, Guid? group, long createdAt)
    {
        return new TestMessage
        {
            MessageId = Guid.NewGuid(),
            Sequence = seq,
            VariantGroupId = group,
            CreatedAtUtc = createdAt
        };
    }

    private sealed class TestMessage : ISelectedPathMessage
    {
        public required Guid MessageId { get; init; }

        public required int Sequence { get; init; }

        public required Guid? VariantGroupId { get; init; }

        public required long CreatedAtUtc { get; init; }
    }
}
