namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation.Policy;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

// The single-agent stream loop: one provider segment at a time, the per-update tool-call lifecycle, the approval round-trips,
// the tool-call-in-reasoning re-prompt, and the end-of-turn checks (leaked think text, empty answer, output limit).
public sealed partial class InvocationRunner
{
    // One provider segment of the single-agent loop: budget the grown history, stream the segment, then run its approval
    // round-trips or arm the one tool-call-in-reasoning re-prompt. RunSingleAgentAsync repeats it while either is pending.
    private async Task RunSegmentAsync(SingleAgentLoop loop,
        RuntimePackage package,
        string resolvedModel,
        StreamTransport transport,
        StreamState stream,
        TurnPolicy turnPolicy,
        CancellationToken invocationToken)
    {
        // Growth point (b): re-budget the approval-grown message list before each provider round — a cheap passthrough on the first iteration, and a bound on
        // the folded tool-call + approval history on a resume. The protected recent turns carry the in-flight round and are never trimmed, so the list stays valid.
        var budgetedMessages = await ApplyContextBudgetAsync(loop.CurrentMessages, package, loop.ToolBudgetDefinitions, resolvedModel, "tool-loop", turnPolicy, transport, loop.BudgetGate);
        if (!ReferenceEquals(budgetedMessages, loop.CurrentMessages))
        {
            loop.CurrentMessages = budgetedMessages as List<ChatMessage> ?? [.. budgetedMessages];
        }

        loop.PendingApprovals.Clear();
        loop.PendingApprovalKeys.Clear();
        loop.RepromptPending = false;
        var segmentUpdates = new List<AgentResponseUpdate>();

        // A provider round's wait for its first output is prompt prefill, which streams nothing and grows with the prompt, so the idle bound is suspended
        // until that output arrives: at the segment's start and after each tool result. The whole-turn deadline still bounds the wait.
        loop.AwaitingRoundOutput = true;

        // The idle watchdog owns the token the provider call binds cancellation to, so an idle expiry actually cancels the send. The first segment also
        // runs through the pre-first-token retry + circuit breaker, which re-invokes this whole factory, so a fresh idle watchdog guards every attempt.
        IAsyncEnumerable<AgentResponseUpdate> ProviderSend(CancellationToken sendToken)
        {
            return StreamIdleWatchdog.WithIdleTimeout(innerToken => loop.AgentContext.Agent.RunStreamingAsync(loop.CurrentMessages, session: null, loop.AgentContext.RunOptions, innerToken),
                loop.StreamIdleTimeout,
                loop.StreamIdleTimeoutMessage,
                _timeProvider,
                sendToken,
                isSuspended: () => loop.OpenToolCalls.Any || loop.AwaitingRoundOutput);
        }

        var segmentStream = loop.IsFirstSegment
            ? _providerStreamResilience.ExecuteStreamingAsync(resolvedModel, ProviderSend, invocationToken)
            : ProviderSend(invocationToken);

        await foreach (var update in segmentStream.WithCancellation(invocationToken))
        {
            await ProcessUpdateAsync(loop, update, segmentUpdates, package, transport, stream);
        }

        // Drained at the end of the FIRST segment so it FOLLOWS the first assistant text, exactly as HistoryTruncated does: the hop cannot reach the
        // transport and leaves its counts on the ambient scope. Counts only, never a tool name; a first segment that THROWS emits nothing, since the line would read as the cause.
        if (loop.IsFirstSegment && ToolRelevanceScope.Current is { } relevanceState)
        {
            var hiddenToolCount = Volatile.Read(ref relevanceState.PendingNoticeHiddenCount);
            if (hiddenToolCount > 0)
            {
                await transport.EmitNoticeAsync(TurnNoticeKind.ToolsFiltered,
                    BuildToolsFilteredNoticeMessage(hiddenToolCount, Volatile.Read(ref relevanceState.PendingNoticeTotalCount)));
            }
        }

        loop.OpenToolCalls.Clear();

        // The first segment has drained; any resume segment past this point follows earlier output and must not be
        // retried (a retry there would replay already-streamed chunks).
        loop.IsFirstSegment = false;

        if (loop.PendingApprovals.Count > 0)
        {
            await ResolvePendingApprovalsAsync(loop, segmentUpdates, package, invocationToken);
        }
        else if (!loop.FinalRoundHasOutput && loop.RetainSegmentUpdates && !loop.ReasoningToolCallReprompted && !invocationToken.IsCancellationRequested
                 && ReasoningCarriesToolCall(stream, loop.FinalRoundReasoningStart))
        {
            ArmReasoningToolCallReprompt(loop, segmentUpdates, stream);
        }
    }

    // One streamed update of a segment: answer text, reasoning, usage and finish reason, plus the tool-call lifecycle events and
    // approval requests its contents carry.
    private async Task ProcessUpdateAsync(SingleAgentLoop loop,
        AgentResponseUpdate update,
        List<AgentResponseUpdate> segmentUpdates,
        RuntimePackage package,
        StreamTransport transport,
        StreamState stream)
    {
        // Any update but a tool result is the round's output; a result re-arms the wait below.
        loop.AwaitingRoundOutput = false;

        if (loop.RetainSegmentUpdates)
        {
            segmentUpdates.Add(update);
        }

        var textChunk = update.Text;
        if (!string.IsNullOrEmpty(textChunk))
        {
            loop.FinalRoundHasOutput = true;
        }

        // Last-wins across the whole turn, segments included: an intermediate tool-call segment must not be the
        // reason the turn is recorded as having stopped.
        if (update.FinishReason is { } finishReason && !string.IsNullOrEmpty(finishReason.Value))
        {
            stream.FinishReason = finishReason.Value;
        }

        // Folded on ARRIVAL, not once per drained stream: a tool-calling turn is several llama-server requests inside ONE RunStreamingAsync, which
        // FunctionInvokingChatClient loops internally, so last-wins keeps only the final one. `timings` rides each request's LAST chunk; `timings_per_token`, which double-counts, stays off.
        stream.AddSegmentTimings(LlamaServerGenerationTimings.TryRead(update.RawRepresentation));

        // Reasoning text and the terminal usage snapshot are pulled in the SAME pass that fires the tool-call lifecycle events, so a streamed token
        // costs one scan. Local tools run inside FunctionInvokingChatClient, so detecting the call/result content here is what puts their lifecycle events on the SSE stream.
        StringBuilder? thinkingBuilder = null;
        UsageDetails? usage = null;
        // How much of this update's reasoning and whether its text were emitted ahead of a requested call in it.
        loop.FlushedThinkingLength = 0;
        loop.TextFlushed = false;

        if (update.Contents is { Count: > 0 } contents)
        {
            foreach (var content in contents)
            {
                switch (content)
                {
                    case TextReasoningContent reasoning:
                        (thinkingBuilder ??= new StringBuilder()).Append(reasoning.Text);
                        break;

                    case UsageContent usageContent:
                        // LastOrDefault semantics: the last usage content in the update wins.
                        usage = usageContent.Details;
                        break;

                    case FunctionCallContent functionCall:
                        await ReportFunctionCallAsync(loop, functionCall, textChunk, thinkingBuilder, package, transport, stream);
                        break;

                    case FunctionResultContent functionResult:
                        await ReportFunctionResultAsync(loop, functionResult, package, transport, stream);
                        break;

                    case ToolApprovalRequestContent approvalRequest:
                        // FunctionInvokingChatClient surfaces this for an ApprovalRequiredAIFunction instead of executing the tool. Capture EVERY
                        // request in the segment, deduped across re-emitting chunks; the outer loop then runs each round-trip and resumes threadlessly.
                        if (!ToolApprovalCoordinator.IsDuplicatePendingApproval(approvalRequest, loop.PendingApprovals, loop.PendingApprovalKeys))
                        {
                            loop.PendingApprovals.Add(approvalRequest);
                        }

                        break;
                }
            }
        }

        if (thinkingBuilder is { Length: > 0 } || !string.IsNullOrEmpty(textChunk))
        {
            loop.OpenToolCalls.OnProviderOutput();
        }

        // "Answer now" may only fire while the armed completion is still reasoning: answer text or a finished completion ends that phase.
        stream.ReasoningControl?.Observe(update.RawRepresentation,
            sawReasoning: thinkingBuilder is { Length: > 0 },
            sawAnswer: !string.IsNullOrEmpty(textChunk) || update.FinishReason is not null);

        if (usage is not null)
        {
            // ACCUMULATED, not assigned — same reason as AddSegmentTimings above: one UsageContent arrives per
            // provider round inside the single RunStreamingAsync, so last-wins reported only the final round.
            stream.AddUsage(usage);
            var cumulativeUsage = stream.UsageSnapshot!;
            _logger.LogDebug("Received cumulative usage for invocation {InvocationId}: input={InputTokens}, output={OutputTokens}, reasoning={ReasoningTokens}, total={TotalTokens}.",
                package.InvocationId,
                cumulativeUsage.InputTokens,
                cumulativeUsage.OutputTokens,
                cumulativeUsage.ReasoningTokens,
                cumulativeUsage.TotalTokens);
        }

        if (thinkingBuilder is not null && thinkingBuilder.Length > loop.FlushedThinkingLength)
        {
            await transport.EmitReasoningAsync(stream, thinkingBuilder.ToString(loop.FlushedThinkingLength, thinkingBuilder.Length - loop.FlushedThinkingLength));
        }

        if (loop.TextFlushed || string.IsNullOrEmpty(textChunk))
        {
            return;
        }

        await transport.EmitTextAsync(stream, textChunk);
    }

    // A FunctionCallContent: resolve its card id (a surrogate for a blank provider id), skip a streamed re-emission, flush the
    // narration riding the same update ahead of it, and report the call Requested.
    private static async Task ReportFunctionCallAsync(SingleAgentLoop loop,
        FunctionCallContent functionCall,
        string? textChunk,
        StringBuilder? thinkingBuilder,
        RuntimePackage package,
        StreamTransport transport,
        StreamState stream)
    {
        // A BLANK CallId takes an invocation-local surrogate (openSurrogateCallIds above), so a second same-name call is its own
        // card and its result is never reported under an id the Requested event did not use.
        var callName = functionCall.Name ?? string.Empty;
        var openSurrogate = string.IsNullOrEmpty(functionCall.CallId) && loop.OpenSurrogateCallIds.TryGetValue(callName, out var open)
            ? open
            : null;
        var callId = openSurrogate ?? ResolveToolCallCardId(functionCall.CallId, functionCall.Name);

        // A retired surrogate is never revived. This runs BEFORE the repeat checks below, so they compare against the fresh key
        // and cannot mistake a genuine second call for a re-emission of the finished one.
        if (string.IsNullOrEmpty(functionCall.CallId) && openSurrogate is null && loop.UsedSurrogateNames.Contains(callName))
        {
            callId = string.Concat(callName, "#", (++loop.SurrogateCallCount).ToString(CultureInfo.InvariantCulture));
        }

        // A provider re-emitting the SAME call across streamed chunks would otherwise pay a Serialize + dispatch + SignalR frame per
        // repeat and evict a real event from InvocationResumeRegistry's CAPPED tool history. Both guards are conservative: a distinct call still reports.
        var isRepeatedCall = loop.PendingLocalToolCalls.TryGetValue(callId, out var alreadyRequested)
                             && string.Equals(alreadyRequested.Name, callName, StringComparison.Ordinal);

        // Same content instance re-emitted: identical by construction, so skip the serialize too.
        if (isRepeatedCall && ReferenceEquals(alreadyRequested.Arguments, functionCall.Arguments))
        {
            return;
        }

        var serializedArguments = functionCall.Arguments is not null
            ? JsonSerializer.Serialize(functionCall.Arguments)
            : null;

        // Distinct instance, byte-identical payload: the event would be indistinguishable from the one already on the wire.
        // Cache the new instance so the next repeat takes the cheaper reference check above.
        if (isRepeatedCall && string.Equals(alreadyRequested.SerializedArguments, serializedArguments, StringComparison.Ordinal))
        {
            loop.PendingLocalToolCalls[callId] = new RequestedToolCall(alreadyRequested.Name, functionCall.Arguments, alreadyRequested.SerializedArguments);
            return;
        }

        if (string.IsNullOrEmpty(functionCall.CallId))
        {
            // A surrogate still OPEN here means the payload DIFFERS from that call's. With no id, a second overlapping call and a
            // revision of the same card are indistinguishable, and collapsing them loses a call and its result where separating costs one extra card.
            if (openSurrogate is not null)
            {
                callId = string.Concat(callName, "#", (++loop.SurrogateCallCount).ToString(CultureInfo.InvariantCulture));
            }

            _ = loop.UsedSurrogateNames.Add(callName);
            loop.OpenSurrogateCallIds[callName] = callId;
            loop.PendingSurrogateResults.Enqueue(new SurrogateCall(callName, callId));
        }

        // callName, not functionCall.Name: the local above null-coalesces the property the compiler still treats as maybe-null.
        // The two are the same string whenever the provider gave a name at all.
        loop.PendingLocalToolCalls[callId] = new RequestedToolCall(callName, functionCall.Arguments, serializedArguments);
        loop.OpenToolCalls.Open(callId);
        loop.FinalRoundHasOutput = true;

        // The call's content offset must include narration riding the SAME update, or it lands in the answer after the card.
        if (thinkingBuilder is not null && thinkingBuilder.Length > loop.FlushedThinkingLength)
        {
            await transport.EmitReasoningAsync(stream, thinkingBuilder.ToString(loop.FlushedThinkingLength, thinkingBuilder.Length - loop.FlushedThinkingLength));
            loop.FlushedThinkingLength = thinkingBuilder.Length;
        }

        if (!loop.TextFlushed && !string.IsNullOrEmpty(textChunk))
        {
            await transport.EmitTextAsync(stream, textChunk);
            loop.TextFlushed = true;
        }

        await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
        {
            InvocationId = package.InvocationId,
            ToolCallId = callId,
            ToolName = callName,
            Phase = ToolCallLifecyclePhase.Requested,
            Arguments = serializedArguments,
            RequiresApproval = false
        });
    }

    // A FunctionResultContent: pair a blank result id with its surrogate, close the call, start a new model round, report the
    // call Completed, and surface a tool the repair wrapper disabled once per tool.
    private static async Task ReportFunctionResultAsync(SingleAgentLoop loop,
        FunctionResultContent functionResult,
        RuntimePackage package,
        StreamTransport transport,
        StreamState stream)
    {
        // MEAI stamps the result with the CALL's id, so a blank call id yields a blank result id. Pair it with the oldest surrogate
        // still awaiting one (results arrive in call order without ids): Completed under the empty string is dropped by every id-correlating consumer.
        var resultCallId = functionResult.CallId ?? string.Empty;
        if (string.IsNullOrEmpty(resultCallId) && loop.PendingSurrogateResults.TryDequeue(out var surrogate))
        {
            resultCallId = surrogate.CallId;

            // That call is closed: the next same-name call with no id opens its own card
            // instead of reviving this one.
            if (loop.OpenSurrogateCallIds.TryGetValue(surrogate.Name, out var stillOpen)
                && string.Equals(stillOpen, resultCallId, StringComparison.Ordinal))
            {
                _ = loop.OpenSurrogateCallIds.Remove(surrogate.Name);
            }
        }

        loop.OpenToolCalls.Close(resultCallId);
        var toolName = loop.PendingLocalToolCalls.TryGetValue(resultCallId, out var requested)
            ? requested.Name
            : resultCallId;
        var toolResultText = functionResult.Result?.ToString();

        // A result starts a new model round, which must produce its own text or call to count as an answer.
        loop.FinalRoundHasOutput = false;
        loop.AwaitingRoundOutput = true;
        loop.FinalRoundContentStart = stream.ResponseBuilder.Length;
        loop.FinalRoundReasoningStart = stream.ReasoningBuilder.Length;

        // Media rides beside the model's text (ToolResultMedia) so an image result persists as an image, never as a type name.
        await transport.Dispatcher.ReportToolCallLifecycleAsync(new ToolCallLifecyclePayload
        {
            InvocationId = package.InvocationId,
            ToolCallId = resultCallId,
            ToolName = toolName,
            Phase = ToolCallLifecyclePhase.Completed,
            Result = toolResultText,
            IsError = functionResult.Exception is not null || ToolFailureText.TryGetCode(functionResult.Result) is not null,
            Media = ToolResultMedia.Get(functionResult.Result),
            DurationMs = ToolExecutionTimingScope.TryGet(resultCallId)
        });

        // ToolArgumentRepairAIFunction returns this structured result instead of throwing once repeated invalid-argument calls disable
        // a tool for the rest of the run; without this it is visible only in the tool-result JSON. Once per tool — every further call returns the same marker.
        if (IsToolDisabledResult(toolResultText) && loop.NotifiedDisabledTools.Add(toolName))
        {
            await transport.EmitNoticeAsync(TurnNoticeKind.ToolDisabled,
                BuildToolDisabledNoticeMessage(toolName),
                toolName);
        }
    }

    // The approval round-trips of a segment that ended on approval requests; the next segment replays the folded history plus
    // one user message carrying every response.
    private async Task ResolvePendingApprovalsAsync(SingleAgentLoop loop,
        List<AgentResponseUpdate> segmentUpdates,
        RuntimePackage package,
        CancellationToken invocationToken)
    {
        // Fold the streamed segment into messages (the assistant tool calls plus the approval requests), run EACH round-trip over the existing transport,
        // which presents one at a time, then replay history plus ONE user message carrying every response, from which FunctionInvokingChatClient executes or rejects each call.
        var foldedMessages = segmentUpdates.ToAgentResponse().Messages;
        loop.CurrentMessages.AddRange(foldedMessages);

        // Each web call is consented to one card at a time, outside the tool window; the consented ones are then retrieved at once, before the first
        // review card, and the tool time is not charged to the model. A refused request has no preview, so the loop below hands its text to the model.
        IReadOnlyDictionary<ToolApprovalRequestContent, WebReviewRetrieval> webRetrievals = FrozenDictionary<ToolApprovalRequestContent, WebReviewRetrieval>.Empty;
        if (loop.PendingApprovals.Exists(ToolApprovalCoordinator.IsWebReviewRequest))
        {
            var consented = new List<ToolApprovalRequestContent>();
            var retrievals = new Dictionary<ToolApprovalRequestContent, WebReviewRetrieval>();
            foreach (var webRequest in loop.PendingApprovals.Where(ToolApprovalCoordinator.IsWebReviewRequest))
            {
                if (await _toolApprovalCoordinator.RequestWebConsentAsync(package, webRequest, ParkFor(package), invocationToken) is { } refusal)
                {
                    retrievals[webRequest] = refusal;
                }
                else
                {
                    consented.Add(webRequest);
                }
            }

            if (consented.Count > 0)
            {
                _lifecycleTracker.SetToolExecuting(package.InvocationId, executing: true);
                try
                {
                    foreach (var (webRequest, retrieval) in await _toolApprovalCoordinator.RetrieveWebContentAsync(package, consented, invocationToken))
                    {
                        retrievals[webRequest] = retrieval;
                    }
                }
                finally
                {
                    _lifecycleTracker.SetToolExecuting(package.InvocationId, executing: false);
                }
            }

            webRetrievals = retrievals;
        }

        var approvalResponses = new List<AIContent>(loop.PendingApprovals.Count);
        var deniedRequests = new List<ToolApprovalRequestContent>();
        var approvedToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var approvalRequest in loop.PendingApprovals)
        {
            // A web review, like ask_user, always approves: the handler returns whatever the review stashed, the decline text included.
            if (webRetrievals.TryGetValue(approvalRequest, out var webRetrieval))
            {
                var reviewNote = await _toolApprovalCoordinator.RequestWebReviewAsync(package, approvalRequest, webRetrieval, ParkFor(package), invocationToken);
                approvalResponses.Add(approvalRequest.CreateResponse(approved: true, reviewNote));
                loop.OpenToolCalls.OpenApproved(approvalRequest);
                continue;
            }

            // ask_user rides the approval seam for its BLOCKING behaviour, not a risk verdict (AskUserToolHandler): its round-trip collects an ANSWER
            // and then always approves, so the framework executes the tool and the handler returns that answer. Every other tool keeps the approve/deny path.
            if (ToolApprovalCoordinator.IsUserQuestionRequest(approvalRequest))
            {
                var answerNote = await _toolApprovalCoordinator.RequestUserAnswerAsync(package, approvalRequest, ParkFor(package), invocationToken);
                approvalResponses.Add(approvalRequest.CreateResponse(approved: true, answerNote));
                loop.OpenToolCalls.OpenApproved(approvalRequest);
                continue;
            }

            var approved = await _toolApprovalCoordinator.RequestToolApprovalAsync(package, approvalRequest, ParkFor(package), invocationToken);
            approvalResponses.Add(approvalRequest.CreateResponse(approved, approved ? ApprovedToolCallReason : DeniedToolCallReason));
            if (approved)
            {
                loop.OpenToolCalls.OpenApproved(approvalRequest);
                _ = approvedToolNames.Add((approvalRequest.ToolCall as FunctionCallContent)?.Name ?? string.Empty);
            }
            else
            {
                deniedRequests.Add(approvalRequest);
            }
        }

        // A tool approved for another call of this batch stays offered: the replay round runs that call, and withholding the tool would fail it.
        foreach (var deniedRequest in deniedRequests.Where(request => !approvedToolNames.Contains((request.ToolCall as FunctionCallContent)?.Name ?? string.Empty)))
        {
            WithholdDeniedTool(loop, deniedRequest);
        }

        loop.CurrentMessages.Add(new ChatMessage(ChatRole.User, approvalResponses));
    }

    // N-85: a denied tool leaves the offer for the rest of this turn, so the model cannot request it again and then invent its result; the next turn
    // offers it again. The MAF skill tools stay: their approval is per skill, and one denied skill must not hide the others.
    private static void WithholdDeniedTool(SingleAgentLoop loop, ToolApprovalRequestContent approvalRequest)
    {
        if ((approvalRequest.ToolCall as FunctionCallContent)?.Name is not { } toolName
            || ToolRelevanceChatClient.SkillToolNames.Contains(toolName, StringComparer.Ordinal)
            || !loop.DeniedToolNames.Add(toolName)
            || loop.DeniedToolNames.Count > 1
            || loop.AgentContext.RunOptions is not (null or ChatClientAgentRunOptions))
        {
            return;
        }

        // Installed once, at the first denial: the filter reads the live set, and it wraps the agent's whole pipeline, so the function invoker never sees the tool either.
        var runOptions = loop.AgentContext.RunOptions as ChatClientAgentRunOptions ?? new ChatClientAgentRunOptions();
        var innerFactory = runOptions.ChatClientFactory;
        var denied = loop.DeniedToolNames;
        runOptions.ChatClientFactory = client => (innerFactory?.Invoke(client) ?? client)
                                                 .AsBuilder()
                                                 .ConfigureOptions(options =>
                                                 {
                                                     if (options.Tools is { Count: > 0 } tools)
                                                     {
                                                         options.Tools = [.. tools.Where(tool => !denied.Contains(tool.Name))];
                                                     }
                                                 })
                                                 .Build();
        loop.AgentContext.RunOptions = runOptions;
    }

    // The one re-prompt after a final round whose reasoning holds an unparsed tool call: replay the segment without its reasoning
    // plus a user nudge, with thinking off for the rest of the turn.
    private static void ArmReasoningToolCallReprompt(SingleAgentLoop loop, List<AgentResponseUpdate> segmentUpdates, StreamState stream)
    {
        // The folded segment keeps every executed call and result but no reasoning, so the unparsed call is not replayed. A nudge recovered
        // the bare 9B 5/5 at b10201 (.tmp/model-matrix-data/phase2/track-1b/probe-summary.txt).
        loop.ReasoningToolCallReprompted = true;
        foreach (var folded in segmentUpdates.ToAgentResponse().Messages)
        {
            var kept = folded.Contents.Where(static content => content is not TextReasoningContent).ToList();
            if (kept.Exists(static content => content is not TextContent text || !string.IsNullOrWhiteSpace(text.Text)))
            {
                loop.CurrentMessages.Add(new ChatMessage(folded.Role, kept));
            }
        }

        loop.CurrentMessages.Add(new ChatMessage(ChatRole.User, ReasoningToolCallRepromptMessage));

        // Thinking stays off for the rest of the turn: back on after the re-prompt, the 9B put its next call in the reasoning
        // again (live C1: 2 of 6), with thinking off it never did. The next turn's budget starts with it on.
        ProviderCallBudget.Current?.TurnThinkingOff();
        loop.FinalRoundContentStart = stream.ResponseBuilder.Length;
        loop.FinalRoundReasoningStart = stream.ReasoningBuilder.Length;
        loop.RepromptPending = true;
    }

    // The end-of-turn checks on the final round: leaked think text moves to the reasoning, then at most one of the empty-answer,
    // tool-call-as-text and output-limit notices.
    private static async Task CompleteSingleAgentTurnAsync(SingleAgentLoop loop,
        RuntimePackage package,
        StreamTransport transport,
        StreamState stream,
        ChatOutputCap outputCap,
        CancellationToken invocationToken)
    {
        // A thinking model leaks its deliberation into the answer with thinking off too (LFM2.5 at effort none, model-matrix W3),
        // so its round is checked whether or not the reasoning channel carried anything.
        if ((package.SupportsThinking || stream.ReasoningBuilder.Length > loop.FinalRoundReasoningStart) && !invocationToken.IsCancellationRequested
                                                                                                         && await ReclassifyLeakedThinkTextAsync(transport, stream, loop.FinalRoundContentStart,
                                                                                                             package.SupportsThinking) is { } hasAnswer)
        {
            loop.FinalRoundHasOutput = hasAnswer;
        }

        var finalRoundReasoned = stream.ReasoningBuilder.Length > loop.FinalRoundReasoningStart;

        // Not for a cancelled turn: the caller reports the cancellation. A round that reasoned and answered nothing ran out
        // while thinking whatever the finish reason: a budget-forced end finishes "stop", the window "length".
        if (!loop.FinalRoundHasOutput && !invocationToken.IsCancellationRequested)
        {
            var message = finalRoundReasoned ? StoppedWhileThinkingNoticeMessage : EmptyAnswerNoticeMessage;
            if (ReasoningCarriesToolCall(stream, loop.FinalRoundReasoningStart))
            {
                message = ToolCallInReasoningNoticeMessage;
            }

            await transport.EmitNoticeAsync(TurnNoticeKind.EmptyAnswer, message, stream.FinishReason);
        }
        // The content-channel twin of a call in the reasoning: the text stays, since a turn never loses its only visible text, and the notice says it is no answer.
        else if (loop.FinalRoundHasOutput && !invocationToken.IsCancellationRequested && AnswerIsOnlyToolCallMarkup(stream, loop.FinalRoundContentStart))
        {
            await transport.EmitNoticeAsync(TurnNoticeKind.EmptyAnswer, ToolCallAsTextNoticeMessage, stream.FinishReason);
        }
        else if (loop.FinalRoundHasOutput)
        {
            await EmitOutputLimitNoticeIfReachedAsync(transport, stream, outputCap, invocationToken);
        }
    }

    // The one "stopped at length" rule for the single-agent turn and the orchestration run alike.
    private static async Task EmitOutputLimitNoticeIfReachedAsync(StreamTransport transport, StreamState stream, ChatOutputCap outputCap, CancellationToken invocationToken)
    {
        if (outputCap.NotifiesOnLength && !invocationToken.IsCancellationRequested
                                       && string.Equals(stream.FinishReason, "length", StringComparison.Ordinal))
        {
            await transport.EmitNoticeAsync(TurnNoticeKind.OutputLimitReached, OutputLimitReachedNoticeMessage, stream.FinishReason);
        }
    }

    private readonly record struct SurrogateCall(string Name, string CallId);

    // Qwen-family templates write a call as <tool_call>…</tool_call>; inside the reasoning block llama-server leaves it unparsed.
    private static bool ReasoningCarriesToolCall(StreamState stream, int reasoningStart) =>
        stream.ReasoningBuilder.Length > reasoningStart
        && stream.ReasoningBuilder.ToString(reasoningStart, stream.ReasoningBuilder.Length - reasoningStart).Contains("<tool_call>", StringComparison.Ordinal);

    // Strict: the whole trimmed round is one or more <tool_call> blocks and nothing else, so an answer that explains the syntax is untouched.
    // The last block may be unclosed: a model that stops mid-call (F-13) leaves only markup all the same.
    private static bool AnswerIsOnlyToolCallMarkup(StreamState stream, int contentStart)
    {
        const string OpenTag = "<tool_call>";
        const string CloseTag = "</tool_call>";
        var rest = stream.ResponseBuilder.ToString(contentStart, stream.ResponseBuilder.Length - contentStart).AsSpan().Trim();
        if (rest.IsEmpty)
        {
            return false;
        }

        while (!rest.IsEmpty)
        {
            if (!rest.StartsWith(OpenTag, StringComparison.Ordinal))
            {
                return false;
            }

            var close = rest.IndexOf(CloseTag, StringComparison.Ordinal);
            if (close < 0)
            {
                // Unclosed: markup only when a JSON object follows the tag, so prose that merely starts with the tag stays an answer.
                return rest[OpenTag.Length..].TrimStart().StartsWith('{');
            }

            rest = rest[(close + CloseTag.Length)..].TrimStart();
        }

        return true;
    }

    /// <summary>
    ///     Moves leaked think text from the final round's answer into the reasoning and reports the corrected texts so
    ///     the terminal message persists them.
    /// </summary>
    /// <remarks>
    ///     Only residue at the start of the answer: the text before a stray <c>&lt;/think&gt;</c> (Qwen3.5 9B, F19), or for a
    ///     thinking model a whole <c>&lt;think&gt;…&lt;/think&gt;</c> block that opens the answer (Qwen3.5 0.8B, V2). A block after
    ///     answer text is quoted markup and stays, tags included. The live stream showed it as the answer; the terminal event
    ///     replaces it. Text that would leave no answer stays the answer, tags dropped. For a model that cannot think, a close
    ///     tag after its own open tag is quoted markup and stays. Returns whether answer text remains, or null.
    /// </remarks>
    private static async Task<bool?> ReclassifyLeakedThinkTextAsync(StreamTransport transport, StreamState stream, int contentStart, bool thinkingModel)
    {
        const string OpenTag = "<think>";
        const string CloseTag = "</think>";
        var round = stream.ResponseBuilder.ToString(contentStart, stream.ResponseBuilder.Length - contentStart);
        var close = round.IndexOf(CloseTag, StringComparison.Ordinal);
        var open = close < 0 ? -1 : round.IndexOf(OpenTag, 0, close, StringComparison.Ordinal);
        if (close < 0 || (open >= 0 && (!thinkingModel || !string.IsNullOrWhiteSpace(round[..open]))))
        {
            return null;
        }

        var leaked = (open < 0 ? round[..close] : round[(open + OpenTag.Length)..close]).Trim();
        var answer = string.Concat(open < 0 ? string.Empty : round[..open], round[(close + CloseTag.Length)..].TrimStart()).TrimStart();

        // The only text of the round is the answer with a stray tag (LFM2.5 at effort none: "Okay.\n</think>"): drop the tags, keep the text.
        if (answer.Length == 0)
        {
            (answer, leaked) = (leaked, string.Empty);
        }

        var separator = stream.ReasoningBuilder.Length == 0 ? string.Empty : "\n\n";
        var reasoningSuffix = leaked.Length == 0 ? string.Empty : string.Concat(separator, leaked);
        var content = string.Concat(stream.ResponseBuilder.ToString(0, contentStart), answer);

        stream.ResponseBuilder.Clear().Append(content);
        stream.ReasoningBuilder.Append(reasoningSuffix);
        await transport.Dispatcher.ReportInvocationTextReclassifiedAsync(transport.InvocationId, content, reasoningSuffix);
        return answer.Length > 0;
    }

    /// <summary>The turn's open tool calls, mirrored onto the whole-turn deadline so tool time is not charged to the model.</summary>
    /// <remarks>
    ///     A round's calls all run before the next provider round, so provider text or reasoning after a round boundary (a result, or a segment start)
    ///     proves none is still running. Usage is not that proof: it closes the very round whose calls are about to run.
    /// </remarks>
    private sealed class OpenToolCalls
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly InvocationLifecycleTracker _tracker;
        private readonly Guid _invocationId;
        private bool _atRoundBoundary = true;

        public OpenToolCalls(InvocationLifecycleTracker tracker, Guid invocationId)
        {
            _tracker = tracker;
            _invocationId = invocationId;
        }

        public bool Any => _ids.Count > 0;

        public void Open(string callId)
        {
            _ = _ids.Add(callId);
            _atRoundBoundary = false;
            _tracker.SetToolExecuting(_invocationId, executing: true);
        }

        /// <summary>An approved call runs in the resume segment before the provider streams; an id-less one could never be closed.</summary>
        public void OpenApproved(ToolApprovalRequestContent approvalRequest)
        {
            if (!string.IsNullOrEmpty(approvalRequest.ToolCall.CallId))
            {
                Open(approvalRequest.ToolCall.CallId);
                _atRoundBoundary = true;
            }
        }

        public void Close(string callId)
        {
            _ = _ids.Remove(callId);
            // A round boundary even with ids still open: the framework runs a round's calls one at a time (AllowConcurrentInvocation is false).
            _atRoundBoundary = true;
            _tracker.SetToolExecuting(_invocationId, Any);
        }

        public void OnProviderOutput()
        {
            if (_atRoundBoundary && Any)
            {
                Clear();
            }
        }

        public void Clear()
        {
            _ids.Clear();
            _atRoundBoundary = true;
            _tracker.SetToolExecuting(_invocationId, executing: false);
        }
    }

    /// <summary>A local tool call seen requested on the stream but not yet resulted: its name plus the arguments as they arrived.</summary>
    /// <remarks>
    ///     Kept so a repeated call for the same id is detected and the result is attributed to its tool. Distinct from
    ///     <c>PendingToolCall</c>, which tracks a worker tool call's approval and result completions.
    /// </remarks>
    private readonly record struct RequestedToolCall(string Name, object? Arguments, string? SerializedArguments);

    /// <summary>The single-agent loop's state across segments and updates: what RunSingleAgentAsync held as locals before the split.</summary>
    private sealed class SingleAgentLoop
    {
        public required InvocationAgentContext AgentContext { get; init; }

        public required IReadOnlyList<string> ToolBudgetDefinitions { get; init; }

        public required ContextBudgetNoticeGate BudgetGate { get; init; }

        public required Dictionary<string, RequestedToolCall> PendingLocalToolCalls { get; init; }

        public required OpenToolCalls OpenToolCalls { get; init; }

        public required Dictionary<string, string> OpenSurrogateCallIds { get; init; }

        public required HashSet<string> UsedSurrogateNames { get; init; }

        public required Queue<SurrogateCall> PendingSurrogateResults { get; init; }

        public required int SurrogateCallCount { get; set; }

        public required HashSet<string> NotifiedDisabledTools { get; init; }

        // The tools the operator denied in this turn, withheld from its later provider rounds (WithholdDeniedTool).
        public HashSet<string> DeniedToolNames { get; } = new(StringComparer.Ordinal);

        public required List<ChatMessage> CurrentMessages { get; set; }

        public required List<ToolApprovalRequestContent> PendingApprovals { get; init; }

        public required HashSet<string> PendingApprovalKeys { get; init; }

        public required TimeSpan StreamIdleTimeout { get; init; }

        public required string StreamIdleTimeoutMessage { get; init; }

        public required bool IsFirstSegment { get; set; }

        public required bool RetainSegmentUpdates { get; init; }

        public required bool ReasoningToolCallReprompted { get; set; }

        public required bool RepromptPending { get; set; }

        // Per segment: read by the idle watchdog's isSuspended callback, so a closure over the old local now reads this property.
        public bool AwaitingRoundOutput { get; set; }

        public bool FinalRoundHasOutput { get; set; }

        public int FinalRoundContentStart { get; set; }

        public int FinalRoundReasoningStart { get; set; }

        // Per update: how much reasoning and whether the text were flushed ahead of a requested call in it.
        public int FlushedThinkingLength { get; set; }

        public bool TextFlushed { get; set; }
    }
}
