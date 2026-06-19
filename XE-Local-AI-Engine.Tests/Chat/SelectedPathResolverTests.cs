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

        var a1 = Message(1, groupA, 100);
        var a2 = Message(1, groupA, 200);
        var b1 = Message(2, groupB, 300);
        var b2 = Message(2, groupB, 400);

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

        AssertEx.Equal(2, path.Count);
        AssertEx.Equal(a1.MessageId, path[0].MessageId);
        AssertEx.Equal(b2.MessageId, path[1].MessageId);
    }

    [Test]
    public void Resolve_WithoutSelection_DefaultsToNewestSibling()
    {
        var group = Guid.NewGuid();
        var oldest = Message(1, group, 100);
        var middle = Message(1, group, 200);
        var newest = Message(1, group, 300);

        var path = SelectedPathResolver.Resolve(new[]
        {
            middle,
            newest,
            oldest
        }, null);

        AssertEx.Equal(1, path.Count);
        AssertEx.Equal(newest.MessageId, path[0].MessageId);
    }

    [Test]
    public void Resolve_WhenSelectionChangesMidThread_ProducesDifferentDownstreamPath()
    {
        var userGroup = Guid.NewGuid();
        var prompt = Message(1, null, 50);

        // Two assistant variants for the same turn, each with its own follow-up downstream.
        var assistantA = Message(2, userGroup, 100);
        var assistantB = Message(2, userGroup, 200);
        var followUp = Message(3, null, 300);

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
        var user = Message(1, null, 100);
        var variant1 = Message(2, group, 200);
        var variant2 = Message(2, group, 300);
        var trailing = Message(3, null, 400);

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

        AssertEx.Equal(3, path.Count);
        AssertEx.Contains(path, message => message.MessageId == user.MessageId);
        AssertEx.Contains(path, message => message.MessageId == trailing.MessageId);
        AssertEx.Contains(path, message => message.MessageId == variant1.MessageId);
    }

    [Test]
    public void Resolve_WithNoVariants_PassesThroughOrderedBySequence()
    {
        var first = Message(1, null, 100);
        var second = Message(2, null, 200);
        var third = Message(3, null, 300);

        var path = SelectedPathResolver.Resolve(new[]
        {
            third,
            first,
            second
        }, null);

        AssertEx.Equal(3, path.Count);
        AssertEx.Equal(first.MessageId, path[0].MessageId);
        AssertEx.Equal(second.MessageId, path[1].MessageId);
        AssertEx.Equal(third.MessageId, path[2].MessageId);
    }

    [Test]
    public void Resolve_WithEmptyConversation_ReturnsEmpty()
    {
        var path = SelectedPathResolver.Resolve(Array.Empty<TestMessage>(), null);

        AssertEx.Empty(path);
    }

    [Test]
    public void Resolve_WhenSelectedIdMissing_FallsBackToNewestSibling()
    {
        var group = Guid.NewGuid();
        var older = Message(1, group, 100);
        var newer = Message(1, group, 200);

        var path = SelectedPathResolver.Resolve(new[]
            {
                older,
                newer
            },
            new Dictionary<Guid, Guid>
            {
                [group] = Guid.NewGuid()
            });

        AssertEx.Equal(1, path.Count);
        AssertEx.Equal(newer.MessageId, path[0].MessageId);
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
