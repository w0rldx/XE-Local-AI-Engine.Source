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
            Action("Run the tests first.", priority: 1),
            Action("Prefer small commits.", priority: 5)
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
            Action("First emitted.", priority: 9),
            Action("Second emitted.", priority: 2)
        ];

        var composed = PlaybookPromptComposer.Compose(BaseInstructions, actions);

        var expected = BaseInstructions + "\n\n## Operating Playbook\n- First emitted.\n- Second emitted.";
        AssertEx.Equal(expected, composed);
    }

    [Test]
    public void Compose_WithFailureScope_RendersFailuresInSeparateNegativeSection()
    {
        // A mix of positive and Failure-scope items: positive items go to the Operating Playbook section in supplied
        // order; Failure items go to a distinct negative-guidance section emitted AFTER it.
        IReadOnlyList<PlaybookActionRecord> actions =
        [
            Scoped("Run the tests first.", priority: 1, createdAtUtc: 10, scope: null),
            Scoped("Never force-push to main.", priority: 2, createdAtUtc: 20, MemoryScope.Failure)
        ];

        var composed = PlaybookPromptComposer.Compose(BaseInstructions, actions);

        var expected = BaseInstructions
                       + "\n\n## Operating Playbook\n- Run the tests first."
                       + "\n\n## Avoid (lessons from past failures)\nDo NOT repeat these mistakes:\n- Never force-push to main.";
        AssertEx.Equal(expected, composed);
    }

    [Test]
    public void Compose_FailureSection_IsDeterministicallyOrdered_ByPriorityThenCreatedAt()
    {
        // Failure items are re-ordered (Priority asc, then CreatedAtUtc asc) independent of the supplied/relevance order,
        // so the composed text — and the config hash — is stable per send for a fixed memory set (resume-safety).
        IReadOnlyList<PlaybookActionRecord> actions =
        [
            Scoped("Higher priority value, emitted later.", priority: 9, createdAtUtc: 5, MemoryScope.Failure),
            Scoped("Lower priority value, emitted first.", priority: 2, createdAtUtc: 50, MemoryScope.Failure),
            Scoped("Same priority as previous, older timestamp.", priority: 2, createdAtUtc: 10, MemoryScope.Failure)
        ];

        var composed = PlaybookPromptComposer.Compose(BaseInstructions, actions);

        var expected = BaseInstructions
                       + "\n\n## Avoid (lessons from past failures)\nDo NOT repeat these mistakes:\n"
                       + "- Same priority as previous, older timestamp.\n"
                       + "- Lower priority value, emitted first.\n"
                       + "- Higher priority value, emitted later.";
        AssertEx.Equal(expected, composed);
    }

    [Test]
    public void Compose_WithOnlyFailureScope_OmitsTheOperatingPlaybookSection()
    {
        IReadOnlyList<PlaybookActionRecord> actions =
        [
            Scoped("Never delete the prod database.", priority: 1, createdAtUtc: 10, MemoryScope.Failure)
        ];

        var composed = PlaybookPromptComposer.Compose(BaseInstructions, actions);

        var expected = BaseInstructions
                       + "\n\n## Avoid (lessons from past failures)\nDo NOT repeat these mistakes:\n- Never delete the prod database.";
        AssertEx.Equal(expected, composed);
    }

    private static PlaybookActionRecord Action(string behavior, int priority)
    {
        return Scoped(behavior, priority, createdAtUtc: 10, scope: null);
    }

    private static PlaybookActionRecord Scoped(string behavior, int priority, long createdAtUtc, MemoryScope? scope)
    {
        return new PlaybookActionRecord(Guid.NewGuid(),
            Guid.NewGuid(),
            PlaybookActionState.Enabled,
            PlaybookActionSource.Manual,
            TriggerCondition: null,
            behavior,
            Scope: null,
            priority,
            Version: 1,
            createdAtUtc,
            createdAtUtc,
            MemoryScope: scope);
    }
}
