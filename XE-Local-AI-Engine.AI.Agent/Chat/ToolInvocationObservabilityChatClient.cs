namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Sessions;

/// <summary>
///     Outermost agent-pipeline hop: pairs each <c>FunctionCallContent</c> with its <c>FunctionResultContent</c> and
///     emits a requested span/log and a completion span/log per tool call.
/// </summary>
/// <remarks>
///     Sits above <c>UseFunctionInvocation</c>, so a requested span measures call DISCOVERY, not run time. Neither span
///     sets <c>gen_ai.operation.name</c>: MEAI's function-invocation hop owns the conventional <c>execute_tool</c> span
///     for every call, and claiming it again would show a convention-aware backend two executions per call. The pair
///     correlates to it by <c>gen_ai.tool.call.id</c>. See docs/wiki/04-agent-mode.md ("The tool-call observation pair").
/// </remarks>
internal sealed class ToolInvocationObservabilityChatClient : DelegatingChatClient
{
    private readonly ILogger<ToolInvocationObservabilityChatClient> _logger;

    public ToolInvocationObservabilityChatClient(IChatClient innerClient, ILogger<ToolInvocationObservabilityChatClient> logger)
        : base(innerClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        // A completed response carries the whole function-calling turn in order, so pairing yields an outcome span per
        // call; its duration is near-zero (the tool already ran below this hop) but outcome/name/result-hash are exact.
        var pending = new Dictionary<string, RequestedCall>(StringComparer.Ordinal);
        var offered = OfferedToolNames(options);
        foreach (var message in response.Messages)
        {
            ObserveContents(message.Contents, pending, offered);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // Streaming is the real chat path: one logical call streams across many updates sharing a CallId, so one pending
        // entry per CallId keeps it to exactly one requested span and one completion span.
        var pending = new Dictionary<string, RequestedCall>(StringComparer.Ordinal);
        var offered = OfferedToolNames(options);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            ObserveContents(update.Contents, pending, offered);
            yield return update;
        }
    }

    /// <summary>
    ///     The names this request actually OFFERED the model, or null when it offered none. Built once per response
    ///     rather than per content, and used for one decision only: whether a requested name is a tool that exists.
    /// </summary>
    private static HashSet<string>? OfferedToolNames(ChatOptions? options) =>
        options?.Tools is { Count: > 0 } tools ? new HashSet<string>(tools.Select(static tool => tool.Name), StringComparer.Ordinal) : null;

    private void ObserveContents(IList<AIContent>? contents, Dictionary<string, RequestedCall> pending, HashSet<string>? offered)
    {
        if (contents is null || contents.Count == 0)
        {
            return;
        }

        foreach (var content in contents)
        {
            switch (content)
            {
                case FunctionCallContent functionCall:
                    ObserveRequested(functionCall, pending, offered);
                    break;
                case FunctionResultContent functionResult:
                    ObserveCompleted(functionResult, pending);
                    break;
            }
        }
    }

    private void ObserveRequested(FunctionCallContent functionCall, Dictionary<string, RequestedCall> pending, HashSet<string>? offered)
    {
        // A call's argument fragments stream under one CallId; record + span it exactly once. A repeat CallId is a
        // streamed fragment (or a duplicate in the completed message list), not a new call.
        if (!pending.TryAdd(functionCall.CallId, new RequestedCall(functionCall.Name, Stopwatch.GetTimestamp())))
        {
            return;
        }

        // The NAME reaches the budget only when it resolves against the tools this request offered, because that set is
        // durable; an unoffered call is still counted (null records the count alone), and span and log below keep it.
        var resolved = offered is not null && offered.Contains(functionCall.Name);
        ProviderCallBudget.Current?.RecordToolCallRequested(resolved ? functionCall.Name : null);

        // Names the model's REQUEST to call a tool, not the tool's execution — the delegate has not run yet, so this
        // span's duration measures call discovery. The paired ObserveCompleted span carries the outcome and latency.
        using var activity = AgentActivitySource.Instance.StartActivity("AgentRun.ToolCallRequested");
        activity?.SetTag("gen_ai.tool.call.id", functionCall.CallId);
        activity?.SetTag("gen_ai.tool.name", functionCall.Name);

        var (argumentsLength, argumentsHash) = SummarizePayload(functionCall.Arguments);
        activity?.SetTag("tool.arguments_length", argumentsLength);

        // Carries the redacted length+SHA-256-prefix digest, never the raw arguments. The convention defines this
        // attribute as the payload itself; the bend is recorded in docs/agent-knowledge.md §4 ("The four gen_ai.tool.*").
        activity?.SetTag("gen_ai.tool.call.arguments", argumentsHash);

        _logger.LogInformation("AgentRunToolInvoked {ToolName} {CallId} ArgsLength={ArgumentsLength} ArgsHash={ArgumentsHash}",
            functionCall.Name, functionCall.CallId, argumentsLength, argumentsHash);
    }

    private void ObserveCompleted(FunctionResultContent functionResult, Dictionary<string, RequestedCall> pending)
    {
        // Correlate the result back to the request we recorded. A result whose request never flowed through this hop
        // (only the tail was replayed) has nothing to time against — skip it rather than emit a duration-less span.
        if (!pending.Remove(functionResult.CallId, out var requested))
        {
            return;
        }

        var duration = Stopwatch.GetElapsedTime(requested.StartTimestamp);
        var durationMs = duration.TotalMilliseconds;
        var outcome = functionResult.Exception is not null ? "error" : "success";
        var (resultLength, resultHash) = SummarizePayload(functionResult.Result);
        ProviderCallBudget.Current?.RecordToolCallCompleted(duration, resultLength, functionResult.Exception is not null);

        // Fires when the FunctionResultContent flows back, so it records the real execution outcome and the
        // request-to-result latency. Length and hash only, never the raw result (docs/agent-knowledge.md §4).
        using var activity = AgentActivitySource.Instance.StartActivity("AgentRun.ToolCallCompleted");
        activity?.SetTag("gen_ai.tool.call.id", functionResult.CallId);
        activity?.SetTag("gen_ai.tool.name", requested.Name);
        activity?.SetTag("tool.outcome", outcome);
        activity?.SetTag("tool.duration_ms", durationMs);
        activity?.SetTag("tool.result_length", resultLength);

        // Carries the redacted length+SHA-256-prefix digest, never the raw result — the same deliberate bend recorded
        // in docs/agent-knowledge.md §4 ("The four gen_ai.tool.* attribute names track MEAI").
        activity?.SetTag("gen_ai.tool.call.result", resultHash);

        _logger.LogInformation("AgentRunToolCompleted {ToolName} {CallId} Outcome={Outcome} DurationMs={DurationMs} ResultLength={ResultLength} ResultHash={ResultHash}",
            requested.Name, functionResult.CallId, outcome, durationMs, resultLength, resultHash);
    }

    /// <summary>
    ///     Reduces a tool-call payload — arguments or result, potentially raw model-supplied PII or file contents — to
    ///     a safe correlation summary, never returning or logging the value itself.
    /// </summary>
    /// <remarks>
    ///     The summary is the serialized UTF-8 byte length plus a truncated SHA-256 hash prefix. A non-serializable
    ///     graph yields the sentinel length -1 with the <c>"unserializable"</c> marker rather than faulting the
    ///     response stream.
    /// </remarks>
    private static PayloadSummary SummarizePayload(object? value)
    {
        string serialized;
        try
        {
            serialized = JsonSerializer.Serialize(value);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Observability is best-effort: a non-serializable graph (e.g. a reference cycle) must never fault the
            // response stream. Report a sentinel length and a fixed marker instead of a real length/hash.
            return new PayloadSummary(-1, "unserializable");
        }

        var bytes = Encoding.UTF8.GetBytes(serialized);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        return new PayloadSummary(bytes.Length, hash[..12]);
    }

    // A requested tool call awaiting its result: the tool name (so the completion span need not re-read it) plus the
    // Stopwatch timestamp captured when the request was first observed.
    private readonly record struct RequestedCall(string Name, long StartTimestamp);

    // A tool-call payload reduced to a loggable summary: serialized UTF-8 byte length plus a truncated SHA-256 hash
    // prefix. Length is -1 when the payload could not be serialized.
    private readonly record struct PayloadSummary(int Length, string HashPrefix);
}
