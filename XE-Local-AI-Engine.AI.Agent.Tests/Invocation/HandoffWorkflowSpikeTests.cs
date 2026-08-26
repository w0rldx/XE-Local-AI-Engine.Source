// Handoff + workflow-approval probe for Microsoft.Agents.AI.Workflows 1.15.0. Fully deterministic — a scripted
// IChatClient stands in for the model (NO Ollama, NO network). Proves the exact API shapes the
// production handoff orchestration + IOrchestrationRunSession.RespondToApprovalAsync will copy:
//   (A) 2-agent handoff routing via AgentWorkflowBuilder.CreateHandoffBuilderWith + InProcessExecution.
//   (B) tool-approval pause/resume INSIDE a workflow run (surfacing event + resume mechanism).
//
// All findings are emitted to Console for the report; the test asserts the load-bearing facts.

// This probe intentionally uses underscore-rich test names (CA1707), instance helpers discovered by the test/runtime
// infrastructure (CA1822), direct awaits in test code (CA2007), explicit workflow-drain loops (S3267), broad catches
// that record unexpected workflow events before failing the assertion (CA1031), and MAF's experimental workflow API
// (MAAIW001). The file is compile-gated behind P0_SPIKE and each shape is part of the probe rather than production code.

#pragma warning disable CA1707, CA1822, CA2007, S3267, CA1031, MAAIW001
namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.Reflection;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Deterministic probe for the MAF 1.15.0 handoff workflow + in-workflow tool approval.
/// </summary>
/// <remarks>
///     <para>
///         This class was once quarantined as "flaky under parallel load". That diagnosis was wrong, and so was the
///         global <c>[NotInParallel]</c> that followed it. The real cause was a MAF 1.8.0 → 1.13.0 shape change:
///         <c>WorkflowOutputEvent.Data</c> stopped being a <c>List&lt;ChatMessage&gt;</c> (1.13.0 yields an
///         <c>AgentResponseUpdate</c>, or an <c>ExternalResponse</c> when the run resumed from an approval port), so
///         the old drain loop — which only read the workflow output — accumulated nothing and hit its break condition
///         before the specialist's streamed text ever arrived. The captured failure showed exactly that:
///         <c>WorkflowOutputEvent data type=AgentResponseUpdate</c> with an empty aggregated output. Nothing about it
///         was process-wide static-state pollution, and none of these probes touch shared mutable state — the chat
///         client is a per-test scripted fake and the workflow runtime is in-process and per-run.
///     </para>
///     <para>
///         Accumulating <c>AgentResponseUpdateEvent</c> / <c>AgentResponseEvent</c> text is therefore the entire fix.
///         An ablation over 5 runs per variant confirms it: new logic passes 5/5 with EITHER a keyed or a global
///         constraint, while the old logic fails with either. The constraint below is kept only to stop these
///         30s-bounded streaming drains from competing with each other for the test host, and it is KEYED
///         deliberately — TUnit's docs call the parameterless form "the most restrictive option" and recommend
///         constraint keys, because the keyless form serialises the whole assembly for no benefit here.
///     </para>
/// </remarks>
[NotInParallel(nameof(HandoffWorkflowSpikeTests))]
public sealed class HandoffWorkflowSpikeTests
{
    private const string TriageInstructions =
        "You are the TRIAGE agent. Hand off the conversation to the specialist.";

    private const string SpecialistInstructions =
        "You are the SPECIALIST agent. Answer the user's question directly.";

    private const string SpecialistAnswer = "SPECIALIST_ANSWER: the migration completed successfully.";

    /// <summary>
    ///     A) Handoff routing. Triage emits the handoff FunctionCallContent targeting the specialist's id;
    ///     specialist emits a plain text answer. Asserts control reaches the specialist and its text shows up
    ///     in the workflow output, and that conversation history carried across the hop.
    /// </summary>
    [Test]
    public async Task Handoff_TriageHandsOffToSpecialist_SpecialistAnswerReachesOutput()
    {
        // Reflect the FunctionPrefix const so the fake can target the handoff tool by name.
        var functionPrefix = ReadFunctionPrefix();
        Console.WriteLine($"[handoff][A] FunctionPrefix='{functionPrefix}'");

        using var fake = new HandoffScriptedChatClient(functionPrefix, SpecialistAnswer);
        var sp = new ServiceCollection().BuildServiceProvider();

        // NOTE: do NOT pre-wrap with UseFunctionInvocation. The handoff builder injects its own
        // handoff_to_<id> tools and intercepts the raw FunctionCallContent; an external FICC layer would
        // try to *invoke* handoff_to_<id> (no matching AIFunction) and swallow the handoff.
        var triage = new ChatClientAgent(fake,
            "triage",
            TriageInstructions,
            "Triage agent.",
            tools: null,
            NullLoggerFactory.Instance,
            sp);

        var specialist = new ChatClientAgent(fake,
            "specialist",
            SpecialistInstructions,
            "Specialist agent.",
            tools: null,
            NullLoggerFactory.Instance,
            sp);

        Console.WriteLine($"[handoff][A] triage.Id='{triage.Id}' specialist.Id='{specialist.Id}'");

        var workflow = AgentWorkflowBuilder
                       .CreateHandoffBuilderWith(triage)
                       .WithHandoff(triage, specialist, "Route domain questions to the specialist.")
                       .EmitAgentResponseEvents()
                       .Build();

        var input = new List<ChatMessage>
        {
            new(ChatRole.User, "Did the database migration complete?")
        };

        var run = await InProcessExecution.RunStreamingAsync(workflow, input, "p5-handoff-run", CancellationToken.None);

        // The HandoffStart executor (a ChatProtocolExecutor with AutoSendTurnToken=false) only ACCUMULATES
        // the List<ChatMessage> input; it takes its turn (and forwards to the initial agent) only on a
        // TurnToken. So we must enqueue a TurnToken to actually start the conversation.
        var accepted = await run.TrySendMessageAsync(new TurnToken(true));
        Console.WriteLine($"[handoff][A] TrySendMessageAsync(TurnToken) accepted={accepted}");

        // Everything the stream yielded, and — separately — only what the SPECIALIST executor emitted. The second
        // accumulator is what actually proves the handoff: text can reach `outputText` from the terminal workflow
        // output without ever having been produced by the specialist, which would let a broken route still pass.
        var outputText = new StringBuilder();
        var specialistText = new StringBuilder();

        // WatchStreamAsync only ends on RequestHaltEvent; in non-autonomous mode a handoff run goes IDLE
        // (awaiting the next user turn) after yielding output, so we bound the watch with a timeout and stop
        // once the specialist's answer has actually been observed in the output stream.
        var watchTimedOut = false;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var evt in run.WatchStreamAsync(timeout.Token))
            {
                Console.WriteLine($"[handoff][A] event={evt.GetType().Name} :: {Truncate(evt.ToString(), max: 200)}");
                switch (evt)
                {
                    case AgentResponseUpdateEvent updateEvent:
                        Console.WriteLine($"[handoff][A]   AgentResponseUpdateEvent.ExecutorId='{updateEvent.ExecutorId}' text='{Truncate(updateEvent.Update.Text)}'");
                        outputText.Append(updateEvent.Update.Text);
                        AppendIfSpecialist(specialistText, updateEvent.ExecutorId, specialist.Id, updateEvent.Update.Text);
                        break;
                    case AgentResponseEvent are:
                        Console.WriteLine($"[handoff][A]   AgentResponseEvent.ExecutorId='{are.ExecutorId}' text='{Truncate(are.Response.Text)}'");
                        outputText.Append(are.Response.Text);
                        AppendIfSpecialist(specialistText, are.ExecutorId, specialist.Id, are.Response.Text);
                        break;
                    case WorkflowOutputEvent woe:
                        // Kept, NOT dead code: the payload shape here is MAF-version dependent. Since 1.13.0 (and still
                        // at 1.15.0) it has
                        // been observed as AgentResponseUpdate and (after resuming an approval port) ExternalResponse,
                        // both of which fall through to the default branch; the List<ChatMessage> shape 1.8.0 yielded
                        // is still the documented terminal payload. It only ever feeds the loose `outputText` — the
                        // load-bearing assertion reads `specialistText`, which this cannot contribute to.
                        Console.WriteLine($"[handoff][A]   WorkflowOutputEvent data type={woe.Data?.GetType().Name}");
                        AppendChatMessages(outputText, woe.Data);
                        break;
                    case ExecutorFailedEvent fail:
                        Console.WriteLine($"[handoff][A]   ExecutorFailedEvent: {fail.Data?.GetType().Name}: {fail.Data?.Message}");
                        break;
                }

                if (specialistText.ToString().Contains("SPECIALIST_ANSWER", StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // On a regression the answer never arrives, the run idles, and the watch burns its full 30s. Swallowing
            // the cancellation here lets the assertions below report WHAT was missing instead of surfacing a bare
            // OperationCanceledException that says nothing about the handoff.
            watchTimedOut = true;
        }

        var allOutput = outputText.ToString();
        var specialistOutput = specialistText.ToString();
        Console.WriteLine($"[handoff][A] aggregated output: {Truncate(allOutput, max: 400)}");
        Console.WriteLine($"[handoff][A] specialist-attributed output: {Truncate(specialistOutput, max: 400)}");
        Console.WriteLine($"[handoff][A] specialistInvokedAtLeastOnce={fake.SpecialistInvocations} sawUserQuestionAtSpecialist={fake.SpecialistSawUserQuestion} watchTimedOut={watchTimedOut}");

        var diagnostics =
            $" [watchTimedOut={watchTimedOut} specialistInvocations={fake.SpecialistInvocations} allOutput='{Truncate(allOutput, max: 200)}' specialistOutput='{Truncate(specialistOutput, max: 200)}']";

        AssertEx.True(fake.SpecialistInvocations > 0, "specialist agent must be invoked after the handoff" + diagnostics);
        AssertEx.True(allOutput.Contains("SPECIALIST_ANSWER", StringComparison.Ordinal),
            "specialist's answer must reach the workflow output / agent-response stream" + diagnostics);
        AssertEx.True(specialistOutput.Contains("SPECIALIST_ANSWER", StringComparison.Ordinal),
            "the answer must be ATTRIBUTED to the specialist executor, not merely present in the stream" + diagnostics);
        AssertEx.True(fake.SpecialistSawUserQuestion,
            "conversation history (the original user question) must carry across the handoff hop" + diagnostics);
    }

    /// <summary>
    ///     B) Approval pause + resume inside a workflow. The (single) agent calls an
    ///     <see cref="ApprovalRequiredAIFunction" />-wrapped tool. Proves the run PAUSES (records HOW) and
    ///     RESUMES so the tool executes only when approved; rejection => never executes.
    /// </summary>
    [Test]
    public async Task Approval_InsideWorkflow_PausesAndResumes_ExecutesOnlyWhenApproved()
    {
        await RunApprovalScenario(true);
        await RunApprovalScenario(false);
    }

    private static async Task RunApprovalScenario(bool approve)
    {
        const string toolName = "destructive_cleanup";
        var executed = 0;
        var inner = AIFunctionFactory.Create((string reason) =>
            {
                executed++;
                return "cleanup performed: " + reason;
            },
            toolName,
            "Performs the destructive cleanup. Side-effecting and irreversible.");
        var approvalTool = new ApprovalRequiredAIFunction(inner);

        using var fake = new ApprovalScriptedChatClient(toolName);
        var sp = new ServiceCollection().BuildServiceProvider();

        var agent = new ChatClientAgent(fake.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build(),
            "approver",
            "Call the destructive_cleanup tool when asked to perform a cleanup.",
            "Approval agent.",
            new List<AITool>
            {
                approvalTool
            },
            NullLoggerFactory.Instance,
            sp);

        // Single-agent workflow (a handoff builder needs >=1 agent; we route nothing — just exercise the
        // agent inside the workflow runtime so approval surfacing through the workflow stream is observed).
        var workflow = AgentWorkflowBuilder
                       .CreateHandoffBuilderWith(agent)
                       .Build();

        var input = new List<ChatMessage>
        {
            new(ChatRole.User, "Perform the destructive cleanup now. Reason: nightly maintenance.")
        };

        var run = await InProcessExecution.RunStreamingAsync(workflow, input, $"p5-approval-{approve}", CancellationToken.None);

        // HandoffStart only accumulates the messages; a TurnToken triggers the agent turn (same as scenario A).
        var accepted = await run.TrySendMessageAsync(new TurnToken(true));
        Console.WriteLine($"[handoff][B approve={approve}] TrySendMessageAsync(TurnToken) accepted={accepted}");

        // Drain until the workflow surfaces an approval request (a RequestInfoEvent carrying a
        // ToolApprovalRequestContent) or the watch times out.
        RequestInfoEvent? approvalRequestEvent = null;
        ToolApprovalRequestContent? approvalContentFromAgentEvent = null;
        using (var pauseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await foreach (var evt in run.WatchStreamAsync(pauseTimeout.Token))
            {
                Console.WriteLine($"[handoff][B approve={approve}] event={evt.GetType().Name} :: {Truncate(evt.ToString(), max: 160)}");
                if (evt is RequestInfoEvent rie)
                {
                    Console.WriteLine(
                        $"[handoff][B approve={approve}]   RequestInfoEvent RequestId='{rie.Request.RequestId}' DataType={rie.Request.Data?.GetType().Name} portReqType={rie.Request.PortInfo.RequestType} portRespType={rie.Request.PortInfo.ResponseType}");
                    approvalRequestEvent = rie;
                    break;
                }

                if (evt is AgentResponseEvent are)
                {
                    var found = are.Response.Messages
                                   .SelectMany(m => m.Contents)
                                   .OfType<ToolApprovalRequestContent>()
                                   .FirstOrDefault();
                    if (found is not null)
                    {
                        Console.WriteLine($"[handoff][B approve={approve}]   approval content surfaced via AgentResponseEvent: {found.GetType().Name}");
                        approvalContentFromAgentEvent = found;
                    }
                }
            }
        }

        Console.WriteLine(
            $"[handoff][B approve={approve}] PAUSE: requestInfoEvent={approvalRequestEvent is not null} agentEventApproval={approvalContentFromAgentEvent is not null} executedBeforeResume={executed}");
        AssertEx.Equal(expected: 0, executed, "tool must NOT execute before approval is granted");
        AssertEx.True(approvalRequestEvent is not null, "workflow must pause by surfacing a RequestInfoEvent for the approval");

        // RESUME: build the ExternalResponse from the held request and send it back into the same run.
        var request = approvalRequestEvent!.Request;
        var response = BuildApprovalResponse(request, approve);
        await run.SendResponseAsync(response);

        // Drain the post-resume stream fully. The tool execution + the model's follow-up turn happen across
        // additional supersteps that stream AFTER SendResponseAsync; we keep consuming until the workflow
        // yields its terminal WorkflowOutputEvent (or idles out via the timeout) before checking the marker.
        var sawTerminalOutput = false;
        using (var resumeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await foreach (var evt in run.WatchStreamAsync(resumeTimeout.Token))
                {
                    Console.WriteLine($"[handoff][B approve={approve}] (resume) event={evt.GetType().Name} :: {Truncate(evt.ToString(), max: 160)}");
                    if (evt is ExecutorFailedEvent fail)
                    {
                        Console.WriteLine($"[handoff][B approve={approve}]   (resume) ExecutorFailedEvent {fail.Data?.GetType().Name}: {fail.Data?.Message}");
                    }

                    if (evt is WorkflowOutputEvent)
                    {
                        sawTerminalOutput = true;
                    }

                    // Stop once the terminal output is observed and the sentinel file reflects the expected state, so
                    // the approved path waits for the tool to actually run before we leave the loop.
                    if (sawTerminalOutput && (executed > 0 || !approve))
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[handoff][B approve={approve}] (resume) watch timed out (workflow went idle)");
            }
        }

        Console.WriteLine($"[handoff][B approve={approve}] AFTER RESUME executed={executed} sawTerminalOutput={sawTerminalOutput}");
        if (approve)
        {
            AssertEx.Equal(expected: 1, executed, "approved tool must execute exactly once after resume");
        }
        else
        {
            AssertEx.Equal(expected: 0, executed, "rejected tool must never execute");
        }
    }

    /// <summary>
    ///     C) Combined: a single triage agent that has BOTH an outgoing handoff edge AND its own
    ///     <see cref="ApprovalRequiredAIFunction" />-wrapped tool. Proves the exact production recipe:
    ///     do NOT externally wrap with UseFunctionInvocation — <see cref="ChatClientAgent" /> auto-inserts FICC
    ///     via <c>WithDefaultAgentMiddleware</c> if the client doesn't already have one. The injected FICC
    ///     handles the agent's own tools (sets them as AdditionalTools) while letting the handoff
    ///     <c>handoff_to_*</c> FunctionCallContent flow through unserviced (it's an AIFunctionDeclaration,
    ///     not an AIFunction — FICC never tries to invoke it). The executor's CollectHandoffRequestsFilter
    ///     catches it downstream. Both paths (approval pause/resume AND handoff routing) work on the same agent.
    /// </summary>
    [Test]
    public async Task Combined_TriageWithOwnApprovalTool_BothApprovalAndHandoffWork()
    {
        await RunCombinedScenario(true);
        await RunCombinedScenario(false);
    }

    private static async Task RunCombinedScenario(bool approveFirst)
    {
        const string ownToolName = "lookup_customer";
        var lookupExecuted = 0;
        var lookupReason = string.Empty;
        var lookupTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string customerId) =>
            {
                lookupExecuted++;
                lookupReason = customerId;
                return "customer_data: premium tier";
            },
            ownToolName,
            "Looks up customer data. Requires approval because it accesses PII."));

        // RECIPE: do NOT externally wrap with UseFunctionInvocation.
        // ChatClientAgent.ctor -> WithDefaultAgentMiddleware -> auto-inserts FunctionInvokingChatClient
        //   (because the raw bare IChatClient has no FICC yet) and sets FICC.AdditionalTools = [lookupTool].
        // HandoffAgentExecutor merges handoff_to_1 into ChatOptions.Tools at run time.
        // FICC sees: AdditionalTools=[ApprovalRequiredAIFunction(lookup_customer)]
        //             ChatOptions.Tools=[handoff_to_1 (AIFunctionDeclaration, no impl)]
        // -> lookup_customer call -> ToolApprovalRequestContent -> _userInputHandler -> RequestInfoEvent.
        // -> handoff_to_1 call -> FICC lets it through (no matching AIFunction) -> executor catches it.
        using var fake = new CombinedScriptedChatClient(ownToolName, SpecialistAnswer);
        var sp = new ServiceCollection().BuildServiceProvider();

        var triage = new ChatClientAgent(fake, // bare IChatClient — FICC auto-inserted by WithDefaultAgentMiddleware
            "triage",
            TriageInstructions,
            "Triage agent that can look up customer data before handing off.",
            new List<AITool>
            {
                lookupTool
            }, // own tool — set as AdditionalTools on the auto-FICC
            NullLoggerFactory.Instance,
            sp);

        var specialist = new ChatClientAgent(fake,
            "specialist",
            SpecialistInstructions,
            "Specialist agent.",
            tools: null,
            NullLoggerFactory.Instance,
            sp);

        Console.WriteLine($"[handoff][C approveFirst={approveFirst}] triage.Id='{triage.Id}' specialist.Id='{specialist.Id}'");

        var workflow = AgentWorkflowBuilder
                       .CreateHandoffBuilderWith(triage)
                       .WithHandoff(triage, specialist, "Route after customer lookup.")
                       .EmitAgentResponseEvents()
                       .Build();

        var input = new List<ChatMessage>
        {
            new(ChatRole.User, "Look up customer C-42, then hand off to specialist.")
        };

        var run = await InProcessExecution.RunStreamingAsync(workflow, input, $"p5-combined-{approveFirst}", CancellationToken.None);
        var accepted = await run.TrySendMessageAsync(new TurnToken(true));
        Console.WriteLine($"[handoff][C approveFirst={approveFirst}] TurnToken accepted={accepted}");

        // --- Step 1: drain until approval RequestInfoEvent appears ---
        RequestInfoEvent? approvalEvent = null;
        using (var ph1Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            await foreach (var evt in run.WatchStreamAsync(ph1Cts.Token))
            {
                Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph1 event={evt.GetType().Name} :: {Truncate(evt.ToString(), max: 160)}");
                if (evt is RequestInfoEvent rie && rie.Request.PortInfo.RequestType.ToString().Contains("ToolApprovalRequestContent", StringComparison.Ordinal))
                {
                    Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph1 APPROVAL PAUSED RequestId='{rie.Request.RequestId}'");
                    approvalEvent = rie;
                    break;
                }

                if (evt is ExecutorFailedEvent fail)
                {
                    Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph1 FAILED: {fail.Data?.Message}");
                }
            }
        }

        Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph1 done: approvalEvent={approvalEvent is not null} lookupExecuted={lookupExecuted}");
        AssertEx.Equal(expected: 0, lookupExecuted, "C: lookup tool must NOT execute before approval");
        AssertEx.True(approvalEvent is not null, "C: workflow must pause with RequestInfoEvent for approval-required own tool");

        // --- Step 2: send approval/rejection, drain until handoff completes or tool-not-executed confirmed ---
        var approvalResponse = BuildApprovalResponse(approvalEvent!.Request, approveFirst);
        await run.SendResponseAsync(approvalResponse);
        Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph2 approval sent (approve={approveFirst})");

        var sawHandoffToSpecialist = false;
        var sawTerminalOutput = false;
        using (var ph2Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await foreach (var evt in run.WatchStreamAsync(ph2Cts.Token))
                {
                    Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph2 event={evt.GetType().Name} :: {Truncate(evt.ToString(), max: 180)}");
                    if (evt is AgentResponseEvent are)
                    {
                        Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph2 AgentResponseEvent executorId='{are.ExecutorId}' text='{Truncate(are.Response.Text)}'");
                        if (are.Response.Text?.Contains("SPECIALIST_ANSWER", StringComparison.Ordinal) ?? false)
                        {
                            sawHandoffToSpecialist = true;
                        }
                    }

                    if (evt is WorkflowOutputEvent)
                    {
                        sawTerminalOutput = true;
                    }

                    if (evt is ExecutorFailedEvent fail2)
                    {
                        Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph2 FAILED: {fail2.Data?.Message}");
                    }

                    // Exit when we have observed the specialist's answer (handoff happened)
                    // and we know the lookup tool state is settled.
                    if (fake.SpecialistInvocations > 0 && (lookupExecuted > 0 || !approveFirst))
                    {
                        break;
                    }

                    // Also stop if terminal output arrived and specialist already ran.
                    if (sawTerminalOutput && fake.SpecialistInvocations > 0)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine($"[handoff][C approveFirst={approveFirst}] ph2 watch timed out (workflow went idle)");
            }
        }

        Console.WriteLine(
            $"[handoff][C approveFirst={approveFirst}] FINAL lookupExecuted={lookupExecuted} specialistInvocations={fake.SpecialistInvocations} sawHandoff={sawHandoffToSpecialist} sawTerminalOutput={sawTerminalOutput}");

        if (approveFirst)
        {
            AssertEx.Equal(expected: 1, lookupExecuted, "C approve: lookup tool must execute exactly once after approval");
            AssertEx.Equal("C-42", lookupReason, "C approve: lookup tool must receive the scripted argument");
        }
        else
        {
            AssertEx.Equal(expected: 0, lookupExecuted, "C reject: lookup tool must never execute when rejected");
        }

        AssertEx.True(fake.SpecialistInvocations > 0, "C: triage must hand off to specialist in both approve and reject paths");
    }

    private static ExternalResponse BuildApprovalResponse(ExternalRequest request, bool approve)
    {
        // The request Data is (expected to be) the ToolApprovalRequestContent; build its response and wrap.
        var data = request.Data?.AsType(typeof(object));
        Console.WriteLine($"[handoff][B] request.Data.AsType -> {data?.GetType().FullName}");
        if (data is ToolApprovalRequestContent approvalReq)
        {
            return request.CreateResponse(approvalReq.CreateResponse(approve));
        }

        // Fallback: send the bool directly if the port expects a bare approval flag.
        return request.CreateResponse(approve);
    }

    /// <summary>
    ///     Accumulates <paramref name="text" /> only when the emitting executor IS the specialist agent.
    ///     <para>
    ///         The original probe asserted <c>ExecutorId == specialist.Id</c>. That equality no longer holds under MAF
    ///         1.13.0 (still true at 1.15.0), which is part of the same shape change that broke the drain loop: the handoff builder now names
    ///         its executors after the agent's sanitized INSTRUCTIONS with the agent id appended, e.g.
    ///         <c>You_are_the_SPECIALIST_agent_Answer_the_user_s_question_directly_923ee58cc43c…</c> for an agent whose
    ///         <c>Id</c> is <c>923ee58cc43c…</c>. Matching on the id suffix restores the attribution check the equality
    ///         used to give while staying honest about the executor-id format the framework actually produces.
    ///     </para>
    /// </summary>
    private static void AppendIfSpecialist(StringBuilder sb, string? executorId, string specialistId, string? text)
    {
        if (!string.IsNullOrEmpty(text) && (executorId?.EndsWith(specialistId, StringComparison.Ordinal) ?? false))
        {
            sb.Append(text);
        }
    }

    private static void AppendChatMessages(StringBuilder sb, object? data)
    {
        switch (data)
        {
            case IEnumerable<ChatMessage> msgs:
                foreach (var m in msgs)
                {
                    sb.AppendLine(m.Text);
                }

                break;
            case ChatMessage one:
                sb.AppendLine(one.Text);
                break;
            case string s:
                sb.AppendLine(s);
                break;
            default:
                sb.AppendLine(data?.ToString());
                break;
        }
    }

    private static string ReadFunctionPrefix()
    {
        var coreType = typeof(AgentWorkflowBuilder).Assembly
                                                   .GetType("Microsoft.Agents.AI.Workflows.HandoffWorkflowBuilderCore`1")!;
        var field = coreType.GetField("FunctionPrefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?? coreType.GetField("FunctionPrefix", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var value = field?.GetRawConstantValue() ?? field?.GetValue(null);
        return value as string ?? "<unknown>";
    }

    private static string Truncate(string? value, int max = 120)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max];
    }

    /// <summary>
    ///     Scripted model for the handoff scenario. Distinguishes triage vs specialist by the SYSTEM message
    ///     (instructions) present in the incoming messages: triage sees its instructions and emits the handoff
    ///     tool call; specialist sees its instructions and emits a plain answer.
    /// </summary>
    private sealed class HandoffScriptedChatClient : IChatClient
    {
        private readonly string _functionPrefix;
        private readonly string _specialistAnswer;

        public HandoffScriptedChatClient(string functionPrefix, string specialistAnswer)
        {
            _functionPrefix = functionPrefix;
            _specialistAnswer = specialistAnswer;
        }

        public int SpecialistInvocations { get; private set; }

        public bool SpecialistSawUserQuestion { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (response, _) = Build(messages.ToList(), options, streaming: false);
            return Task.FromResult(response!);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (_, update) = Build(messages.ToList(), options, streaming: true);
            return Single(update!);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private (ChatResponse? Response, ChatResponseUpdate? Update) Build(List<ChatMessage> list, ChatOptions? options, bool streaming)
        {
            // DISCRIMINATOR: the handoff builder injects the `handoff_to_<n>` tool declarations ONLY into the
            // agent that has outgoing handoffs (here, triage), via options.Tools. The specialist receives no
            // such tool. So presence of a handoff_to_* tool in options.Tools => this is the triage turn.
            var handoffTool = options?.Tools?
                .FirstOrDefault(t => t.Name.StartsWith(_functionPrefix, StringComparison.Ordinal));
            var toolNames = options?.Tools is null ? "<none>" : string.Join(",", options.Tools.Select(t => t.Name));
            Console.WriteLine($"[handoff][A][fake] invoked streaming={streaming} msgCount={list.Count} roles=[{string.Join("|", list.Select(m => m.Role.Value))}] optionTools=[{toolNames}]");
            foreach (var m in list)
            {
                Console.WriteLine($"[handoff][A][fake]   {m.Role.Value}: {Truncate(m.Text, max: 90)} contents=[{string.Join(",", m.Contents.Select(c => c.GetType().Name))}]");
            }

            if (handoffTool is null)
            {
                // SPECIALIST turn: no handoff tool offered -> answer in plain text.
                SpecialistInvocations++;
                SpecialistSawUserQuestion = list.Any(m =>
                    m.Text?.Contains("database migration", StringComparison.Ordinal) ?? false);
                Console.WriteLine($"[handoff][A][fake]   -> SPECIALIST answer (invocation #{SpecialistInvocations}, sawUserQuestion={SpecialistSawUserQuestion})");
                return streaming
                    ? (null, new ChatResponseUpdate(ChatRole.Assistant, _specialistAnswer))
                    : (new ChatResponse(new ChatMessage(ChatRole.Assistant, _specialistAnswer)), null);
            }

            // TRIAGE turn: emit the framework-injected handoff tool call by its REAL name (handoff_to_<n>).
            var handoffName = handoffTool.Name;
            Console.WriteLine($"[handoff][A][fake]   -> TRIAGE handoff call '{handoffName}'");
            var call = new FunctionCallContent($"call-{handoffName}", handoffName, new Dictionary<string, object?>());
            return streaming
                ? (null, new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                {
                    call
                }))
                : (new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    call
                })), null);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Single(ChatResponseUpdate update)
        {
            yield return update;
            await Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Scripted model for the approval scenario. Emits one FunctionCallContent for the approval-required
    ///     tool until a FunctionResultContent appears in the history, then a plain final message.
    /// </summary>
    private sealed class ApprovalScriptedChatClient : IChatClient
    {
        private readonly string _toolName;

        public ApprovalScriptedChatClient(string toolName)
        {
            _toolName = toolName;
        }

        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(BuildResponse(messages));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var resp = BuildResponse(messages);
            return ToUpdates(resp);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private ChatResponse BuildResponse(IEnumerable<ChatMessage> messages)
        {
            CallCount++;
            var list = messages.ToList();
            Console.WriteLine($"[handoff][B][fake] call#{CallCount} msgCount={list.Count} roles=[{string.Join("|", list.Select(m => m.Role.Value))}]");
            foreach (var m in list)
            {
                Console.WriteLine($"[handoff][B][fake]   {m.Role.Value}: '{Truncate(m.Text, max: 70)}' contents=[{string.Join(",", m.Contents.Select(c => c.GetType().Name))}]");
            }

            var toolHasRun = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (toolHasRun)
            {
                Console.WriteLine("[handoff][B][fake]   -> tool result present, returning final text");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "cleanup complete"));
            }

            Console.WriteLine($"[handoff][B][fake]   -> emitting tool call '{_toolName}'");
            var call = new FunctionCallContent($"call-{_toolName}", _toolName, new Dictionary<string, object?>
            {
                ["reason"] = "nightly maintenance"
            });
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                call
            }));
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> ToUpdates(ChatResponse response)
        {
            foreach (var msg in response.Messages)
            {
                yield return new ChatResponseUpdate(msg.Role, msg.Contents);
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    ///     Scripted model for the COMBINED scenario (C). The triage agent has both an own
    ///     <see cref="ApprovalRequiredAIFunction" /> and an outgoing <c>handoff_to_*</c> edge.
    ///     <para>
    ///         Phase discrimination (triage turns only — specialist is identified by absence of handoff tool):
    ///         <list type="bullet">
    ///             <item>Call 1 (no FunctionResultContent in history): emit own-tool call.</item>
    ///             <item>Call 2 (FunctionResultContent present): emit the handoff_to_1 call.</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         FICC (auto-inserted by <c>WithDefaultAgentMiddleware</c>) handles the own-tool call:
    ///         approval-required → <see cref="ToolApprovalRequestContent" /> → RequestInfoEvent.
    ///         After resume FICC calls back with the result in the message history (call 2).
    ///         The handoff_to_* call is an <see cref="AIFunctionDeclaration" /> with no implementation —
    ///         FICC lets it through unserviced; the executor's CollectHandoffRequestsFilter catches it.
    ///     </para>
    /// </summary>
    private sealed class CombinedScriptedChatClient : IChatClient
    {
        private readonly string _ownToolName;
        private readonly string _specialistAnswer;

        public CombinedScriptedChatClient(string ownToolName, string specialistAnswer)
        {
            _ownToolName = ownToolName;
            _specialistAnswer = specialistAnswer;
        }

        public int SpecialistInvocations { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (resp, _) = Build(messages.ToList(), options, streaming: false);
            return Task.FromResult(resp!);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var (_, upd) = Build(messages.ToList(), options, streaming: true);
            return Single(upd!);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private (ChatResponse? Response, ChatResponseUpdate? Update) Build(List<ChatMessage> list, ChatOptions? options, bool streaming)
        {
            // DISCRIMINATOR: triage has handoff_to_* in options.Tools; specialist does not.
            var handoffTool = options?.Tools?.FirstOrDefault(t => t.Name.StartsWith("handoff_to_", StringComparison.Ordinal));
            var toolNames = options?.Tools is null ? "<none>" : string.Join(",", options.Tools.Select(t => t.Name));
            Console.WriteLine($"[handoff][C][fake] invoked streaming={streaming} msgCount={list.Count} roles=[{string.Join("|", list.Select(m => m.Role.Value))}] optionTools=[{toolNames}]");
            foreach (var m in list)
            {
                Console.WriteLine($"[handoff][C][fake]   {m.Role.Value}: '{Truncate(m.Text, max: 80)}' contents=[{string.Join(",", m.Contents.Select(c => c.GetType().Name))}]");
            }

            if (handoffTool is null)
            {
                // SPECIALIST turn.
                SpecialistInvocations++;
                Console.WriteLine($"[handoff][C][fake]   -> SPECIALIST answer (#{SpecialistInvocations})");
                return streaming
                    ? (null, new ChatResponseUpdate(ChatRole.Assistant, _specialistAnswer))
                    : (new ChatResponse(new ChatMessage(ChatRole.Assistant, _specialistAnswer)), null);
            }

            // TRIAGE turn. Check whether a FunctionResultContent is already present (i.e. FICC already
            // executed/rejected the own tool and is calling back for the next model turn).
            var hasOwnToolResult = list.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Any();
            if (hasOwnToolResult)
            {
                // Step 2: own-tool result is in history → now emit the handoff call.
                var handoffName = handoffTool.Name;
                Console.WriteLine($"[handoff][C][fake]   -> TRIAGE phase2 handoff call '{handoffName}'");
                var handoffCall = new FunctionCallContent($"call-{handoffName}", handoffName, new Dictionary<string, object?>());
                return streaming
                    ? (null, new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                    {
                        handoffCall
                    }))
                    : (new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                    {
                        handoffCall
                    })), null);
            }

            // Step 1: no result yet → emit the own-tool call (approval-required, will be intercepted by FICC).
            Console.WriteLine($"[handoff][C][fake]   -> TRIAGE phase1 own-tool call '{_ownToolName}'");
            var ownCall = new FunctionCallContent($"call-{_ownToolName}", _ownToolName, new Dictionary<string, object?>
            {
                ["customerId"] = "C-42"
            });
            return streaming
                ? (null, new ChatResponseUpdate(ChatRole.Assistant, new List<AIContent>
                {
                    ownCall
                }))
                : (new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    ownCall
                })), null);
        }

        private static async IAsyncEnumerable<ChatResponseUpdate> Single(ChatResponseUpdate update)
        {
            yield return update;
            await Task.CompletedTask;
        }
    }
}
#pragma warning restore CA1707, CA1822, CA2007, S3267, CA1031, MAAIW001
