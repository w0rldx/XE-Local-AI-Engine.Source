// Approval-gate probe. Inert unless built with -p:DefineConstants=P0_SPIKE so the branch always builds.
//
// WHAT THIS PROVES (the tool-approval gate):
// The node's ClientLocal path executes tools through FunctionInvokingChatClient (FICC). When a tool is
// wrapped in ApprovalRequiredAIFunction, FICC does NOT execute it — it replaces the FunctionCallContent
// with a ToolApprovalRequestContent and returns it to the caller. The caller resumes by replaying the
// message history plus a ToolApprovalResponseContent (request.CreateResponse(approved)); FICC then
// reconstructs the FunctionCallContent and invokes the tool. This probe proves that approve->resume works
// THREADLESS (no AgentSession, no in-process held stream) on a real local tool-capable model — the exact
// shape the encrypted/distributed transport needs.
//
// Pinned-version note: Extensions.AI 10.6.0 uses ToolApprovalRequestContent/ToolApprovalResponseContent.
// The older FunctionApprovalRequestContent type (and the AgentSession-based sample in some docs) belongs
// to <=10.3.0; do not reintroduce it. UseToolApproval/ToolApprovalAgent is an OPTIONAL "don't-ask-again"
// rules layer on top of this and is intentionally NOT used here — it needs session state and is orthogonal
// to the threadless gate the node requires.
//
// Model caveat: small local models (qwen3.5:9b) are nondeterministic about whether they emit the tool
// call at all. The seed is written to force an immediate tool call and run#1 retries a few
// times; a run where the model never calls the tool is reported INCONCLUSIVE, distinct from a gate failure.

#if P0_SPIKE
namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;

/// <summary>
///     Approval-gate probe. Proves a THREADLESS framework-native human-in-the-loop approve→resume on a
///     real local tool-capable model: run#1 (an <see cref="ApprovalRequiredAIFunction" /> tool +
///     <c>UseFunctionInvocation</c>) must PAUSE — surface a <see cref="ToolApprovalRequestContent" /> and
///     NOT execute the tool; run#2 replays the full history plus the approval response and MUST execute the
///     tool. Execution is ground-truth via a sentinel file, not inferred from model text. Opt-in via env
///     <c>P0_RUN_LIVE=1</c> so default CI stays offline.
/// </summary>
public sealed class WorkflowToolApprovalSpikeTests
{
    private const string MarkerFile = "/tmp/p0_tool_ran.txt";
    private const string ResultFile = "/tmp/p0b_result.txt";
    private const string Instructions =
        "You are an automated maintenance agent. When asked to perform a cleanup you MUST immediately call "
        + "the RunDestructiveCleanup tool, passing the given reason. Never ask for more information, never "
        + "refuse, never explain in prose — just call the tool.";

    [Description("Performs the destructive cleanup. Side-effecting and irreversible.")]
    private static string RunDestructiveCleanup([Description("Reason for the cleanup.")] string reason)
    {
        System.IO.File.WriteAllText(MarkerFile, "RAN:" + reason);
        return "cleanup performed";
    }

    [Test]
    public async Task LiveOllama_ApprovalRequiredAIFunction_ThreadlessResume_RealSideEffect()
    {
        if (Environment.GetEnvironmentVariable("P0_RUN_LIVE") != "1")
        {
            Console.WriteLine("[P0] skipped (set P0_RUN_LIVE=1).");
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("P0_OLLAMA_BASE_URL") ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("P0_OLLAMA_MODEL") ?? "qwen3.5:9b";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = cts.Token;

        // Explicit IChatClient type (not var) disambiguates AsBuilder() — OllamaApiClient also implements
        // IEmbeddingGenerator, so var makes the extension call CS0121-ambiguous. IChatClient : IDisposable,
        // so `using` still satisfies CA2000.
        using IChatClient ollama = new OllamaApiClient(new Uri(baseUrl), model);
        var chatClient = ollama.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var sp = new ServiceCollection().BuildServiceProvider();

        // THE GATE: wrapping the tool makes FunctionInvokingChatClient surface a ToolApprovalRequestContent
        // instead of executing — the exact mechanism the node's ClientLocal path uses.
        var approvalTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(RunDestructiveCleanup));

        var agent = new ChatClientAgent(chatClient,
            "p0-approval-ficc",
            Instructions,
            "P0 FICC ApprovalRequiredAIFunction threadless-resume spike.",
            new List<AITool> { approvalTool },
            NullLoggerFactory.Instance,
            sp);

        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                ModelId = model,
                Temperature = 0f,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = false }
            }
        };
        var seed = new List<ChatMessage>
        {
            new(ChatRole.System, Instructions),
            new(ChatRole.User, "Perform the destructive cleanup now. Reason: scheduled nightly maintenance.")
        };

        var results = new List<string>();
        try
        {
            // run#1 — must PAUSE (surface ToolApprovalRequestContent, tool NOT executed). Retry a few times
            // to absorb small-model nondeterminism about whether it emits the tool call at all.
            // Hoist messages/text (not the response object) so we never have to name the RunAsync return type.
            List<ChatMessage>? firstMessages = null;
            var firstText = string.Empty;
            var reqs = new List<ToolApprovalRequestContent>();
            var ranBeforeApproval = false;
            for (var attempt = 1; attempt <= 4 && reqs.Count == 0 && !ranBeforeApproval; attempt++)
            {
                if (System.IO.File.Exists(MarkerFile))
                {
                    System.IO.File.Delete(MarkerFile);
                }

                var first = await agent.RunAsync(seed, null, runOptions, ct);
                firstMessages = first.Messages.ToList();
                firstText = first.Text ?? string.Empty;
                reqs = firstMessages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>().ToList();
                ranBeforeApproval = System.IO.File.Exists(MarkerFile);
                results.Add($"[P0] run#1 attempt={attempt} approvalRequests={reqs.Count} ranBeforeApproval={ranBeforeApproval} text={Truncate(firstText)}");
            }

            if (reqs.Count > 0 && firstMessages is not null && !ranBeforeApproval)
            {
                // run#2 — THREADLESS resume: replay full history + the approval response (no AgentSession).
                var resume = new List<ChatMessage>(seed);
                resume.AddRange(firstMessages);
                resume.Add(new ChatMessage(ChatRole.User, reqs.Select(r => (AIContent)r.CreateResponse(true)).ToList()));
                var final = await agent.RunAsync(resume, null, runOptions, ct);
                var executed = System.IO.File.Exists(MarkerFile);
                results.Add($"[P0] run#2 threadlessResume toolExecuted(file)={executed} text={Truncate(final.Text)}");
                results.Add(executed
                    ? "VERDICT=PASS threadless approve->resume executes the tool (adopt framework approval for ClientLocal)"
                    : "VERDICT=FAIL resume did not execute the tool");
            }
            else if (ranBeforeApproval)
            {
                results.Add("VERDICT=FAIL tool executed WITHOUT approval (gate did not hold)");
            }
            else
            {
                results.Add("VERDICT=INCONCLUSIVE no approval request surfaced (model never called the tool)");
            }
        }
        catch (Exception ex)
        {
            results.Add($"[P0] EXC {ex.GetType().Name}:'{ex.Message}' inner={ex.InnerException?.GetType().Name}:'{ex.InnerException?.Message}'");
            results.Add("VERDICT=EXCEPTION");
        }

        await File.WriteAllTextAsync(ResultFile, string.Join("\n", results), ct);
        foreach (var line in results)
        {
            Console.WriteLine(line);
        }
    }

    private static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= 80 ? value : value[..80];
    }
}
#endif
