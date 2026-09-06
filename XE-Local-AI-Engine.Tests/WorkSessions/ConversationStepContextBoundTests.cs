namespace XE_Local_AI_Engine.Tests.WorkSessions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Compaction;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The step boundary's transcript bound. A session step replays every earlier step's state block, answer and
///     reasoning verbatim, and its own knowledge-base reads can spend 16k tokens on a single document — on 2026-08-24 a
///     27B model at a 65,536-token window went over at step 5. The bound folds the older turns before the send.
/// </summary>
public sealed class ConversationStepContextBoundTests
{
    [Test]
    public async Task Loop_WhenTheProjectedTranscriptExceedsTheBudget_ForcesCompactionWithASessionKeepWindow()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        var compaction = new RecordingCompactionService();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:StepContextBudgetTokens", "200")),
            ConfigureAdditionalTestServices = WithFakes(services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(),
                    services,
                    sessionId),
                publisher,
                compaction)
        };

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        // Well past a 200-token budget: ~4,000 characters of completed history at roughly four characters per token.
        await SeedTranscriptAsync(factory.Services, session.ConversationId, turns: 4, contentChars: 1_000).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, fake.Requests.Count);
        var forced = compaction.Calls.Where(call => call.ConversationId == session.ConversationId && call.KeepVerbatim is not null).ToList();
        AssertEx.NotEmpty(forced, "An over-budget step boundary must fold the session conversation before it sends.");
        AssertEx.Equal(ConversationStepContextBound.SessionKeepVerbatim,
            forced[0].KeepVerbatim,
            "The forced fold keeps one step verbatim, not the configured chat window.");
    }

    [Test]
    public async Task Loop_WhenTheProjectedTranscriptFitsTheBudget_SendsWithoutCompactingAtTheBoundary()
    {
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        var compaction = new RecordingCompactionService();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:StepContextBudgetTokens", "100000")),
            ConfigureAdditionalTestServices = WithFakes(services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(),
                    services,
                    sessionId),
                publisher,
                compaction)
        };

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await SeedTranscriptAsync(factory.Services, session.ConversationId, turns: 4, contentChars: 1_000).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        compaction.SendsSoFar = () => fake.Requests.Count;

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, fake.Requests.Count);
        // Only the folds that precede the send are the boundary's. The checkpoint the pause takes afterwards folds the
        // same conversation with the same window on purpose — that is what gives a short session a synopsis at all.
        AssertEx.Empty(compaction.Calls.Where(call => call.KeepVerbatim is not null && call.SendsBefore == 0).ToList(),
            "Under budget, the step boundary must not summarize — that is a local model call per step for nothing.");
    }

    [Test]
    public async Task StateBlock_AfterTheTranscriptIsFolded_StillCarriesEveryOpenTaskAndKeyFinding()
    {
        // The whole reason folding is safe: the state block is rebuilt from the database, not from what survived the
        // transcript. A step that sends after a forced fold must still see the plan and the findings.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        var compaction = new RecordingCompactionService();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:StepContextBudgetTokens", "200")),
            ConfigureAdditionalTestServices = WithFakes(services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(),
                    services,
                    sessionId),
                publisher,
                compaction)
        };

        var session = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        await SeedTranscriptAsync(factory.Services, session.ConversationId, turns: 4, contentChars: 1_000).ConfigureAwait(false);
        await SeedPlanAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.NotEmpty(compaction.Calls.Where(call => call.KeepVerbatim is not null).ToList());
        var sent = fake.Requests[0].Content;
        AssertEx.Contains(sent, "Read the runtime wiki", message: "The open task survives the fold.");
        AssertEx.Contains(sent, "Still open after folding", message: "The blocked task survives the fold.");
        AssertEx.Contains(sent, "llama.cpp is the default runtime", message: "The recorded finding survives the fold.");
    }

    [Test]
    public async Task Loop_SeedsTheTightenedToolResultBudget_ForTheDurationOfTheTurn()
    {
        // The ambient budget is what actually clips a knowledge-base read (ToolResultBudgetScopeTests covers the
        // clipping). What this proves is the half that fake-driven unit tests cannot: the value the supervisor seeds
        // reaches the code running INSIDE the turn, which is where the tool loop lives.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        int? seenInsideTurn = null;
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1"),
                ("WorkSessions:MaxToolResultCharacters", "16000")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted],
            DuringTurn: (_, _) =>
            {
                seenInsideTurn = ToolResultBudgetScope.Current;
                return Task.CompletedTask;
            }));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 16_000, seenInsideTurn, "The step's tightened tool-result budget must reach the tool loop.");
        AssertEx.True(ToolResultBudgetScope.Current is null, "The scope must not leak out of the step.");
    }

    [Test]
    public async Task Loop_WhenAStepSpendsItsProviderCallBudget_EndsTheStepAndRunsTheNextOne()
    {
        // The cap is a bound, not a fault: the tools the step ran are persisted and the state block carries the plan
        // forward, so ending the SESSION on its own safety limit would be the bug.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "2")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        // The message the step cap actually produces: the supervisor seeds a per-step cap tighter than the node-wide
        // ceiling, so the budget throws its step wording, which the classifier forwards verbatim onto the failed row.
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantFailed], TerminalError: ProviderCallBudget.StepCallCapReachedMessage));
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantCompleted]));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        var settled = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        AssertEx.Equal(expected: 2, fake.Requests.Count, "The next step must still be sent after the cap ends one.");
        AssertEx.Equal(expected: 2, settled.StepCount, "A capped step still counts as a step.");

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Contains(events, entry => entry.EventType == "StepEnded" && entry.Outcome == nameof(ProviderCallBudget));
        AssertEx.Empty(events.Where(entry => entry.EventType == "StepFailed").ToList(), "A spent budget is not a failure.");
    }

    [Test]
    public async Task Loop_WhenAStepHitsTheNodeWideCeiling_StillEndsTheStepRatherThanTheSession()
    {
        // The wider ceiling is a bound too. A session that reaches it is bounded, not broken, so the supervisor keeps
        // matching BOTH of the budget's fixed messages — dropping this one would move the same bug one ceiling out.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(("WorkSessions:MaxStepsPerRun", "1")),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantFailed], TerminalError: ProviderCallBudget.CeilingExceededMessage));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Paused).ConfigureAwait(false);

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Contains(events, entry => entry.EventType == "StepEnded" && entry.Outcome == nameof(ProviderCallBudget));
        AssertEx.Empty(events.Where(entry => entry.EventType == "StepFailed").ToList(), "A spent budget is not a failure.");
    }

    [Test]
    public async Task Loop_WhenAStepFailsForAnyOtherReason_StillFailsTheSession()
    {
        // The guard above keys on the budget's own fixed terminal message, so an ordinary failure must be unaffected.
        var sessionId = Guid.NewGuid();
        var publisher = new RecordingWorkSessionEventPublisher();
        FakeNodeChatStreamService? stream = null;
        await using var factory = new TestServerWebAppFactory
        {
            AdditionalConfiguration = WorkSessionTestSupport.Configuration(),
            ConfigureAdditionalTestServices = WorkSessionTestSupport.WithFakes(
                services => stream = new FakeNodeChatStreamService(services.GetRequiredService<INodeChatStreamCancellationRegistry>(), services, sessionId),
                publisher)
        };

        _ = await WorkSessionTestSupport.SeedSessionAsync(factory.Services, sessionId).ConfigureAwait(false);
        var fake = ResolveStream(factory, ref stream);
        fake.Enqueue(new StepScript([ChatStreamEventTypes.AssistantFailed], TerminalError: "The model went away."));

        AssertEx.True(factory.Services.GetRequiredService<IWorkSessionExecutionSupervisor>().TryStart(sessionId));
        _ = await WorkSessionTestSupport.WaitForStatusAsync(factory.Services, sessionId, AgentWorkSessionStatus.Failed).ConfigureAwait(false);

        var events = await WorkSessionTestSupport.ReadEventsAsync(factory.Services, sessionId).ConfigureAwait(false);
        AssertEx.Contains(events, entry => entry.EventType == "StepFailed");
    }

    [Test]
    public void Project_CountsReasoningAndIgnoresWhatTheSynopsisAlreadyCovers()
    {
        var estimator = new HeuristicTokenEstimator();
        var messages = new List<NodeChatPersistedMessageDto>
        {
            Message(sequence: 0, "user", new string('a', 4_000)),
            Message(sequence: 1, "assistant", new string('b', 400), new string('c', 4_000)),
            Message(sequence: 2, "user", new string('d', 400))
        };

        var whole = ConversationStepContextBound.Project(Conversation(messages), estimator);
        var covered = ConversationStepContextBound.Project(Conversation(messages, "SYNOPSIS", coversToSequence: 1), estimator);
        var withoutReasoning = ConversationStepContextBound.Project(Conversation([messages[0], Message(sequence: 1, "assistant", new string('b', 400)), messages[2]]),
            estimator);

        AssertEx.True(whole > 1_800, $"~8,800 characters of history should project well past 1,800 tokens, projected {whole}.");
        AssertEx.True(covered < whole / 2, $"A synopsis covering the first two messages should more than halve the projection, {covered} vs {whole}.");
        // Counted deliberately, and conservatively. Verified against Microsoft.Extensions.AI.OpenAI 10.9.0: a historical
        // TextReasoningContent is DROPPED by the Chat Completions content-part conversion, so a llama.cpp step never
        // actually carries it — but the Responses API (Codex) replays it and must, and over-counting the former is a
        // bound that fires early where under-counting the latter is the overflow this exists to prevent.
        AssertEx.True(whole - withoutReasoning > 800,
            $"Replayed reasoning is real input on a Responses-API provider and must be counted; dropping 4,000 characters of it changed the projection by {whole - withoutReasoning}.");
    }

    [Test]
    public void Project_CountsReplayedToolExchangesOnlyWhenToolHistoryIsOn()
    {
        // The bound decides the fold from this number, so it has to count what the turn will actually SEND. With tool
        // history off (chat, and every per-invocation run) a turn's persisted parts are invisible here, exactly as they
        // are invisible to the send path.
        var estimator = new HeuristicTokenEstimator();
        var messages = new List<NodeChatPersistedMessageDto>
        {
            Message(sequence: 0, "user", "list the files"),
            Message(sequence: 1, "assistant", "there are two files") with
            {
                Parts = [ToolPart("call-1", "list_files", new string('r', 1_200))]
            }
        };

        var off = ConversationStepContextBound.Project(Conversation(messages), estimator);
        var on = ConversationStepContextBound.Project(Conversation(messages), estimator, modelName: null, includeToolHistory: true);

        AssertEx.True(on > off + 200,
            $"A replayed 1,200-character tool result is real input and must be counted; the projection moved from {off} to {on}.");
    }

    [Test]
    public void Project_WithToolHistoryOn_CountsATurnKeptOnlyForItsCompletedToolCall()
    {
        // The keep rule the send path applies: a failed, blank turn that completed a tool call is still replayed, so the
        // estimate cannot pretend it is absent.
        var estimator = new HeuristicTokenEstimator();
        var failedWithSideEffect = Message(sequence: 0, "assistant", string.Empty) with
        {
            Status = NodeChatMessageStatusValues.Failed,
            Parts = [ToolPart("call-1", "save_artifact", new string('r', 1_200))]
        };

        var counted = ConversationStepContextBound.Project(Conversation([failedWithSideEffect]), estimator, modelName: null, includeToolHistory: true);
        var ignored = ConversationStepContextBound.Project(Conversation([failedWithSideEffect]), estimator);

        AssertEx.Equal(expected: 0, ignored, "With the flag off the turn is dropped by the ordinary content/status filter.");
        AssertEx.True(counted > 200, $"The replayed exchange is the whole of that turn's cost and must be counted, projected {counted}.");
    }

    [Test]
    public void Project_WithToolHistoryOn_CountsATurnTheCompactionCutoffWouldOtherwiseHaveDropped()
    {
        // The send path keeps a turn the synopsis could not have covered but that completed a tool call, so the fold
        // decision has to be made against a number that includes it. Counting it as absent is how the bound decides not
        // to fold and then overflows on exactly the content it did not measure.
        var estimator = new HeuristicTokenEstimator();
        var failedWithSideEffect = Message(sequence: 0, "assistant", string.Empty) with
        {
            Status = NodeChatMessageStatusValues.Failed,
            Parts = [ToolPart("call-1", "save_artifact", new string('r', 1_200))]
        };
        var conversation = Conversation([failedWithSideEffect, Message(sequence: 1, "user", "recent")], "SYNOPSIS", coversToSequence: 0);

        var counted = ConversationStepContextBound.Project(conversation, estimator, modelName: null, includeToolHistory: true);
        var folded = ConversationStepContextBound.Project(conversation, estimator);

        AssertEx.True(counted > folded + 200,
            $"The replayed exchange survives the cutoff and must be counted; the projection moved from {folded} to {counted}.");
    }

    [Test]
    public void Project_WithToolHistoryOn_CountsACoveredSendableTurnsExchangesButNotItsText()
    {
        // A COMPLETED turn below the cutoff that also called a tool survives for its exchange alone: the send path
        // blanks its text and reasoning because the synopsis already carries them. Counting the text here would measure
        // a request the turn does not send, and the bound would fold early on prose that is not there.
        var estimator = new HeuristicTokenEstimator();
        var completedWithSideEffect = Message(sequence: 0, "assistant", new string('t', 4_000), new string('k', 4_000)) with
        {
            Parts = [ToolPart("call-1", "save_artifact", "saved")]
        };
        var conversation = Conversation([completedWithSideEffect, Message(sequence: 1, "user", "recent")], "SYNOPSIS", coversToSequence: 0);

        var counted = ConversationStepContextBound.Project(conversation, estimator, modelName: null, includeToolHistory: true);
        var exchangeOnly = ConversationStepContextBound.Project(Conversation([
                    Message(sequence: 0, "assistant", string.Empty) with
                    {
                        Parts = [ToolPart("call-1", "save_artifact", "saved")]
                    },
                    Message(sequence: 1, "user", "recent")
                ],
                "SYNOPSIS",
                coversToSequence: 0),
            estimator,
            modelName: null,
            includeToolHistory: true);

        AssertEx.Equal(exchangeOnly, counted, "The covered turn's 8,000 characters of text and reasoning are folded away; only its exchange is counted.");
    }

    [Test]
    public void Project_WithToolHistoryOn_CountsTheExcerptedResult_NotTheWholeOne()
    {
        var estimator = new HeuristicTokenEstimator();
        var messages = new List<NodeChatPersistedMessageDto>
        {
            Message(sequence: 0, "assistant", "read it") with
            {
                Parts = [ToolPart("call-1", "read_file", new string('r', 8_000))]
            }
        };

        var capped = ConversationStepContextBound.Project(Conversation(messages), estimator, modelName: null, includeToolHistory: true, toolResultExcerptChars: 100);
        var uncapped = ConversationStepContextBound.Project(Conversation(messages), estimator, modelName: null, includeToolHistory: true, toolResultExcerptChars: 8_000);

        AssertEx.True(capped < uncapped / 2,
            $"The estimate must measure the EXCERPTED result the send path carries, not the whole one; {capped} vs {uncapped}.");
    }

    private static NodeChatMessagePart ToolPart(string callId, string name, string result) =>
        new(NodeChatMessagePartKinds.Tool,
            Sequence: 0,
            Text: null,
            callId,
            name,
            NodeChatToolPartStates.Received,
            Args: "{}",
            result);

    [Test]
    public void Project_WhenAMessageIsNotCompleted_LeavesItOut()
    {
        var estimator = new HeuristicTokenEstimator();
        var completed = Message(sequence: 0, "user", new string('a', 4_000));
        var streaming = completed with
        {
            MessageId = Guid.NewGuid(),
            Sequence = 1,
            Status = NodeChatMessageStatusValues.Streaming
        };

        var withStreaming = ConversationStepContextBound.Project(Conversation([completed, streaming]), estimator);
        var withoutStreaming = ConversationStepContextBound.Project(Conversation([completed]), estimator);

        AssertEx.Equal(withoutStreaming, withStreaming, "The send path drops non-completed messages, so the projection must too.");
    }

    [Test]
    public void Project_UsesTheCalibratedDivisorOfTheModelTheSessionIsRunningOn()
    {
        // The projection used to estimate with NO model name, so it always divided by the uncalibrated four while the
        // two context budgeters it is supposed to agree with were already dividing by the model's measured divisor. The
        // bound then fired against arithmetic nothing else in the turn used.
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor(SessionModel, charsPerToken: 2);
        var estimator = new HeuristicTokenEstimator(store);

        var calibrated = ConversationStepContextBound.Project(Conversation(TranscriptOn(SessionModel)), estimator);
        var uncalibrated = ConversationStepContextBound.Project(Conversation(TranscriptOn("a-model-nothing-was-measured-for")), estimator);

        AssertEx.True(calibrated > uncalibrated,
            $"A model measured at two characters per token must project more tokens for the same transcript than the chars/4 default; {calibrated} vs {uncalibrated}.");
    }

    [Test]
    public void Project_WithAnExplicitModelName_PrefersItOverTheTranscript()
    {
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor(SessionModel, charsPerToken: 2);
        var estimator = new HeuristicTokenEstimator(store);
        var transcript = Conversation(TranscriptOn("a-model-nothing-was-measured-for"));

        var explicitly = ConversationStepContextBound.Project(transcript, estimator, SessionModel);
        var derived = ConversationStepContextBound.Project(transcript, estimator);

        AssertEx.True(explicitly > derived,
            $"An explicitly supplied model must win over the one derived from the transcript; {explicitly} vs {derived}.");
    }

    [Test]
    public void Project_WhenNothingHasAnsweredYet_FallsBackToTheUncalibratedDefault()
    {
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor(SessionModel, charsPerToken: 2);
        var estimator = new HeuristicTokenEstimator(store);

        // A first step has no assistant message to read a model off, and must not throw or guess one.
        var firstStep = ConversationStepContextBound.Project(Conversation([Message(sequence: 0, "user", new string('a', 4_000))]), estimator);
        var plain = ConversationStepContextBound.Project(Conversation([Message(sequence: 0, "user", new string('a', 4_000))]), new HeuristicTokenEstimator());

        AssertEx.Equal(plain, firstStep);
    }

    [Test]
    public async Task ApplyAsync_WhenTheModelIsObservedToCostMoreThanEstimated_FoldsEarlier()
    {
        // The projection already estimates under the model's calibrated divisor. If the BUDGET it is compared against
        // stays uncalibrated, the two sides are in different arithmetic and the bound folds late on exactly the models
        // calibration exists to protect. Same transcript, same configured budget - only the correction differs.
        var store = new TokenEstimatorCalibrationStore();
        store.RecordObservedUsage(SessionModel, estimatedTokens: 10_000, observedInputTokens: 20_000);
        var correction = store.ResolveObservedCorrection(SessionModel);
        AssertEx.True(correction > 1.0, $"The fixture needs an above-neutral correction to mean anything; measured {correction}.");

        var conversation = Conversation(TranscriptOn(SessionModel));
        var projected = ConversationStepContextBound.Project(conversation, new HeuristicTokenEstimator(store), SessionModel);

        // A budget that the projection fits under uncalibrated, but not once the correction tightens it.
        var budget = projected + 1;
        AssertEx.True(TokenEstimatorCalibrationStore.ApplyObservedCorrection(budget, correction) < projected,
            "The fixture's budget must straddle the correction, or the pair below proves nothing.");

        var calibrated = await RunBoundAsync(conversation, budget, store).ConfigureAwait(false);
        var neutral = await RunBoundAsync(conversation, budget, new TokenEstimatorCalibrationStore()).ConfigureAwait(false);

        AssertEx.NotEmpty(calibrated.Calls, "An over-budget projection under the model's observed correction must fold.");
        AssertEx.Empty(neutral.Calls, "The same transcript and budget must NOT fold when nothing has been observed for the model.");
    }

    [Test]
    public async Task ApplyAsync_WithNoObservedCorrection_ComparesAgainstTheConfiguredBudgetUnchanged()
    {
        // The byte-identical guarantee for the flat budget: no safety factor is applied here, so an uncalibrated
        // session compares against exactly the number the operator configured.
        var conversation = Conversation(TranscriptOn(SessionModel));
        var store = new TokenEstimatorCalibrationStore();
        var projected = ConversationStepContextBound.Project(conversation, new HeuristicTokenEstimator(store), SessionModel);

        var atBudget = await RunBoundAsync(conversation, projected, store).ConfigureAwait(false);
        var overBudget = await RunBoundAsync(conversation, projected - 1, store).ConfigureAwait(false);

        AssertEx.Empty(atBudget.Calls, "A projection exactly at the budget is not over it.");
        AssertEx.NotEmpty(overBudget.Calls, "A projection one token over the budget must fold.");
    }

    [Test]
    public async Task ApplyAsync_WithASuppliedModel_CalibratesUnderItRatherThanTheTranscriptModel()
    {
        // The supervisor knows which model the UPCOMING step runs on; the transcript only knows the last one. A paused
        // session repointed to another agent (or an unpinned agent whose node default moved) runs the next step
        // elsewhere, and calibrating against the previous model folds late - the overflow this bound exists to stop.
        var store = new TokenEstimatorCalibrationStore();
        store.RecordObservedUsage(UpcomingModel, estimatedTokens: 10_000, observedInputTokens: 20_000);
        var correction = store.ResolveObservedCorrection(UpcomingModel);
        AssertEx.True(correction > 1.0, $"The fixture needs an above-neutral correction to mean anything; measured {correction}.");
        AssertEx.Equal(1.0, store.ResolveObservedCorrection(SessionModel), "The transcript's model must stay uncalibrated, or the pair below proves nothing.");

        var conversation = Conversation(TranscriptOn(SessionModel));
        var projected = ConversationStepContextBound.Project(conversation, new HeuristicTokenEstimator(store), UpcomingModel);
        var budget = projected + 1;
        AssertEx.True(TokenEstimatorCalibrationStore.ApplyObservedCorrection(budget, correction) < projected,
            "The fixture's budget must straddle the upcoming model's correction.");

        var supplied = await RunBoundAsync(conversation, budget, store, UpcomingModel).ConfigureAwait(false);
        var transcriptDerived = await RunBoundAsync(conversation, budget, store).ConfigureAwait(false);

        AssertEx.NotEmpty(supplied.Calls, "The supplied model's observed correction must be the one the budget is tightened by.");
        AssertEx.Empty(transcriptDerived.Calls, "With no supplied model the uncalibrated transcript model applies, and the same budget is not exceeded.");
    }

    [Test]
    public async Task ApplyAsync_WithNoSuppliedModel_FallsBackToTheTranscriptModel()
    {
        // The fallback still has to work: the agent definition can be gone, or the gate read can have failed, and the
        // transcript's model is then the best answer available.
        var store = new TokenEstimatorCalibrationStore();
        store.SetDivisor(SessionModel, charsPerToken: 2);
        var estimator = new HeuristicTokenEstimator(store);
        var conversation = Conversation(TranscriptOn(SessionModel));

        var calibrated = ConversationStepContextBound.Project(conversation, estimator, SessionModel);
        var uncalibrated = ConversationStepContextBound.Project(conversation, estimator, UpcomingModel);
        AssertEx.True(calibrated > uncalibrated, $"The fixture needs the two models to project differently; {calibrated} vs {uncalibrated}.");

        // A budget the uncalibrated projection sits exactly at - so only the transcript model's divisor exceeds it.
        var fallback = await RunBoundAsync(conversation, uncalibrated, store).ConfigureAwait(false);
        var supplied = await RunBoundAsync(conversation, uncalibrated, store, UpcomingModel).ConfigureAwait(false);

        AssertEx.NotEmpty(fallback.Calls, "A null supplied model must estimate under the transcript's model, whose divisor puts the projection over the budget.");
        AssertEx.Empty(supplied.Calls, "Supplying the upcoming model must override the transcript's, leaving the projection at the budget rather than over it.");
    }

    /// <summary>Drives ApplyAsync against one conversation and returns the compaction service it talked to.</summary>
    private static async Task<RecordingCompactionService> RunBoundAsync(NodeChatConversationDto conversation,
        int budgetTokens,
        TokenEstimatorCalibrationStore store,
        string? effectiveModel = null)
    {
        var persistence = Substitute.For<INodeChatPersistenceService>();
        _ = persistence.GetConversationForTurnAsync(conversation.ConversationId, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<NodeChatConversationDto?>(conversation));
        var compaction = new RecordingCompactionService();

        var sut = new ConversationStepContextBound(persistence,
            compaction,
            new HeuristicTokenEstimator(store),
            NullLogger<ConversationStepContextBound>.Instance);
        await sut.ApplyAsync(conversation.ConversationId, budgetTokens, effectiveModel).ConfigureAwait(false);
        return compaction;
    }

    private const string SessionModel = "qwen3.8-27b:Q4_K_M";

    /// <summary>The model a repoint (or a moved node default) puts the NEXT step on - not the one the transcript ran on.</summary>
    private const string UpcomingModel = "gemma-3-12b:Q5_K_M";

    /// <summary>A completed two-message transcript whose assistant turn ran on <paramref name="model" />.</summary>
    private static List<NodeChatPersistedMessageDto> TranscriptOn(string model) =>
    [
        Message(sequence: 0, "user", new string('a', 4_000)),
        Message(sequence: 1, "assistant", new string('b', 4_000)) with
        {
            Model = model
        }
    ];

    private static Action<IServiceCollection> WithFakes(Func<IServiceProvider, INodeChatStreamService> streamFactory,
        RecordingWorkSessionEventPublisher publisher,
        RecordingCompactionService compaction) =>
        services =>
        {
            WorkSessionTestSupport.WithFakes(streamFactory, publisher)(services);
            services.RemoveAll<IConversationCompactionService>();
            services.AddSingleton<IConversationCompactionService>(compaction);
        };

    private static FakeNodeChatStreamService ResolveStream(TestServerWebAppFactory factory, ref FakeNodeChatStreamService? stream)
    {
        _ = factory.Services.GetRequiredService<INodeChatStreamService>();
        return AssertEx.NotNull(stream, "The fake stream service must have been constructed from the container.");
    }

    /// <summary>Persists <paramref name="turns" /> completed user/assistant exchanges so the projection has something to measure.</summary>
    private static async Task SeedTranscriptAsync(IServiceProvider services, Guid conversationId, int turns, int contentChars)
    {
        await using var scope = services.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<INodeChatPersistenceService>();
        for (var turn = 0; turn < turns; turn++)
        {
            var messageId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            _ = await persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(conversationId,
                                     Guid.NewGuid(),
                                     new string('u', contentChars),
                                     CreatedAtUtc: turn))
                                 .ConfigureAwait(false);
            _ = await persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(conversationId, messageId, requestId, CreatedAtUtc: turn))
                                 .ConfigureAwait(false);
            _ = await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(new NodeChatMessageCorrelation(conversationId, messageId, requestId),
                                     NodeChatMessageStatusValues.Completed,
                                     UpdatedAtUtc: turn,
                                     new string('a', contentChars),
                                     new string('r', contentChars)))
                                 .ConfigureAwait(false);
        }
    }

    /// <summary>Seeds the durable state the folded transcript must not take with it.</summary>
    private static async Task SeedPlanAsync(IServiceProvider services, Guid sessionId)
    {
        await using var scope = services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAgentWorkSessionStore>();
        _ = await store.ApplyPlanAsync(new ApplyWorkPlanCommand(sessionId,
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionTaskOrigin.Agent,
                           [
                               new WorkPlanTaskChange(Guid.NewGuid(), WorkPlanTaskOperation.Add, Title: "Read the runtime wiki", Status: AgentWorkSessionTaskStatus.Active),
                               new WorkPlanTaskChange(Guid.NewGuid(), WorkPlanTaskOperation.Add, Title: "Still open after folding", Status: AgentWorkSessionTaskStatus.Planned)
                           ]))
                       .ConfigureAwait(false);
        _ = await store.AppendFindingAsync(new AppendWorkSessionFindingCommand(sessionId,
                           Guid.NewGuid(),
                           WorkSessionVersions.Any,
                           Guid.NewGuid(),
                           AgentWorkSessionFindingKind.Finding,
                           "llama.cpp is the default runtime"))
                       .ConfigureAwait(false);
    }

    private static NodeChatConversationDto Conversation(IReadOnlyList<NodeChatPersistedMessageDto> messages, string? summary = null, int? coversToSequence = null) =>
        new(ConversationId: Guid.NewGuid(),
            Title: null,
            UserId: null,
            CreatedAtUtc: 0,
            LastSeenUtc: 0,
            Purged: false,
            Messages: messages,
            CompactionSummary: summary,
            CompactionSummaryCoversToSequence: coversToSequence);

    private static NodeChatPersistedMessageDto Message(int sequence, string role, string content, string? reasoning = null) =>
        new(Guid.NewGuid(),
            ConversationId: Guid.NewGuid(),
            RequestId: null,
            sequence,
            role,
            content,
            reasoning,
            NodeChatMessageStatusValues.Completed,
            CreatedAtUtc: sequence,
            UpdatedAtUtc: sequence,
            Model: null,
            Error: null,
            MetadataJson: null);

    /// <summary>Records every compaction the loop asks for, including the keep window it asked with.</summary>
    private sealed class RecordingCompactionService : IConversationCompactionService
    {
        public List<(Guid ConversationId, int? KeepVerbatim, int SendsBefore)> Calls { get; } = [];

        /// <summary>
        ///     Sends observed so far, when a test sets it. The step boundary and the checkpoint composer now both fold
        ///     the same conversation with the same keep window, so the keep window alone no longer says which one
        ///     called: the boundary folds BEFORE the step's send, the checkpoint only after it.
        /// </summary>
        public Func<int>? SendsSoFar { get; set; }

        public Task<ConversationCompactionResult> CompactAsync(Guid conversationId,
            string? requestedModel,
            int? recentMessagesToKeepVerbatim,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((conversationId, recentMessagesToKeepVerbatim, SendsSoFar?.Invoke() ?? 0));
            return Task.FromResult(new ConversationCompactionResult(ConversationCompactionOutcome.NothingToCompact));
        }
    }
}
