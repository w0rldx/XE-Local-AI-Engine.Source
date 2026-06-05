namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Cancellation propagates from an agent run into an in-flight client-local
///     tool. The runner cancels by cancelling the token it threads into <c>RunStreamingAsync</c>; this test drives a
///     <see cref="ChatClientAgent" /> with a blocking <see cref="MetadataToolFunction" /> (the same bridge type that
///     backs <c>run_in_agent_home</c>) and a scripted model, then cancels the run token mid-tool and asserts the
///     handler observed the cancellation. Deterministic — a scripted <see cref="IChatClient" />, no Ollama.
/// </summary>
public sealed class ClientLocalToolCancellationTests
{
    private const string ToolName = "run_blocking_client_local";
    private const string Schema = """{"type":"object"}""";

    [Test]
    public async Task RunCancellation_PropagatesToInFlightClientLocalTool()
    {
        var toolStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancel = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var tool = new MetadataToolFunction(
            ToolName,
            "Blocks until cancelled.",
            MetadataToolFunction.ParseSchema(Schema),
            async (_, cancellationToken) =>
            {
                toolStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    observedCancel.TrySetResult(true);
                    throw;
                }

                return "done";
            });

        using var scripted = new ScriptedToolCallChatClient(ToolName);
        var chatClient = scripted.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var agent = new ChatClientAgent(
            chatClient,
            "cancel-propagation",
            "Call the tool when asked.",
            "Cancellation propagation guard.",
            new List<AITool> { tool },
            NullLoggerFactory.Instance,
            serviceProvider);

        var seed = new List<ChatMessage>
        {
            new(ChatRole.System, "Call the tool when asked."),
            new(ChatRole.User, "Run it.")
        };

        using var cancellation = new CancellationTokenSource();
        var runTask = agent.RunAsync(seed, null, null, cancellation.Token);

        await toolStarted.Task;
        await cancellation.CancelAsync();

        await AssertEx.ThrowsAsync<OperationCanceledException>(() => runTask);
        AssertEx.True(await observedCancel.Task, "the in-flight client-local tool must observe the run cancellation");
    }

    private sealed class ScriptedToolCallChatClient : IChatClient
    {
        private readonly string _toolName;

        public ScriptedToolCallChatClient(string toolName)
        {
            _toolName = toolName ?? throw new ArgumentNullException(nameof(toolName));
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var toolHasRun = messages.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
            if (toolHasRun)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "done")));
            }

            var call = new FunctionCallContent($"call-{_toolName}", _toolName, new Dictionary<string, object?>());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent> { call })));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("This guard exercises the non-streaming RunAsync path only.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }
}
