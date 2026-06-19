namespace XE_Local_AI_Engine.Tests.Agents;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Agents.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class PlaybookPromptComposerTests
{
    private const string BaseInstructions = "You are the bound persona.";

    [Test]
    public void Compose_WithEmptyList_ReturnsBaseInstructionsVerbatim()
    {
        var composed = PlaybookPromptComposer.Compose(BaseInstructions, []);

        // The byte-identical guard: no header, no trailing delimiter, reference-equal to the input.
        AssertEx.Equal(BaseInstructions, composed);
        AssertEx.True(ReferenceEquals(BaseInstructions, composed), "An empty playbook must return the base instructions verbatim.");
    }

    [Test]
    public void Compose_WithActions_AppendsHeaderAndBulletsInGivenOrder()
    {
        IReadOnlyList<PlaybookActionRecord> actions =
        [
            Action("Run the tests first.", 1),
            Action("Prefer small commits.", 5)
        ];

        var composed = PlaybookPromptComposer.Compose(BaseInstructions, actions);

        var expected = BaseInstructions + "\n\n## Operating Playbook\n- Run the tests first.\n- Prefer small commits.";
        AssertEx.Equal(expected, composed);
    }

    [Test]
    public void Compose_PreservesSuppliedOrder_WithoutReSorting()
    {
        // The store already ordered by Priority; the composer must emit them in exactly the order it receives, even if
        // the Priority values look out of order, so it never second-guesses the store's ordering.
        IReadOnlyList<PlaybookActionRecord> actions =
        [
            Action("First emitted.", 9),
            Action("Second emitted.", 2)
        ];

        var composed = PlaybookPromptComposer.Compose(BaseInstructions, actions);

        var expected = BaseInstructions + "\n\n## Operating Playbook\n- First emitted.\n- Second emitted.";
        AssertEx.Equal(expected, composed);
    }

    private static PlaybookActionRecord Action(string behavior, int priority)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            null,
            behavior,
            null,
            priority,
            1,
            10,
            10);
    }
}
