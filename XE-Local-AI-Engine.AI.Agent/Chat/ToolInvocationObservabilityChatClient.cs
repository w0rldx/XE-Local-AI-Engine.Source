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
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
        {
            LogToolInvocations(update.Contents);
            yield return update;
        }
    }

    private void LogToolInvocations(IList<AIContent>? contents)
    {
        if (contents is null || contents.Count == 0)
        {
            return;
        }

        foreach (var functionCall in contents.OfType<FunctionCallContent>())
        {
            using var activity = AgentActivitySource.Instance.StartActivity("AgentRun.ToolInvocation");
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
