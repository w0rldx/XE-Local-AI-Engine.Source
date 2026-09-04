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

        // A completed response carries the whole function-calling turn in order — the assistant's FunctionCallContent
        // then the tool's FunctionResultContent — so pairing them here yields an outcome span per call. (Non-streaming
        // durations are near-zero: the tool already executed below this hop before the response returned; the
        // outcome/name/result-hash are still accurate. Request-to-result latency comes from the streaming path.)
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
        // Streaming is the real chat path. A logical tool call streams across many updates (its argument fragments share
        // one CallId), the tool executes below this hop, then its FunctionResultContent update flows back. One pending
        // entry per CallId keeps
        // each call to exactly one requested span/log and one completion span. The interval is deliberately named
        // request-to-result latency: it can include remaining argument generation and middleware work before execution.
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

        // The NAME is recorded only when it resolves against the tools this request offered. A model can emit any
        // string here, and what the budget's name set feeds is durable: the persisted step detail, and from there a
        // work-session event detail and a node-run column. A name nothing offered would be recorded as a tool this run
        // reached for, which it is not — so the call is still counted (null records the count alone) and the identifier
        // is dropped. The span and the log below keep it, because an attempted call nobody offered is exactly the thing
        // an operator reading the trace needs to see.
        var resolved = offered is not null && offered.Contains(functionCall.Name);
        ProviderCallBudget.Current?.RecordToolCallRequested(resolved ? functionCall.Name : null);

        // Names the model's REQUEST to call a tool (a FunctionCallContent observed on the response), NOT the tool's
        // execution: this hop sits above UseFunctionInvocation, so the delegate has not run yet and this span's
        // duration measures call DISCOVERY, not run time. Named accordingly for honesty. The paired
        // ObserveCompleted span carries the execution outcome + request-to-result latency.
        using var activity = AgentActivitySource.Instance.StartActivity("AgentRun.ToolCallRequested");
        activity?.SetTag("tool.call_id", functionCall.CallId);
        activity?.SetTag("tool.name", functionCall.Name);

        var (argumentsLength, argumentsHash) = SummarizePayload(functionCall.Arguments);
        activity?.SetTag("tool.arguments_length", argumentsLength);
        activity?.SetTag("tool.arguments_hash", argumentsHash);

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

        // The completion span sits at the same hop but fires when the FunctionResultContent flows back, so it records
        // the actual execution outcome + request-to-result latency. Only the result length + hash are captured — never the raw result
        // value (docs/agent-knowledge.md §4: tool telemetry must never log raw arguments or results).
        using var activity = AgentActivitySource.Instance.StartActivity("AgentRun.ToolCallCompleted");
        activity?.SetTag("tool.call_id", functionResult.CallId);
        activity?.SetTag("tool.name", requested.Name);
        activity?.SetTag("tool.outcome", outcome);
        activity?.SetTag("tool.duration_ms", durationMs);
        activity?.SetTag("tool.result_length", resultLength);
        activity?.SetTag("tool.result_hash", resultHash);

        _logger.LogInformation("AgentRunToolCompleted {ToolName} {CallId} Outcome={Outcome} DurationMs={DurationMs} ResultLength={ResultLength} ResultHash={ResultHash}",
            requested.Name, functionResult.CallId, outcome, durationMs, resultLength, resultHash);
    }

    /// <summary>
    ///     Reduces a tool-call payload (arguments or result — potentially raw model-supplied PII/file contents) to a
    ///     safe correlation summary: the serialized UTF-8 byte length plus a truncated SHA-256 hash prefix. Never returns
    ///     or logs the value itself. A non-serializable graph yields the sentinel <c>Length = -1</c> with the
    ///     <c>"unserializable"</c> marker rather than faulting the response stream.
    /// </summary>
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
