// P0 approval-gate spike. Inert unless built with -p:DefineConstants=P0_SPIKE so the branch always
// builds (this session hit non-deterministic restore that intermittently dropped OllamaSharp /
// Microsoft.Extensions.AI.Abstractions refs). Run via /tmp/p0.sh. See memory 'agent-mode-foundation'.
#if P0_SPIKE
namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     P0 approval spike (GATE). Proves framework-native human-in-the-loop approval on a real local tool-capable
///     model: an <see cref="ApprovalRequiredAIFunction" /> pauses + surfaces a <c>FunctionApprovalRequestContent</c>
///     (no execution); then a <b>threadless</b> resume (no <see cref="AgentSession" />) executes the tool.
///     run#1 (pause) is proven; the resume reconstruction that FunctionInvokingChatClient accepts is what this
///     spike probes — it tries several message shapes in one run and writes the verdict to <c>/tmp/p0_result.txt</c>.
///     Opt-in via env <c>P0_RUN_LIVE=1</c> so default CI stays offline.
/// </summary>
public sealed class P0ApprovalSpikeTests
{
    private const string ApprovalToolEffectMarker = "DELETED::";
    private const string TargetPath = "/tmp/p0-spike-report.txt";
    private const string ResultFile = "/tmp/p0_result.txt";

    [Description("Permanently deletes a file from disk. Irreversible and destructive.")]
    private static string DangerousDelete([Description("Absolute path of the file to delete.")] string path)
        => $"{ApprovalToolEffectMarker}{path}";

    [Test]
    public async Task LiveOllama_ApprovalRequiredTool_ThreadlessResume_ProbeStrategies()
    {
        if (Environment.GetEnvironmentVariable("P0_RUN_LIVE") != "1")
        {
            Console.WriteLine("[P0] live gate skipped (set P0_RUN_LIVE=1 to run).");
            return;
        }

        var baseUrl = Environment.GetEnvironmentVariable("P0_OLLAMA_BASE_URL") ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("P0_OLLAMA_MODEL") ?? "qwen3.5:9b";
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var ct = cts.Token;

        IChatClient ollama = new OllamaApiClient(new Uri(baseUrl), model);
        IChatClient chatClient = ollama.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var (agent, runOptions) = BuildAgent(chatClient, model);

        var seed = new List<ChatMessage>
        {
            new(ChatRole.System,
                "You are a careful file assistant. To delete a file you MUST call the DangerousDelete tool with the absolute path. Do not claim a file is deleted unless the tool was called."),
            new(ChatRole.User, $"Please delete the file {TargetPath}.")
        };

        var results = new List<string>();

        // run#1 — must pause with an approval request (proven half of the gate).
        var first = await agent.RunAsync(seed, null, runOptions, ct);
        var approvals = ExtractApprovalRequests(first.Messages);
        results.Add($"run1: approvals={approvals.Count} toolExec={ContainsToolEffect(first.Messages, first.Text)} firstMsgs=[{DescribeMessages(first.Messages)}]");
        await File.WriteAllTextAsync(ResultFile, string.Join("\n", results), ct);

        AssertEx.True(approvals.Count > 0, "run#1 must surface a FunctionApprovalRequestContent");

        // Candidate threadless-resume message shapes (the run#1 assistant-approval-request-only shape throws
        // InvalidOperationException in FunctionInvokingChatClient; probe alternatives that expose the call id).
        var strategies = new (string Name, Func<List<ChatMessage>> Build)[]
        {
            ("S1_assistantApprovalReqOnly", () => new List<ChatMessage>(seed)
            {
                new(ChatRole.Assistant, approvals.Cast<AIContent>().ToList()),
                ApprovalResponse(approvals)
            }),
            ("S2_assistantFunctionCallOnly", () => new List<ChatMessage>(seed)
            {
                new(ChatRole.Assistant, approvals.Select(a => (AIContent)a.FunctionCall).ToList()),
                ApprovalResponse(approvals)
            }),
            ("S3_assistantCallPlusApprovalReq", () => new List<ChatMessage>(seed)
            {
                new(ChatRole.Assistant, approvals.SelectMany(a => new AIContent[] { a.FunctionCall, a }).ToList()),
                ApprovalResponse(approvals)
            }),
            ("S4_verbatimFirstMessages", () =>
            {
                var l = new List<ChatMessage>(seed);
                l.AddRange(first.Messages);
                l.Add(ApprovalResponse(approvals));
                return l;
            }),
            ("S5_approvalResponseOnlyAfterFirst", () =>
            {
                var l = new List<ChatMessage>(seed);
                l.AddRange(first.Messages);
                l.Add(ApprovalResponse(approvals));
                return l;
            })
        };

        string? winner = null;
        foreach (var (name, build) in strategies)
        {
            try
            {
                var resp = await agent.RunAsync(build(), null, runOptions, ct);
                var exec = ContainsToolEffect(resp.Messages, resp.Text);
                var pending = ExtractApprovalRequests(resp.Messages).Count;
                results.Add($"{name}: OK toolExec={exec} pendingApprovals={pending} text={Truncate(resp.Text)}");
                if (exec && winner is null)
                {
                    winner = name;
                }
            }
            catch (Exception ex)
            {
                results.Add($"{name}: EXC {ex.GetType().Name}:'{ex.Message}' inner={ex.InnerException?.GetType().Name}:'{ex.InnerException?.Message}'");
            }

            await File.WriteAllTextAsync(ResultFile, string.Join("\n", results), ct);
        }

        results.Add($"WINNER={winner ?? "<none>"}");
        await File.WriteAllTextAsync(ResultFile, string.Join("\n", results), ct);

        // NOTE: the in-process AgentSession baseline (agent.CreateSessionAsync) and the higher-level
        // ToolApprovalAgent / agent.UseToolApproval(...) message-based flow are the next things to probe
        // (see memory 'agent-mode-foundation') — UseToolApproval may make threadless resume work.
        AssertEx.NotNull(winner, $"no threadless resume strategy executed the tool; see {ResultFile}");
    }

    private static (ChatClientAgent Agent, ChatClientAgentRunOptions RunOptions) BuildAgent(IChatClient chatClient, string model)
    {
        var deleteTool = AIFunctionFactory.Create(DangerousDelete);
        AITool approvalTool = new ApprovalRequiredAIFunction(deleteTool);

        var agent = new ChatClientAgent(chatClient,
            "p0-approval-spike",
            "P0 approval spike agent.",
            "Proves framework-native approval + threadless resume.",
            new List<AITool> { approvalTool },
            NullLoggerFactory.Instance,
            new ServiceCollection().BuildServiceProvider());

        var runOptions = new ChatClientAgentRunOptions
        {
            ChatOptions = new ChatOptions
            {
                ModelId = model,
                AdditionalProperties = new AdditionalPropertiesDictionary { ["think"] = false }
            }
        };

        return (agent, runOptions);
    }

    private static List<FunctionApprovalRequestContent> ExtractApprovalRequests(IEnumerable<ChatMessage> messages)
        => messages.SelectMany(static m => m.Contents).OfType<FunctionApprovalRequestContent>().ToList();

    private static ChatMessage ApprovalResponse(IEnumerable<FunctionApprovalRequestContent> approvals)
        => new(ChatRole.User, approvals.Select(a => (AIContent)a.CreateResponse(true)).ToList());

    private static bool ContainsToolEffect(IEnumerable<ChatMessage> messages, string? text)
    {
        if (!string.IsNullOrEmpty(text) && text.Contains(ApprovalToolEffectMarker, StringComparison.Ordinal))
        {
            return true;
        }

        return messages.SelectMany(static m => m.Contents)
                       .OfType<FunctionResultContent>()
                       .Any(static r => r.Result?.ToString()?.Contains(ApprovalToolEffectMarker, StringComparison.Ordinal) == true);
    }

    private static string DescribeMessages(IEnumerable<ChatMessage> messages)
        => string.Join(" | ", messages.Select(m => $"{m.Role}:{string.Join(",", m.Contents.Select(c => c.GetType().Name))}"));

    private static string Truncate(string? value)
        => string.IsNullOrEmpty(value) ? string.Empty : value.Length <= 80 ? value : value[..80];
}
#endif
