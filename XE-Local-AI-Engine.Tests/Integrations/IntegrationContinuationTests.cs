namespace XE_Local_AI_Engine.Tests.Integrations;

using Microsoft.Extensions.AI;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using Harness = IntegrationCoordinatorHarness;

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
    public async Task WhenTheAgentGainsANonReadLocalToolAfterTheTriggerWasSaved_TheRunProceeds()
    {
        // ADR 0008 R6-1, at RUN time. Ruling R4-9(a) used to terminalize this row with `session-policy` before the
        // runner was ever reached, because a caller-managed session persisted no tool history. It persists and replays
        // it now, so a write-capable agent is an ordinary caller-managed target and the run happens.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.OfferedTools =
        [
            Harness.Tool("write_file", ToolCategory.WriteExecute)
        ];
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var row = harness.Row(executionId);
        AssertEx.Equal(IntegrationExecutionStatus.Completed, row.Status);
        AssertEx.Null(row.FailureCategory);
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

    [Test]
    public async Task ATurnsCompletedToolCallIsPersistedOnItsAssistantRow()
    {
        // Half of the continuation contract: nothing can be replayed that was never written. The accumulator rides the
        // SAME lifecycle event the stream mapper does, so a run that calls a tool leaves a part behind.
        using var harness = new Harness();
        harness.DuringRun = (running, package) => RaiseToolCall(running, package.InvocationId, "call-1", "list_files", "{\"path\":\".\"}", "a.txt\nb.txt");
        var executionId = harness.SeedAccepted();

        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        var parts = AssertEx.NotNull(AssertEx.NotNull(harness.TerminalizeRequest).Parts, "The terminal persist must carry the run's tool parts.");
        var part = parts.Single();
        AssertEx.Equal(NodeChatMessagePartKinds.Tool, part.Kind);
        AssertEx.Equal("call-1", part.ToolCallId);
        AssertEx.Equal("list_files", part.Name);
        AssertEx.Equal(NodeChatToolPartStates.Received, part.State, "The requested phase must collapse into the completed one, not stay open.");
        AssertEx.Equal("a.txt\nb.txt", part.Result);
    }

    [Test]
    public async Task ATurnThatCallsNoToolPersistsNoParts()
    {
        // Null, not an empty list: the persistence contract reads null as "leave the existing parts untouched" and an
        // empty list as a positive claim that the turn had none.
        using var harness = new Harness();

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        AssertEx.Null(AssertEx.NotNull(harness.TerminalizeRequest).Parts);
    }

    [Test]
    public async Task ALifecycleEventWithNoToolCallIdIsDroppedRatherThanFaultingTheRun()
    {
        // The accumulator throws on an empty call id and InvocationRunner.ResolveToolCallCardId can yield one. A payload
        // that cannot be correlated into a call/result pair is dropped; the run still completes.
        using var harness = new Harness();
        harness.DuringRun = (running, package) => RaiseToolCall(running, package.InvocationId, callId: string.Empty, "list_files", args: null, "a.txt");

        var executionId = harness.SeedAccepted();
        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Completed, harness.Row(executionId).Status);
        AssertEx.Null(AssertEx.NotNull(harness.TerminalizeRequest).Parts);
    }

    [Test]
    public async Task ACallerManagedContinuationReplaysTheCallItsResultAndThenTheTurnsText()
    {
        // The other half: the persisted part becomes a real FunctionCallContent / FunctionResultContent pair, in the
        // order the model performed them, ahead of the answer it wrote afterwards.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.OfferedTools = [Harness.Tool("list_files", ToolCategory.ReadLocal)];
        harness.AddHistory("user", "list the files");
        harness.AddHistory("assistant", "there are two files", [Harness.CompletedToolPart("call-1", "list_files", "{\"path\":\".\"}", "a.txt\nb.txt")]);

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var assistant = Context(harness).Single(message => message.Role == MessageRole.Assistant);
        var exchange = AssertEx.NotNull(assistant.ToolExchanges, "A caller-managed continuation must carry the session's completed tool exchanges.").Single();
        AssertEx.Equal("call-1", exchange.CallId);
        AssertEx.Equal("list_files", exchange.Name);
        AssertEx.Equal("{\"path\":\".\"}", exchange.ArgumentsJson);
        AssertEx.Equal("a.txt\nb.txt", exchange.Result);
        AssertEx.False(exchange.IsError);

        var messages = InvocationRunner.BuildChatMessages(AssertEx.NotNull(harness.CapturedPackage)).ToList();
        var callIndex = messages.FindIndex(static message => message.Contents.OfType<FunctionCallContent>().Any());
        AssertEx.True(callIndex >= 0, "The replayed call must reach the model as a FunctionCallContent.");

        var call = messages[callIndex].Contents.OfType<FunctionCallContent>().Single();
        AssertEx.Equal(ChatRole.Assistant, messages[callIndex].Role);
        AssertEx.Equal("list_files", call.Name);
        AssertEx.Equal(expected: 1, messages[callIndex].Contents.Count, "A call-only assistant message must carry no text part.");

        AssertEx.Equal(ChatRole.Tool, messages[callIndex + 1].Role);
        var result = messages[callIndex + 1].Contents.OfType<FunctionResultContent>().Single();
        AssertEx.Equal("call-1", result.CallId);
        AssertEx.Equal("a.txt\nb.txt", result.Result?.ToString());

        AssertEx.Equal(ChatRole.Assistant, messages[callIndex + 2].Role);
        AssertEx.Equal("there are two files", messages[callIndex + 2].Contents.OfType<TextContent>().Single().Text);
    }

    [Test]
    public async Task AWriteExecuteToolsCallAndResultAreReplayedLikeAnyOther()
    {
        // The category-blind half of ADR 0008 R6-1. The replayed exchange is what lets turn 2 know the artifact was
        // ALREADY saved; without it the model reads only its own prose about having saved one and can save it twice.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.OfferedTools = [Harness.Tool("save_artifact", ToolCategory.WriteExecute)];
        harness.AddHistory("user", "save the count");
        harness.AddHistory("assistant", "saved it", [Harness.CompletedToolPart("call-1", "save_artifact", "{\"name\":\"s6.txt\"}", "saved s6.txt")]);

        var executionId = harness.SeedAccepted();
        await harness.Coordinator.ProcessOneAsync(executionId, CancellationToken.None);

        AssertEx.Equal(IntegrationExecutionStatus.Completed, harness.Row(executionId).Status);
        var assistant = Context(harness).Single(message => message.Role == MessageRole.Assistant);
        var exchange = AssertEx.NotNull(assistant.ToolExchanges, "A WriteExecute call is replayed on the same path a ReadLocal one is.").Single();
        AssertEx.Equal("save_artifact", exchange.Name);
        AssertEx.Equal("saved s6.txt", exchange.Result);

        var messages = InvocationRunner.BuildChatMessages(AssertEx.NotNull(harness.CapturedPackage)).ToList();
        var call = messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>().Single();
        AssertEx.Equal("save_artifact", call.Name);
        AssertEx.Equal("saved s6.txt", messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().Single().Result?.ToString());
    }

    [Test]
    public async Task APerInvocationContinuationReplaysNoToolHistory()
    {
        // The gate: only a caller-managed session asks for tool history. A per-invocation run starts fresh, and chat's
        // behaviour is unchanged for the same reason.
        using var harness = new Harness();
        harness.AddHistory("assistant", "there are two files", [Harness.CompletedToolPart("call-1", "list_files", args: null, "a.txt")]);

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        AssertEx.True(Context(harness).All(message => message.ToolExchanges is null));
    }

    [Test]
    public async Task ARequestedButNeverCompletedCallIsNotReplayed()
    {
        // An orphan FunctionCallContent with no result is worse than none: it invites the model to wait for an answer
        // that never comes.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.AddHistory("assistant",
            "I started reading",
            [
                Harness.CompletedToolPart("call-1", "list_files", args: null, "a.txt", sequence: 0),
                Harness.RequestedToolPart("call-2", "read_file", "{\"path\":\"a.txt\"}", sequence: 1)
            ]);

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var assistant = Context(harness).Single(message => message.Role == MessageRole.Assistant);
        var exchange = AssertEx.NotNull(assistant.ToolExchanges).Single();
        AssertEx.Equal("call-1", exchange.CallId, "Only the completed call is replayable; the requested-only one has no result to pair with.");
    }

    [Test]
    public async Task AFailedTurnsCompletedToolCallIsStillReplayed()
    {
        // The ADR amendment, as a test. A run that called a tool and then died left a REAL side effect; dropping the
        // turn because its status is not Completed is exactly the hole tool-history replay exists to close — and it has
        // no text at all, so the ordinary content filter would drop it twice over.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.AddHistory("assistant",
            string.Empty,
            [Harness.CompletedToolPart("call-1", "save_artifact", "{\"name\":\"s6.txt\"}", "saved")],
            NodeChatMessageStatusValues.Failed);

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var assistant = Context(harness).Single(message => message.Role == MessageRole.Assistant);
        AssertEx.Equal("call-1", AssertEx.NotNull(assistant.ToolExchanges).Single().CallId);

        var messages = InvocationRunner.BuildChatMessages(AssertEx.NotNull(harness.CapturedPackage));
        AssertEx.Equal(expected: 1, messages.Count(message => message.Contents.OfType<FunctionCallContent>().Any()));
        AssertEx.False(messages.Any(message => message.Contents.OfType<TextContent>().Any(text => string.IsNullOrEmpty(text.Text))),
            "A turn with no text of its own must emit no trailing message, not an empty text part.");
    }

    [Test]
    public async Task ACompactionSummaryCoveringAFailedToolTurnStillCarriesItsExchange()
    {
        // Compaction summarizes COMPLETED, non-blank text and then persists a cutoff past everything older — including
        // the rows it skipped. A run that called save_artifact and then died sits below that cutoff with its side
        // effect recorded nowhere else, so the fold would erase the model's only record of an action it took.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.AddHistory("assistant",
            string.Empty,
            [Harness.CompletedToolPart("call-1", "save_artifact", "{\"name\":\"s6.txt\"}", "saved")],
            NodeChatMessageStatusValues.Failed);
        harness.CompactionSummary = "SYNOPSIS";
        harness.CompactionCoversToSequence = 0;

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var context = Context(harness);
        AssertEx.Contains(context, static message => message.Content.Contains("SYNOPSIS", StringComparison.Ordinal));
        var assistant = context.Single(static message => message.Role == MessageRole.Assistant);
        AssertEx.Equal("call-1", AssertEx.NotNull(assistant.ToolExchanges, "The fold must not erase a turn the synopsis never saw.").Single().CallId);
    }

    [Test]
    public async Task ACompactionSummaryCoveringACompletedToolTurnReplaysItsExchangeWithoutItsProse()
    {
        // The half a status filter hides, end to end. A COMPLETED turn that wrote prose AND saved an artifact sits below
        // the cutoff: the synopsis carries the prose, and nothing carries the call — so folding it whole is how turn 2
        // reads only "I saved it" and saves it a second time. It survives for its exchange, with its text folded away.
        // It also proves the read: the parts live in the metadata blob the CAPPED turn read blanks for exactly this row.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        harness.OfferedTools = [Harness.Tool("save_artifact", ToolCategory.WriteExecute)];
        harness.AddHistory("assistant",
            "I saved the artifact",
            [Harness.CompletedToolPart("call-1", "save_artifact", "{\"name\":\"s6.txt\"}", "saved s6.txt")]);
        harness.CompactionSummary = "SYNOPSIS";
        harness.CompactionCoversToSequence = 0;

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var context = Context(harness);
        AssertEx.Contains(context, static message => message.Content.Contains("SYNOPSIS", StringComparison.Ordinal));
        var assistant = context.Single(static message => message.Role == MessageRole.Assistant);
        AssertEx.Equal("call-1", AssertEx.NotNull(assistant.ToolExchanges, "A completed call is a record the prose synopsis cannot carry.").Single().CallId);
        AssertEx.Equal(string.Empty, assistant.Content, "The synopsis already carries this turn's prose.");

        var messages = InvocationRunner.BuildChatMessages(AssertEx.NotNull(harness.CapturedPackage)).ToList();
        AssertEx.Equal(expected: 1, messages.SelectMany(static message => message.Contents).OfType<FunctionCallContent>().Count());
        AssertEx.False(messages.SelectMany(static message => message.Contents)
                               .OfType<TextContent>()
                               .Any(static text => text.Text.Contains("I saved the artifact", StringComparison.Ordinal)),
            "The folded prose must not ride the turn a second time alongside the synopsis.");
    }

    [Test]
    public async Task TwoCallsInOneTurn_StayTwoExchanges_WithOrWithoutProviderCallIds()
    {
        // Two calls are two parts either way. With provider ids the accumulator keys on them directly; with a blank
        // one the runner mints "<name>", then "<name>#2" (see InvocationRunnerTests
        // .RunAsync_WhenTheProviderStreamsTwoSameNameCallsWithABlankCallId_PairsEachResultWithItsOwnCall), which is
        // what this half raises. Before that surrogate both calls keyed on the bare tool name and collapsed into one
        // part, losing the first call and its result outright.
        using var distinct = new Harness();
        distinct.DuringRun = (running, package) =>
        {
            RaiseToolCall(running, package.InvocationId, "call-1", "list_files", "{\"path\":\"a\"}", "a.txt");
            RaiseToolCall(running, package.InvocationId, "call-2", "list_files", "{\"path\":\"b\"}", "b.txt");
        };

        await distinct.Coordinator.ProcessOneAsync(distinct.SeedAccepted(), CancellationToken.None);
        var distinctParts = AssertEx.NotNull(AssertEx.NotNull(distinct.TerminalizeRequest).Parts);
        AssertEx.Equal(expected: 2, distinctParts.Count);
        AssertEx.Equal("a.txt", distinctParts[0].Result);
        AssertEx.Equal("b.txt", distinctParts[1].Result);

        using var surrogates = new Harness();
        surrogates.DuringRun = (running, package) =>
        {
            RaiseToolCall(running, package.InvocationId, "list_files", "list_files", "{\"path\":\"a\"}", "a.txt");
            RaiseToolCall(running, package.InvocationId, "list_files#2", "list_files", "{\"path\":\"b\"}", "b.txt");
        };

        await surrogates.Coordinator.ProcessOneAsync(surrogates.SeedAccepted(), CancellationToken.None);
        var surrogateParts = AssertEx.NotNull(AssertEx.NotNull(surrogates.TerminalizeRequest).Parts);
        AssertEx.Equal(expected: 2, surrogateParts.Count, "The surrogate keeps the second id-less call off the first one's part.");
        AssertEx.Equal("a.txt", surrogateParts[0].Result);
        AssertEx.Equal("{\"path\":\"a\"}", surrogateParts[0].Args, "The requested phase owns the arguments; the completed phase only fills the result.");
        AssertEx.Equal("b.txt", surrogateParts[1].Result);
    }

    [Test]
    public async Task AnOversizedHistoricalResultIsExcerptedBeforeItRidesTheContinuation()
    {
        // Bounded at PROJECTION time, so one huge result cannot ride every later continuation whole.
        using var harness = new Harness();
        harness.SetSessionPolicy(IntegrationSessionPolicy.CallerManaged);
        var result = new string('r', ConversationContextBudgetOptions.DefaultHistoricalToolResultExcerptChars + 500);
        harness.AddHistory("assistant", "read it", [Harness.CompletedToolPart("call-1", "read_file", args: null, result)]);

        await harness.Coordinator.ProcessOneAsync(harness.SeedAccepted(), CancellationToken.None);

        var assistant = Context(harness).Single(message => message.Role == MessageRole.Assistant);
        var replayed = AssertEx.NotNull(AssertEx.NotNull(assistant.ToolExchanges).Single().Result);
        AssertEx.True(replayed.Length < result.Length, $"An oversized result must be excerpted, not replayed whole ({replayed.Length} of {result.Length}).");
        AssertEx.Contains(replayed, "500 chars omitted");
    }

    private static void RaiseToolCall(Harness harness, Guid invocationId, string callId, string toolName, string? args, string? result)
    {
        harness.Dispatcher.ToolCallLifecycleChanged += Raise.EventWith(new ToolCallLifecycleChangedEventArgs(new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = callId,
            ToolName = toolName,
            Phase = ToolCallLifecyclePhase.Requested,
            Arguments = args
        }));

        harness.Dispatcher.ToolCallLifecycleChanged += Raise.EventWith(new ToolCallLifecycleChangedEventArgs(new ToolCallLifecyclePayload
        {
            InvocationId = invocationId,
            ToolCallId = callId,
            ToolName = toolName,
            Phase = ToolCallLifecyclePhase.Completed,
            Result = result
        }));
    }

    private static IReadOnlyList<ConversationMessageDto> Context(Harness harness) =>
        (harness.CapturedPackage ?? throw new AssertionException("The runner was never called.")).ConversationContext;
}
