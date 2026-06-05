// Production-grade tests of the handoff orchestration factory + run session. Fully deterministic — a
// scripted IChatClient stands in for the model (NO Ollama, NO network) — and drives the PRODUCTION surface
// (IOrchestrationAgentFactory.CreateAsync + IOrchestrationRunSession.WatchAsync / RespondToApprovalAsync), not the
// raw workflow. Evolves the framework-handoff probe shapes into regression guards.

#pragma warning disable MEAI001 // ApprovalRequiredAIFunction is [Experimental]; adopted deliberately (loop plan §4).
namespace XE_Local_AI_Engine.AI.Agent.Tests.Invocation.Orchestration;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class OrchestrationAgentFactoryTests
{
    private const string TriageInstructions =
        "You are the TRIAGE agent. Hand off the conversation to the specialist.";

    private const string SpecialistInstructions =
        "You are the SPECIALIST agent. Answer the user's question directly.";

    private const string SpecialistAnswer = "SPECIALIST_ANSWER: the migration completed successfully.";

    [Test]
    public async Task CreateAsync_TwoAgentHandoff_RoutesToSpecialistAndPreservesHistory()
    {
        using var fake = new HandoffScriptedChatClient(SpecialistAnswer);
        var factory = CreateFactory(fake);

        var definition = BuildHandoffDefinition();
        var seed = new List<ChatMessage>
        {
            new(ChatRole.User, "Did the database migration complete?")
        };

        await using var session = await factory.CreateAsync(definition, seed);

        var text = new StringBuilder();
        var sawTerminal = false;
        await foreach (var update in session.WatchAsync())
        {
            if (update.Kind == OrchestrationUpdateKind.TextDelta)
            {
                text.Append(update.Text);
            }

            if (update.Kind == OrchestrationUpdateKind.TerminalOutput)
            {
                sawTerminal = true;
            }
        }

        AssertEx.True(fake.SpecialistInvocations > 0, "the specialist must be invoked after the handoff");
        AssertEx.True(fake.SpecialistSawUserQuestion, "conversation history must carry across the handoff hop");
        AssertEx.Contains(text.ToString(), "SPECIALIST_ANSWER", message: "the specialist's answer must reach the normalized stream");
        AssertEx.True(sawTerminal, "the run must surface a terminal output update");
    }

    [Test]
    public async Task CreateAsync_StreamingUpdate_TagsTheEmittingParticipant()
    {
        using var fake = new HandoffScriptedChatClient(SpecialistAnswer);
        var factory = CreateFactory(fake);
        var definition = BuildHandoffDefinition();
        var seed = new List<ChatMessage>
        {
            new(ChatRole.User, "Did the database migration complete?")
        };

        await using var session = await factory.CreateAsync(definition, seed);

        var specialistTagged = false;
        await foreach (var update in session.WatchAsync())
        {
            if (update.Kind == OrchestrationUpdateKind.TextDelta
                && (update.Text?.Contains("SPECIALIST_ANSWER", StringComparison.Ordinal) ?? false))
            {
                specialistTagged = string.Equals(update.ParticipantKey, "specialist", StringComparison.Ordinal)
                                   && string.Equals(update.ParticipantName, "Specialist", StringComparison.Ordinal);
            }
        }

        AssertEx.True(specialistTagged, "the specialist's text delta must be attributed to the specialist participant");
    }

    [Test]
    public async Task CreateAsync_ApprovalAcrossHandoff_ExecutesOnlyWhenApproved()
    {
        await RunApprovalAcrossHandoff(approve: true, decorateClient: false);
        await RunApprovalAcrossHandoff(approve: false, decorateClient: false);
    }

    [Test]
    public async Task CreateAsync_ApprovalAcrossHandoff_OverProductionDecoratedClient_StillSurfacesAndExecutes()
    {
        // The production composition root hands the factory an IChatClient ALREADY decorated by
        // DecorateChatClientPipeline (ToolInvocationObservabilityChatClient + UseFunctionInvocation/FICC). This proves
        // a participant with BOTH an outgoing handoff edge AND its own approval-required tool still works over that
        // pre-decorated client: ChatClientAgent's ctor sees an existing FICC and must still set the agent's own tools
        // as AdditionalTools so the approval surfaces (RequestInfoEvent) and the tool executes once approved, while
        // handoff_to_* flows through. If this regressed, the fallback is to give the factory the base (pre-decoration)
        // client.
        await RunApprovalAcrossHandoff(approve: true, decorateClient: true);
        await RunApprovalAcrossHandoff(approve: false, decorateClient: true);
    }

    [Test]
    public async Task CreateAsync_DelayedApprovalBeyondIdleTimeout_DoesNotCancelTheRun()
    {
        // Regression guard for the per-quiescence idle bound: the idle clock is SUSPENDED while an approval is
        // pending, so a human decision that arrives long after the configured idle timeout must NOT cancel the run.
        // Here the idle timeout is 1s but the approval is answered after ~2.5s; the tool must still execute. (Before
        // the fix, the idle CTS was a whole-run wall-clock cap and would have cancelled the held run during the wait.)
        const string ownToolName = "lookup_customer";
        var lookupExecuted = 0;
        var lookupTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string customerId) =>
            {
                lookupExecuted++;
                return "customer_data: premium tier";
            },
            ownToolName,
            "Looks up customer data. Requires approval because it accesses PII."));

        using var fake = new CombinedScriptedChatClient(ownToolName, SpecialistAnswer);
        var clientLocalRegistry = new FakeClientLocalToolRegistry(lookupTool);
        var factory = CreateFactory(fake, clientLocalToolRegistry: clientLocalRegistry, idleTimeoutSeconds: 1);

        var triage = Triage() with
        {
            Tools = [InvocationToolBridge.CreateOfferPlaceholder(ownToolName)]
        };
        var definition = new OrchestrationAgentDefinition
        {
            Triage = triage,
            Participants = [triage, Specialist()],
            Edges =
            [
                new OrchestrationEdge
                {
                    FromKey = "triage",
                    ToKey = "specialist",
                    Reason = "Route after customer lookup."
                }
            ]
        };
        var seed = new List<ChatMessage>
        {
            new(ChatRole.User, "Look up customer C-42, then hand off to specialist.")
        };

        await using var session = await factory.CreateAsync(definition, seed);

        var sawApproval = false;
        await foreach (var update in session.WatchAsync())
        {
            if (update.Kind == OrchestrationUpdateKind.ApprovalRequest)
            {
                sawApproval = true;

                // Wait well past the 1s idle timeout before responding; the suspended clock must keep the run alive.
                await Task.Delay(TimeSpan.FromMilliseconds(2500));
                await session.RespondToApprovalAsync(update.RequestId!, approved: true, reason: null);
            }
        }

        AssertEx.True(sawApproval, "the workflow must surface the approval request");
        AssertEx.Equal(1, lookupExecuted, "the tool must execute after a delayed approval (the idle clock was suspended while pending)");
        AssertEx.True(fake.SpecialistInvocations > 0, "the handoff must complete after the delayed approval resumes the run");
    }

    [Test]
    public async Task CreateAsync_MeshDefault_NoEdges_BuildsAndDrivesTheTriage()
    {
        // No explicit edges => the factory takes the AddParticipants/mesh branch (MAF auto-wires every agent to
        // hand off to every other). This guards that the empty-edge code path builds a workflow and drives the
        // initial (triage) agent, which gets the framework's injected handoff tool. (The specific mesh route a
        // handoff index resolves to is internal to MAF's auto-wiring; the deterministic triage->specialist route is
        // asserted via the explicit-edge tests above.)
        using var fake = new MeshProbeChatClient();
        var factory = CreateFactory(fake);

        var definition = new OrchestrationAgentDefinition
        {
            Triage = Triage(),
            Participants = [Triage(), Specialist()],
            Edges = [],
            MaxTurnsPerAgent = 2
        };
        var seed = new List<ChatMessage>
        {
            new(ChatRole.User, "Did the database migration complete?")
        };

        await using var session = await factory.CreateAsync(definition, seed);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await foreach (var _ in session.WatchAsync(cts.Token))
            {
                // Drain; the probe answers immediately on its first turn so the run completes promptly.
            }
        }
        catch (OperationCanceledException)
        {
            // Mesh routing is MAF-internal; the structural guarantee is that the triage ran.
        }

        AssertEx.True(fake.TriageInvoked, "the mesh-default branch must build a workflow and drive the initial (triage) agent");
        AssertEx.NotEmpty(fake.OfferedHandoffTools, "the framework must inject a handoff tool into the triage agent under mesh wiring");
    }

    private static async Task RunApprovalAcrossHandoff(bool approve, bool decorateClient)
    {
        const string ownToolName = "lookup_customer";
        var lookupExecuted = 0;
        var lookupArgument = string.Empty;
        var lookupTool = new ApprovalRequiredAIFunction(AIFunctionFactory.Create((string customerId) =>
            {
                lookupExecuted++;
                lookupArgument = customerId;
                return "customer_data: premium tier";
            },
            ownToolName,
            "Looks up customer data. Requires approval because it accesses PII."));

        using var fake = new CombinedScriptedChatClient(ownToolName, SpecialistAnswer);

        // When decorateClient is set, replicate the production pipeline (ToolInvocationObservabilityChatClient +
        // UseFunctionInvocation) so the factory receives an already-FICC'd client — the real composition-root shape.
        var chatClient = decorateClient
            ? fake.AsBuilder()
                  .Use(inner => new ToolInvocationObservabilityChatClient(inner, NullLogger<ToolInvocationObservabilityChatClient>.Instance))
                  .UseFunctionInvocation(NullLoggerFactory.Instance)
                  .Build()
            : fake;

        // Production path: the participant carries the OFFER (a name-only placeholder); the approval-wrapped
        // executable lives in the ClientLocal registry and the factory swaps the placeholder for it (Option B). This
        // exercises the exact resolution + approval-wrapping that the single-agent factory uses.
        var clientLocalRegistry = new FakeClientLocalToolRegistry(lookupTool);
        var factory = CreateFactory(chatClient, clientLocalToolRegistry: clientLocalRegistry);

        var triage = Triage() with
        {
            Tools = [InvocationToolBridge.CreateOfferPlaceholder(ownToolName)]
        };
        var definition = new OrchestrationAgentDefinition
        {
            Triage = triage,
            Participants = [triage, Specialist()],
            Edges =
            [
                new OrchestrationEdge
                {
                    FromKey = "triage",
                    ToKey = "specialist",
                    Reason = "Route after customer lookup."
                }
            ]
        };
        var seed = new List<ChatMessage>
        {
            new(ChatRole.User, "Look up customer C-42, then hand off to specialist.")
        };

        await using var session = await factory.CreateAsync(definition, seed);

        var sawApproval = false;
        await foreach (var update in session.WatchAsync())
        {
            if (update.Kind == OrchestrationUpdateKind.ApprovalRequest)
            {
                sawApproval = true;
                AssertEx.Equal(ownToolName, update.ToolName, "the approval request must name the approval-required tool");
                AssertEx.Equal(0, lookupExecuted, "the tool must NOT execute before approval is granted");
                AssertEx.NotNullOrEmpty(update.RequestId);
                await session.RespondToApprovalAsync(update.RequestId!, approve, null);
            }
        }

        AssertEx.True(sawApproval, "the workflow must surface the approval request across the run");
        AssertEx.True(fake.SpecialistInvocations > 0, "triage must hand off to the specialist in both approve and reject paths");
        if (approve)
        {
            AssertEx.Equal(1, lookupExecuted, "the approved tool must execute exactly once");
            AssertEx.Equal("C-42", lookupArgument, "the approved tool must receive the scripted argument");
        }
        else
        {
            AssertEx.Equal(0, lookupExecuted, "the rejected tool must never execute");
        }
    }

    private static OrchestrationAgentDefinition BuildHandoffDefinition()
    {
        return new OrchestrationAgentDefinition
        {
            Triage = Triage(),
            Participants = [Triage(), Specialist()],
            Edges =
            [
                new OrchestrationEdge
                {
                    FromKey = "triage",
                    ToKey = "specialist",
                    Reason = "Route domain questions to the specialist."
                }
            ]
        };
    }

    private static OrchestrationParticipant Triage()
    {
        return new OrchestrationParticipant
        {
            Key = "triage",
            Name = "Triage",
            Description = "Triage agent.",
            Instructions = TriageInstructions,
            ModelId = "qwen3:8b",
            Tools = []
        };
    }

    private static OrchestrationParticipant Specialist()
    {
        return new OrchestrationParticipant
        {
            Key = "specialist",
            Name = "Specialist",
            Description = "Specialist agent.",
            Instructions = SpecialistInstructions,
            ModelId = "qwen3:8b",
            Tools = []
        };
    }

    private static OrchestrationAgentFactory CreateFactory(IChatClient chatClient,
        IAgentToolRegistry? toolRegistry = null,
        IClientLocalToolRegistry? clientLocalToolRegistry = null,
        IMcpToolRegistry? mcpToolRegistry = null,
        int idleTimeoutSeconds = 20)
    {
        return new OrchestrationAgentFactory(chatClient,
            Options.Create(new OrchestrationAgentOptions
            {
                IdleTimeoutSeconds = idleTimeoutSeconds
            }),
            NullLogger<OrchestrationAgentFactory>.Instance,
            NullLoggerFactory.Instance,
            FakeServiceProvider.Instance,
            toolRegistry ?? new FakeToolRegistry(),
            clientLocalToolRegistry ?? new FakeClientLocalToolRegistry(),
            mcpToolRegistry ?? new FakeMcpToolRegistry());
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToUpdates(ChatResponse response,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        foreach (var message in response.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ChatResponseUpdate(message.Role, message.Contents);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Scripted model for handoff routing. Discriminates triage vs specialist by the presence of a
    ///     <c>handoff_to_*</c> tool in <c>options.Tools</c> (the handoff builder injects it only into agents with
    ///     outgoing handoffs). Triage emits the framework handoff call by its real injected name; specialist answers.
    /// </summary>
    private sealed class HandoffScriptedChatClient : IChatClient
    {
        private readonly string _specialistAnswer;

        public HandoffScriptedChatClient(string specialistAnswer)
        {
            _specialistAnswer = specialistAnswer;
        }

        public int SpecialistInvocations { get; private set; }

        public bool SpecialistSawUserQuestion { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Build(messages.ToList(), options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return ToUpdates(Build(messages.ToList(), options), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private ChatResponse Build(List<ChatMessage> list, ChatOptions? options)
        {
            // Discriminate by the participant's system instructions: the SPECIALIST answers; the triage hands off.
            // Keying on the instructions (not on tool presence) is robust because the handoff builder injects a
            // handoff_to_* tool into any agent with an outgoing edge.
            var isSpecialist = list.Any(message =>
                message.Role == ChatRole.System
                && (message.Text?.Contains("SPECIALIST agent", StringComparison.Ordinal) ?? false));
            var handoffTool = options?.Tools?.FirstOrDefault(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal));

            if (isSpecialist || handoffTool is null)
            {
                SpecialistInvocations++;
                SpecialistSawUserQuestion = list.Any(message => message.Text?.Contains("database migration", StringComparison.Ordinal) ?? false);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, _specialistAnswer));
            }

            var call = new FunctionCallContent($"call-{handoffTool.Name}", handoffTool.Name, new Dictionary<string, object?>());
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                call
            }));
        }
    }

    /// <summary>
    ///     Scripted model for the combined approval+handoff scenario. Triage (identified by the handoff tool in
    ///     <c>options.Tools</c>) first emits its own approval-required tool call, then — once that tool's result is in
    ///     history — emits the handoff call. The specialist answers in plain text.
    /// </summary>
    private sealed class CombinedScriptedChatClient : IChatClient
    {
        private readonly string _ownToolName;
        private readonly string _specialistAnswer;

        public CombinedScriptedChatClient(string ownToolName, string specialistAnswer)
        {
            _ownToolName = ownToolName;
            _specialistAnswer = specialistAnswer;
        }

        public int SpecialistInvocations { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Build(messages.ToList(), options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return ToUpdates(Build(messages.ToList(), options), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private ChatResponse Build(List<ChatMessage> list, ChatOptions? options)
        {
            var handoffTool = options?.Tools?.FirstOrDefault(tool => tool.Name.StartsWith("handoff_to_", StringComparison.Ordinal));
            if (handoffTool is null)
            {
                SpecialistInvocations++;
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, _specialistAnswer));
            }

            var hasOwnToolResult = list.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any();
            if (hasOwnToolResult)
            {
                var handoffCall = new FunctionCallContent($"call-{handoffTool.Name}", handoffTool.Name, new Dictionary<string, object?>());
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
                {
                    handoffCall
                }));
            }

            var ownCall = new FunctionCallContent($"call-{_ownToolName}", _ownToolName, new Dictionary<string, object?>
            {
                ["customerId"] = "C-42"
            });
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, new List<AIContent>
            {
                ownCall
            }));
        }
    }

    /// <summary>
    ///     A probe for the mesh-default branch: it records that the initial (triage) agent ran and the handoff tools
    ///     the framework injected into it, then answers immediately (no handoff) so the run completes deterministically.
    ///     Used to assert that empty-edge topology builds + drives the triage; the exact mesh route a handoff index
    ///     resolves to is internal to MAF and is covered deterministically by the explicit-edge tests.
    /// </summary>
    private sealed class MeshProbeChatClient : IChatClient
    {
        public bool TriageInvoked { get; private set; }

        public IReadOnlyList<string> OfferedHandoffTools { get; private set; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Build(options));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return ToUpdates(Build(options), cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        private ChatResponse Build(ChatOptions? options)
        {
            TriageInvoked = true;
            OfferedHandoffTools = options?.Tools?
                                         .Select(tool => tool.Name)
                                         .Where(name => name.StartsWith("handoff_to_", StringComparison.Ordinal))
                                         .ToList()
                                  ?? [];
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Mesh probe answer."));
        }
    }

    private sealed class FakeToolRegistry : IAgentToolRegistry
    {
        private readonly IReadOnlyList<AITool> _tools;

        public FakeToolRegistry(params AITool[] tools)
        {
            _tools = tools;
        }

        public IReadOnlyList<AITool> GetLocalChatTools()
        {
            return _tools;
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetLocalChatToolDescriptors()
        {
            return [];
        }
    }

    private sealed class FakeClientLocalToolRegistry : IClientLocalToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public FakeClientLocalToolRegistry(params AITool[] tools)
        {
            foreach (var function in tools.OfType<AIFunction>())
            {
                _tools[function.Name] = function;
            }
        }

        public bool TryResolve(string toolName, [NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(toolName, out tool);
        }
    }

    private sealed class FakeMcpToolRegistry : IMcpToolRegistry
    {
        private readonly Dictionary<string, AITool> _tools = new(StringComparer.Ordinal);

        public bool TryResolve(string name, [NotNullWhen(true)] out AITool? tool)
        {
            return _tools.TryGetValue(name, out tool);
        }

        public IReadOnlyList<LocalChatToolDescriptor> GetDescriptors()
        {
            return [];
        }

        public void ReplaceSnapshot(IReadOnlyList<McpRegisteredTool> tools)
        {
            _tools.Clear();
            foreach (var tool in tools)
            {
                _tools[tool.Name] = tool.Executable;
            }
        }
    }

    private sealed class FakeServiceProvider : IServiceProvider
    {
        public static FakeServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }
}
#pragma warning restore MEAI001
