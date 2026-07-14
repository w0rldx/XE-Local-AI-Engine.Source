namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
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

        foreach (var message in response.Messages)
        {
            LogToolInvocations(message.Contents);
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        // One logical tool call streams across many updates (its argument fragments share a single CallId). Track the
        // CallIds already logged for THIS streaming request so each call yields exactly one log line and one span,
        // instead of a near-zero-duration span and a repeated log per streamed fragment.
        var loggedCallIds = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            LogToolInvocations(update.Contents, loggedCallIds);
            yield return update;
        }
    }

    private void LogToolInvocations(IList<AIContent>? contents, HashSet<string>? loggedCallIds = null)
    {
        if (contents is null || contents.Count == 0)
        {
            return;
        }

        foreach (var functionCall in contents.OfType<FunctionCallContent>())
        {
            // Streaming path only: skip a CallId already logged for this request so one call is not spanned per
            // fragment. The non-streaming path passes no set (each call appears once in a complete response).
            if (loggedCallIds is not null && !loggedCallIds.Add(functionCall.CallId))
            {
                continue;
            }

            // Names the model's REQUEST to call a tool (a FunctionCallContent observed on the response), NOT the tool's
            // execution: this hop sits above UseFunctionInvocation, so the delegate has not run yet and this span's
            // duration measures call DISCOVERY, not run time. Named accordingly for honesty (MED-007).
            using var activity = AgentActivitySource.Instance.StartActivity("AgentRun.ToolCallRequested");
            activity?.SetTag("tool.call_id", functionCall.CallId);
            activity?.SetTag("tool.name", functionCall.Name);

            var (argumentsLength, argumentsHash) = SummarizeArguments(functionCall.Arguments);
            activity?.SetTag("tool.arguments_length", argumentsLength);
            activity?.SetTag("tool.arguments_hash", argumentsHash);

            _logger.LogInformation("AgentRunToolInvoked {ToolName} {CallId} ArgsLength={ArgumentsLength} ArgsHash={ArgumentsHash}",
                functionCall.Name, functionCall.CallId, argumentsLength, argumentsHash);
        }
    }

    /// <summary>
    ///     Reduces tool-call arguments (potentially raw model-supplied PII/file contents) to a safe correlation
    ///     summary: the serialized UTF-8 byte length plus a truncated SHA-256 hash prefix. Never returns or logs the
    ///     argument values themselves. A non-serializable argument graph yields the sentinel <c>(-1,
    ///     "unserializable")</c> rather than faulting the response stream.
    /// </summary>
    private static (int Length, string HashPrefix) SummarizeArguments(IDictionary<string, object?>? arguments)
    {
        string serializedArguments;
        try
        {
            serializedArguments = JsonSerializer.Serialize(arguments);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            // Observability is best-effort: a non-serializable argument graph (e.g. a reference cycle) must never fault
            // the response stream. Report a sentinel length and a fixed marker instead of a real length/hash.
            return (-1, "unserializable");
        }

        var bytes = Encoding.UTF8.GetBytes(serializedArguments);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));

        return (bytes.Length, hash[..12]);
    }
}
