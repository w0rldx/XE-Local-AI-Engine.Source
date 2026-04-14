namespace XE_Local_AI_Engine.AI.Agent.Chat;

using System.Runtime.CompilerServices;
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

            var serializedArguments = JsonSerializer.Serialize(functionCall.Arguments);
            activity?.SetTag("tool.arguments", serializedArguments);

            _logger.LogInformation("AgentRunToolInvoked {ToolName} {CallId} {Arguments}", functionCall.Name, functionCall.CallId, serializedArguments);
        }
    }
}
