namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = XE_Local_AI_Engine.Tests.Integrations.IntegrationCoordinatorHarness;

/// <summary>
///     The per-turn context an integration execution sends, assembled by the SAME builder the chat send path uses.
///     <para>
///         The integration path is the REVERSE of the chat path in one load-bearing way: the accept path persists the
///         seed before this coordinator runs, so the turn read already contains it and it has to be lifted back out by
///         id. Chat reads the conversation first and persists second, so its read never contains the current turn.
///     </para>
/// </summary>
public sealed class IntegrationContinuationTests
{
    private const string SeedText = Harness.SeedText;

    [Test]
    public async Task TheSeedIsSentOnceAndIsNotAlsoHistory()
    {
        using var harness = new Harness();
        harness.AddHistory("user", "the first question");
        harness.AddHistory("assistant", "the first answer");
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var context = Context(harness);
        AssertEx.Equal(expected: 1,
            context.Count(message => string.Equals(message.Content, SeedText, StringComparison.Ordinal)),
            "The seed must appear exactly once: concatenating it again would send the caller's input twice.");
        AssertEx.Equal(SeedText, context[^1].Content, "The seed is the CURRENT turn, so it is last.");
    }

    [Test]
    public async Task SecondTurn_CarriesTheFirstTurnsAnswer()
    {
        using var harness = new Harness();
        harness.AddHistory("user", "what temperature did I send?");
        harness.AddHistory("assistant", "twenty-one degrees");
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var context = Context(harness);
        AssertEx.Equal(expected: 3, context.Count);
        AssertEx.Equal("what temperature did I send?", context[0].Content);
        AssertEx.Equal("twenty-one degrees", context[1].Content);
        AssertEx.Equal(MessageRole.Assistant, context[1].Role, "An assistant turn must be replayed as one, or the model reads its own answer as the user's.");
        AssertEx.Equal(SeedText, context[2].Content);
    }

    [Test]
    public async Task TurnOne_SendsOnlyTheSeed()
    {
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var context = Context(harness);
        AssertEx.Equal(expected: 1, context.Count, "Turn one and turn N take the identical path; on turn one the history is empty.");
        AssertEx.Equal(SeedText, context[0].Content);
    }

    [Test]
    public async Task WhenCompacted_TheSynopsisReplacesTheSpanItCoversAndTheSeedStillLeadsTheTurn()
    {
        using var harness = new Harness();
        harness.AddHistory("user", "an old question");
        harness.AddHistory("assistant", "an old answer");
        harness.AddHistory("user", "a recent question");
        harness.AddHistory("assistant", "a recent answer");
        harness.CompactionSummary = "SYNOPSIS";
        harness.CompactionCoversToSequence = 1;
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var context = Context(harness);
        AssertEx.True(context[0].Content.Contains("SYNOPSIS", StringComparison.Ordinal), "The synopsis leads the context, in the covered span's place.");
        AssertEx.False(context.Any(message => message.Content.Contains("an old answer", StringComparison.Ordinal)),
            "Messages the synopsis covers must not be re-sent verbatim.");
        AssertEx.Contains(context.Select(message => message.Content), "a recent answer");
        AssertEx.Equal(SeedText, context[^1].Content);
    }

    [Test]
    public async Task ContextBound_FoldsOnlyWhenTheProjectionExceedsTheBudget()
    {
        using var underBudget = new Harness();
        underBudget.AddHistory("assistant", new string('a', count: 4_000));
        await underBudget.Coordinator.ProcessOneAsync(underBudget.SeedAccepted(), CancellationToken.None);
        AssertEx.Empty(underBudget.Compaction.Calls, "The default budget leaves a short session unfolded.");

        using var overBudget = new Harness(contextBudgetTokens: 1);
        overBudget.AddHistory("assistant", new string('a', count: 4_000));
        await overBudget.Coordinator.ProcessOneAsync(overBudget.SeedAccepted(), CancellationToken.None);

        AssertEx.Equal(expected: 1, overBudget.Compaction.Calls.Count, "Over budget, the per-turn bound folds before the turn is read.");
        AssertEx.Equal(overBudget.ConversationId, overBudget.Compaction.Calls[0].ConversationId);
    }

    [Test]
    public async Task ContextBound_KeepsTheChatVerbatimWindow_NotTheWorkSessionFloor()
    {
        // The whole reason the keep window became a parameter. A work-session step rebuilds its state block from the
        // database every step, so folding to two loses nothing; an integration session's transcript IS its state, so
        // the same floor would delete the continuation the feature exists to deliver.
        using var harness = new Harness(contextBudgetTokens: 1);
        harness.AddHistory("assistant", new string('a', count: 4_000));

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Compaction.Calls.Count);
        AssertEx.Equal(Harness.ChatKeepVerbatim, harness.Compaction.Calls[0].KeepVerbatim, "An integration turn passes the CHAT window, never the work-session floor of two.");
    }

    [Test]
    public async Task WhenTheAgentGainsANonReadLocalToolAfterTheTriggerWasSaved_TheRunFailsSessionPolicy()
    {
        // Ruling R4-9(a) at RUN time, against the offer the package would actually carry. The trigger passed its
        // save-time check; the agent's tools changed afterwards, and a caller-managed session persists no tool history.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.OfferedTools =
        [
            Harness.Tool("write_file", XE_Local_AI_Engine.AI.Agent.Tools.ToolCategory.WriteExecute)
        ];
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Failed, row.Status);
        AssertEx.Equal(IntegrationFailureCategories.SessionPolicy, row.FailureCategory);
        AssertEx.Equal(expected: 0, harness.RunCount, "Nothing may run: the point is that the side-effecting agent never starts.");
    }

    [Test]
    public async Task APerInvocationTriggerIsNeverJudgedByTheCallerManagedRule()
    {
        // A per-invocation run starts fresh every time, so it carries no history a missing tool call could make wrong.
        using var harness = new Harness();
        harness.OfferedTools =
        [
            Harness.Tool("write_file", XE_Local_AI_Engine.AI.Agent.Tools.ToolCategory.WriteExecute)
        ];
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Completed, harness.Row(executionId).Status);
        AssertEx.Equal(expected: 1, harness.RunCount);
    }

    [Test]
    public async Task APerInvocationSessionIsClosedWhenItsExecutionTerminalizes()
    {
        // A per-invocation session exists for ONE run, so leaving it Active would show an operator a session nothing
        // will ever join.
        using var harness = new Harness();
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationSessionStatus.Closed, harness.Session().Status);
    }

    [Test]
    public async Task ACallerManagedSessionStaysActiveAfterItsExecutionTerminalizes()
    {
        // The whole point of the policy: the caller sends the same session id back on its next invoke.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Completed, harness.Row(executionId).Status);
        AssertEx.Equal(IntegrationSessionStatus.Active, harness.Session().Status);
    }

    [Test]
    public async Task StartupReconciliation_ClosesPerInvocationSessionsForTheRowsItFails()
    {
        // A DIFFERENT terminal path from the run's own, and it needs the same close: a session interrupted by a restart
        // would otherwise stay Active with no execution left that could close it.
        using var harness = new Harness();
        _ = harness.SeedAccepted(IntegrationExecutionStatus.Running);

        await harness.Coordinator.StartAsync(CancellationToken.None);
        await harness.Coordinator.StopAsync(CancellationToken.None);

        AssertEx.Equal(IntegrationSessionStatus.Closed, harness.Session().Status);
    }

    private static IReadOnlyList<ConversationMessageDto> Context(Harness harness) =>
        (harness.CapturedPackage ?? throw new AssertionException("The runner was never called.")).ConversationContext;
}
