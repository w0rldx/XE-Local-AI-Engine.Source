namespace XE_Local_AI_Engine.AI.Agent.Tests.Eval;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Eval.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Deterministic CI guard for the playbook eval runner (no Ollama, no network). A scripted
///     <see cref="IChatClient" /> stands in for the node-local model so the runner's behaviour — final-text
///     return, tools-off, instruction flow-through, threadless — is fully reproducible.
/// </summary>
public sealed class MafPlaybookEvalAgentRunnerTests
{
    private const string BaselineInstructions = "You are a concise assistant. Answer directly.";
    private const string CandidateInstructions = "You are a concise assistant. CANDIDATE: prefer bullet points.";

    [Test]
    public async Task RunAsync_NonToolScriptedResponse_ReturnsFinalAssistantText()
    {
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        var result = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        AssertEx.Equal(ScriptedEvalChatClient.BaselineReply, result);
    }

    [Test]
    public async Task RunAsync_EmptyToolSet_PassesNoToolsToTheChatClient()
    {
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        var options = AssertEx.NotNull(scripted.LastOptions, "the chat client must have been invoked at least once");
        AssertEx.True(options.Tools is null || options.Tools.Count == 0,
            "the eval runner must run with an empty tool set (no tools passed to the model)");
    }

    [Test]
    public async Task RunAsync_PinsTemperatureToZero_ForDeterministicGenerations()
    {
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        var options = AssertEx.NotNull(scripted.LastOptions, "the chat client must have been invoked at least once");
        AssertEx.Equal(expected: (float?)0f, options.Temperature,
            "the eval runner must pin Temperature=0 so generations are deterministic, not sampled");
    }

    [Test]
    public async Task RunAsync_BaselineVersusCandidateInstructions_SurfacesDivergentText()
    {
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        var baseline = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);
        var candidate = await runner.RunAsync(scripted, CandidateInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        AssertEx.Equal(ScriptedEvalChatClient.BaselineReply, baseline);
        AssertEx.Equal(ScriptedEvalChatClient.CandidateReply, candidate);
        AssertEx.NotEqual(baseline, candidate);
        AssertEx.True(scripted.SawCandidateMarker,
            "the candidate instructions must reach the chat client as a system message (instructions flow through)");
    }

    [Test]
    public async Task RunAsync_RunsThreadless_AndStillReturnsText()
    {
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        var result = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        AssertEx.False(scripted.SawConversationId,
            "a threadless run must not carry a ChatOptions.ConversationId (no persisted session state)");
        AssertEx.NotNullOrEmpty(result);
    }

    [Test]
    public async Task RunAsync_InstructionsDeliveredOnce_AndAgentNameNeverLeaksToTheWire()
    {
        using var capturing = new CapturingEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(capturing, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        var messages = AssertEx.NotNull(capturing.CapturedMessages, "the chat client must have captured the outbound messages");

        var systemInstructionMessages = messages.Count(message =>
            message.Role == ChatRole.System && string.Equals(message.Text, BaselineInstructions, StringComparison.Ordinal));
        AssertEx.Equal(expected: 1, systemInstructionMessages);

        var outboundInstructions = capturing.CapturedOptions?.Instructions;
        AssertEx.True(string.IsNullOrEmpty(outboundInstructions),
            "instructions must not also ride ChatOptions.Instructions (that would double-send them)");

        AssertEx.False(messages.Any(message => (message.Text ?? string.Empty).Contains("playbook-eval", StringComparison.Ordinal)),
            "the eval agent name must never appear as message content");
        AssertEx.True(string.IsNullOrEmpty(outboundInstructions) || !outboundInstructions.Contains("playbook-eval", StringComparison.Ordinal),
            "the eval agent name must never be sent as instructions");
    }

    [Test]
    public async Task RunAsync_WhenEffortIsNull_ChatOptionsAreByteIdentical()
    {
        // The default keeps every existing caller's request exactly as it was: Temperature pinned, and NO additional
        // properties at all — not an empty dictionary, which would already be a different request on the wire.
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), cancellationToken: CancellationToken.None);

        var options = AssertEx.NotNull(scripted.LastOptions, "the chat client must have been invoked at least once");
        AssertEx.Equal(expected: (float?)0f, options.Temperature);
        AssertEx.Null(options.AdditionalProperties, "a null effort must add nothing to the request");
    }

    [Test]
    public async Task RunAsync_WhenEffortIsHigh_CarriesTheBudgetMarker()
    {
        // A supplied effort goes through the same matrix the production loop uses, so the eval sends what a real turn
        // at that effort would send: the graded think level and the per-request llama.cpp reasoning budget.
        using var scripted = new ScriptedEvalChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(scripted, BaselineInstructions, BuildTurns(), "high", CancellationToken.None);

        var properties = AssertEx.NotNull(scripted.LastOptions?.AdditionalProperties, "a supplied effort must reach the request");
        AssertEx.True(properties.TryGetValue<int>("xe.llama.reasoning_budget_tokens", out var budget));
        AssertEx.True(budget > 0, "a graded effort carries a positive reasoning budget");
        AssertEx.Equal(expected: (object)"high", properties["think"]);
    }

    private static List<ChatMessage> BuildTurns()
    {
        return
        [
            new ChatMessage(ChatRole.User, "Summarise the deployment status.")
        ];
    }

    /// <summary>
    ///     Scripted stand-in for the node-local model. Returns a distinct reply depending on whether the system
    ///     instructions in the replayed history contain the "CANDIDATE" marker, captures the
    ///     <see cref="ChatOptions" /> it was invoked with (to assert tools-off and threadless), and only ever drives
    ///     the non-streaming <c>GetResponseAsync</c> path that <c>RunAsync</c> uses.
    /// </summary>
    private sealed class ScriptedEvalChatClient : IChatClient
    {
        public const string BaselineReply = "Deployment status: green.";
        public const string CandidateReply = "Deployment status:\n- green";

        public ChatOptions? LastOptions { get; private set; }

        public bool SawCandidateMarker { get; private set; }

        public bool SawConversationId { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            if (options?.ConversationId is not null)
            {
                SawConversationId = true;
            }

            var hasCandidateMarker = messages
                                     .Where(message => message.Role == ChatRole.System)
                                     .Any(message => message.Text.Contains("CANDIDATE", StringComparison.Ordinal));
            if (hasCandidateMarker)
            {
                SawCandidateMarker = true;
            }

            var reply = hasCandidateMarker ? CandidateReply : BaselineReply;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The eval runner exercises the non-streaming RunAsync path only.");
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

    /// <summary>
    ///     Records the exact outbound <see cref="ChatMessage" /> list and <see cref="ChatOptions" /> the eval runner
    ///     hands to the model on the non-streaming <c>GetResponseAsync</c> path, so a test can assert the provider-wire
    ///     instruction contract (instructions delivered once, agent name never leaked).
    /// </summary>
    private sealed class CapturingEvalChatClient : IChatClient
    {
        public IReadOnlyList<ChatMessage>? CapturedMessages { get; private set; }

        public ChatOptions? CapturedOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            CapturedMessages = messages.ToList();
            CapturedOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("The eval runner exercises the non-streaming RunAsync path only.");
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

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}
