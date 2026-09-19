namespace XE_Local_AI_Engine.Tests.Agents;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Eval.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Mocks;

/// <summary>
///     Three sites assemble the agent prompt by hand: <c>InvocationAgentFactory.BuildSeedMessages</c> (production),
///     <c>MafPlaybookEvalAgentRunner.RunAsync</c> (the eval gate, which must measure a prompt's effect against the
///     real prompt shape) and <c>StructuredAgentRunner.RunAsync</c> (dataset teachers). The separation is deliberate
///     and recorded at each site; what nothing enforced was the FIDELITY claim — a change to production seed
///     assembly propagates to neither mirror, and a silently diverged eval runner would promote or reject playbook
///     actions on the basis of a prompt shape production no longer uses.
///     These assertions are the mirror, made mechanical: on the ACTUAL outbound provider call, the system
///     instructions arrive exactly once as the leading message, never a second time through the
///     <c>ChatClientAgent</c> constructor or <see cref="ChatOptions.Instructions" />, and every run is threadless so
///     no turn inherits the previous one's history.
///     A failure here means the three shapes have diverged; fix the diverged site or move this pin deliberately.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SeedAssemblyContractTests
{
    private const string Instructions = "You are the worker. Follow the playbook exactly.";
    private const string FirstTurn = "Summarise the deployment status.";
    private const string SecondTurn = "Now list the open incidents.";
    private const string TeacherModel = "teacher.gguf";

    [Test]
    public async Task InvocationAgentFactory_DeliversInstructionsOnce_AndRunsThreadless()
    {
        using var chatClient = new RecordingChatClient();
        var factory = CreateInvocationAgentFactory(chatClient);

        await using var first = await factory.CreateAsync(Definition(FirstTurn));
        AssertEx.Null(first.Session, "the production invocation context is threadless — the runner passes session: null.");
        await DriveStreamingAsync(first);

        await using var second = await factory.CreateAsync(Definition(SecondTurn));
        await DriveStreamingAsync(second);

        AssertSeedContract(chatClient);
    }

    [Test]
    public async Task MafPlaybookEvalAgentRunner_DeliversInstructionsOnce_AndRunsThreadless()
    {
        using var chatClient = new RecordingChatClient();
        var runner = new MafPlaybookEvalAgentRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(chatClient, Instructions, [new ChatMessage(ChatRole.User, FirstTurn)]);
        _ = await runner.RunAsync(chatClient, Instructions, [new ChatMessage(ChatRole.User, SecondTurn)]);

        AssertSeedContract(chatClient);
    }

    [Test]
    public async Task StructuredAgentRunner_DeliversInstructionsOnce_AndRunsThreadless()
    {
        using var chatClient = new RecordingChatClient();
        var capabilities = Substitute.For<IModelCapabilityResolver>();
        _ = capabilities.ResolveAsync(TeacherModel, Arg.Any<CancellationToken>())
                        .Returns(new ModelCapabilitySnapshot(SupportsThinking: false, true, IsCloud: false));
        var runner = new StructuredAgentRunner(capabilities, NullLoggerFactory.Instance, EmptyServiceProvider.Instance);

        _ = await runner.RunAsync(chatClient, TeacherRequest(FirstTurn));
        _ = await runner.RunAsync(chatClient, TeacherRequest(SecondTurn));

        AssertSeedContract(chatClient);
    }

    // The shared contract, asserted identically for all three sites against the messages the provider actually saw.
    private static void AssertSeedContract(RecordingChatClient chatClient)
    {
        AssertEx.Equal(expected: 2, chatClient.Calls.Count, "each site must have reached the provider once per run.");

        var expectedTurns = new[]
        {
            FirstTurn,
            SecondTurn
        };
        for (var index = 0; index < chatClient.Calls.Count; index++)
        {
            var (messages, options) = chatClient.Calls[index];

            AssertEx.Equal(expected: 2, messages.Count,
                "the outbound prompt is exactly the leading system message plus this run's turns — nothing else is injected.");
            AssertEx.Equal(ChatRole.System, messages[0].Role, "the instructions must be the LEADING message.");
            AssertEx.Equal(Instructions, messages[0].Text);
            AssertEx.Equal(expected: 1, messages.Count(message => message.Role == ChatRole.System && string.Equals(message.Text, Instructions, StringComparison.Ordinal)),
                "the instructions must be delivered exactly once — a non-null ChatClientAgent ctor instructions argument would double-send them.");

            var outboundInstructions = options?.Instructions ?? string.Empty;
            AssertEx.False(outboundInstructions.Contains(Instructions, StringComparison.Ordinal),
                "the instructions must not also ride ChatOptions.Instructions (the second way MAF double-sends ctor instructions).");

            // Threadless: run N carries run N's turn and nothing from run N-1.
            AssertEx.Equal(expectedTurns[index], messages[1].Text,
                "each run must send its own turn; a persisted session would replay the previous run's history here.");
        }
    }

    private static InvocationAgentDefinition Definition(string userTurn)
    {
        return new InvocationAgentDefinition("qwen3.5:0.8b", Instructions, [], [new ChatMessage(ChatRole.User, userTurn)]);
    }

    private static StructuredAgentRequest TeacherRequest(string userTurn)
    {
        return new StructuredAgentRequest { ModelName = TeacherModel, SystemInstructions = Instructions, UserPrompt = userTurn, OutputMode = TeacherOutputMode.ValidateAfter, ResponseSchema = TeacherSchema, Temperature = 0f, Seed = null };
    }

    private static readonly JsonElement TeacherSchema = JsonDocument.Parse("""{"type":"object","properties":{"userMessage":{"type":"string"}}}""").RootElement.Clone();

    private static async Task DriveStreamingAsync(InvocationAgentContext context)
    {
        // Exactly how InvocationRunner drives it: the seed messages, session: null, the context's run options.
        await foreach (var _ in context.Agent.RunStreamingAsync(context.SeedMessages, context.Session, context.RunOptions, CancellationToken.None))
        {
            // Drain so the inner chat client is actually invoked.
        }
    }

    private static InvocationAgentFactory CreateInvocationAgentFactory(IChatClient chatClient)
    {
        // The real registries where one exists with no dependencies of its own; the custom-tool catalog is a
        // hand-written empty fake because these AI.Agent interfaces are internal and Castle DynamicProxy cannot proxy
        // an internal type without an InternalsVisibleTo("DynamicProxyGenAssembly2") this repo deliberately omits.
        return new InvocationAgentFactory(chatClient,
            Options.Create(new InvocationAgentOptions()),
            NullLogger<InvocationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            EmptyServiceProvider.Instance,
            new LocalAgentToolRegistry(TimeProvider.System),
            new EmptyClientLocalToolRegistry(),
            new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
            new EmptyCustomToolCatalog());
    }

    private sealed class EmptyCustomToolCatalog : ICustomToolCatalog
    {
        public Task<IReadOnlyList<LocalChatToolDescriptor>> GetDescriptorsAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<LocalChatToolDescriptor>>([]);
        }

        public Task<IReadOnlyDictionary<string, AITool>> TryResolveManyAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyDictionary<string, AITool>>(new Dictionary<string, AITool>(StringComparer.Ordinal));
        }
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    /// <summary>Records the exact outbound message list and options of every provider call, on both paths.</summary>
    private sealed class RecordingChatClient : IChatClient
    {
        private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _calls = [];

        public IReadOnlyList<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls => _calls;

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Capture(messages, options);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, """{"userMessage":"ok"}""")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            Capture(messages, options);
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private void Capture(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            _calls.Add(([.. messages], options));
        }
    }
}
