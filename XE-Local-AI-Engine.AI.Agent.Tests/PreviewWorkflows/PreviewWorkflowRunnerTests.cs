namespace XE_Local_AI_Engine.AI.Agent.Tests.PreviewWorkflows;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows;
using XE_Local_AI_Engine.AI.Agent.PreviewWorkflows.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Deterministic CI guard for the Preview workflow runner (no Ollama, no network). A recording streaming
///     <see cref="IChatClient" /> stands in for the node-local model. Agent executors run the STREAMING path,
///     so the fake MUST implement <see cref="IChatClient.GetStreamingResponseAsync" />.
/// </summary>
public sealed class PreviewWorkflowRunnerTests
{
    [Test]
    public async Task PreviewRunner_StartAgentAgentDebugEnd_PipesPrevOutputToNext()
    {
        using var client = new RecordingStreamingChatClient(("alpha", "ALPHA_OUTPUT"),
            ("beta", "BETA_OUTPUT"));
        var runner = NewRunner();

        var definition = BuildLinear(Agent("alpha", "Agent Alpha"),
            Agent("beta", "Agent Beta"),
            Debug("debug1"));

        await using var session = await runner.StartAsync(definition, _ => client, CancellationToken.None);
        var updates = await Drain(session);

        // AgentB's input must be AgentA's output: the recording client logs each invocation's user turns. Beta saw
        // exactly AgentA's output.
        var betaInbound = client.FirstUserTurnFor("beta");
        AssertEx.Equal("ALPHA_OUTPUT", betaInbound);

        // The run reached completion with the final agent's output.
        var completed = updates.Single(update => update.Kind == PreviewWorkflowUpdateKind.RunCompleted);
        AssertEx.Equal("BETA_OUTPUT", completed.Output);
    }

    [Test]
    public async Task PreviewRunner_NextAgent_SeesOnlyUpstreamOutput_NotFullHistory()
    {
        using var client = new RecordingStreamingChatClient(("alpha", "ALPHA_OUTPUT"),
            ("beta", "BETA_OUTPUT"));
        var runner = NewRunner();

        var definition = BuildLinear(Agent("alpha", "Agent Alpha"),
            Agent("beta", "Agent Beta"));

        await using var session = await runner.StartAsync(definition, _ => client, CancellationToken.None);
        _ = await Drain(session);

        // NEGATIVE ASSERTION (downstream-isolation guard): Beta's FIRST inbound is EXACTLY one user message carrying
        // only the transform output — no Start seed text, no instructions message, no prior assistant turn.
        var betaFirstInvocation = client.FirstInvocationMessagesFor("beta");
        AssertEx.Equal(expected: 1, betaFirstInvocation.Count);
        AssertEx.Equal(ChatRole.User, betaFirstInvocation[0].Role);
        AssertEx.Equal("ALPHA_OUTPUT", betaFirstInvocation[0].Text);

        AssertEx.False(betaFirstInvocation.Any(message => message.Role == ChatRole.System),
            "the downstream agent must not see any system/instruction message in its routed input");
        AssertEx.False(betaFirstInvocation.Any(message => message.Role == ChatRole.Assistant),
            "the downstream agent must not see the upstream agent's assistant turn");
        AssertEx.False(betaFirstInvocation.Any(message => (message.Text ?? string.Empty).Contains("SEED", StringComparison.Ordinal)),
            "the downstream agent must not see the Start seed text");
    }

    [Test]
    public async Task PreviewRunner_DebugNode_EmitsUpstream_ForwardsUnchanged()
    {
        using var client = new RecordingStreamingChatClient(("alpha", "ALPHA_OUTPUT"));
        var runner = NewRunner();

        var definition = BuildLinear(Agent("alpha", "Agent Alpha"),
            Debug("debug1"));

        await using var session = await runner.StartAsync(definition, _ => client, CancellationToken.None);
        var updates = await Drain(session);

        // The debug side-event is emitted exactly once, carrying the upstream agent's output.
        var debugEvents = updates.Where(update => update.Kind == PreviewWorkflowUpdateKind.NodeDebug).ToList();
        AssertEx.Equal(expected: 1, debugEvents.Count);
        AssertEx.Equal("debug1", debugEvents[0].NodeId);
        AssertEx.Equal("ALPHA_OUTPUT", debugEvents[0].Output);

        // Forwarded unchanged: the run completes with the same upstream output (the edge did not fork or mutate).
        var completed = updates.Single(update => update.Kind == PreviewWorkflowUpdateKind.RunCompleted);
        AssertEx.Equal("ALPHA_OUTPUT", completed.Output);
    }

    [Test]
    public async Task PreviewRunner_PauseNode_Surfaces_ThenContinueResumes()
    {
        using var client = new RecordingStreamingChatClient(("alpha", "ALPHA_OUTPUT"));
        var runner = NewRunner();

        var definition = BuildLinear(Agent("alpha", "Agent Alpha"),
            Pause("pause1"));

        await using var session = await runner.StartAsync(definition, _ => client, CancellationToken.None);

        // First drain halts at the pause: a RunPaused update surfaces BEFORE any RunCompleted.
        var firstPass = await Drain(session);
        var paused = firstPass.Single(update => update.Kind == PreviewWorkflowUpdateKind.RunPaused);
        AssertEx.Equal("pause1", paused.NodeId);
        AssertEx.Equal("ALPHA_OUTPUT", paused.Output);
        AssertEx.NotNullOrEmpty(paused.RequestId);
        AssertEx.False(firstPass.Any(update => update.Kind == PreviewWorkflowUpdateKind.RunCompleted),
            "the run must not complete before the pause is resumed");

        // Resume via the surfaced request id; re-draining drives the held run to completion.
        await session.ResumeAsync(paused.RequestId!, CancellationToken.None);
        var secondPass = await Drain(session);
        AssertEx.True(secondPass.Any(update => update.Kind == PreviewWorkflowUpdateKind.RunCompleted),
            "the resumed run must drive to completion");
    }

    [Test]
    public async Task PreviewRunner_TwoAgentsDifferentModels_EachRunsOnItsOwnModelClient()
    {
        // Two distinct models → two distinct recording clients. The resolver hands each agent the client for ITS model
        // id, so AgentB must run on model-b's client (NOT AgentA's model-a client).
        using var clientA = new RecordingStreamingChatClient(("alpha", "ALPHA_OUTPUT"));
        using var clientB = new RecordingStreamingChatClient(("beta", "BETA_OUTPUT"));
        var runner = NewRunner();

        var resolvedModelIds = new List<string>();

        IChatClient Resolve(string modelId)
        {
            resolvedModelIds.Add(modelId);
            return modelId switch
            {
                "model-a" => clientA,
                "model-b" => clientB,
                _ => throw new InvalidOperationException($"Unexpected model id '{modelId}'.")
            };
        }

        var definition = BuildLinear(Agent("alpha", "Agent Alpha", "model-a"),
            Agent("beta", "Agent Beta", "model-b"));

        await using var session = await runner.StartAsync(definition, Resolve, CancellationToken.None);
        var updates = await Drain(session);

        // The resolver was called once per distinct model — and with each agent's OWN model id.
        AssertEx.True(resolvedModelIds.Contains("model-a"), "the resolver must be asked for AgentA's model id.");
        AssertEx.True(resolvedModelIds.Contains("model-b"), "the resolver must be asked for AgentB's model id.");

        // AgentB ran on model-b's client (clientB recorded the beta invocation; AgentA's client never saw beta).
        var betaInbound = clientB.FirstUserTurnFor("beta");
        AssertEx.Equal("ALPHA_OUTPUT", betaInbound);

        // Each client served exactly its own agent — clientA only ran alpha (its input is the Start seed).
        AssertEx.Equal("START_SEED_TEXT", clientA.FirstUserTurnFor("alpha"));

        var completed = updates.Single(update => update.Kind == PreviewWorkflowUpdateKind.RunCompleted);
        AssertEx.Equal("BETA_OUTPUT", completed.Output);
    }

    private static PreviewWorkflowRunner NewRunner()
    {
        return new PreviewWorkflowRunner(NullLoggerFactory.Instance, EmptyServiceProvider.Instance);
    }

    private static async Task<List<PreviewWorkflowUpdate>> Drain(IPreviewWorkflowRunSession session)
    {
        var updates = new List<PreviewWorkflowUpdate>();
        await foreach (var update in session.WatchAsync(CancellationToken.None))
        {
            updates.Add(update);
        }

        return updates;
    }

    private static PreviewAgentNode Agent(string id, string label, string model = "test-model")
    {
        return new PreviewAgentNode
        {
            Id = id,
            Kind = PreviewNodeKind.Agent,
            Label = label,
            Instructions = $"INSTR_{id}",
            ModelId = model
        };
    }

    private static PreviewWorkflowNode Debug(string id)
    {
        return new PreviewWorkflowNode
        {
            Id = id,
            Kind = PreviewNodeKind.Debug
        };
    }

    private static PreviewWorkflowNode Pause(string id)
    {
        return new PreviewWorkflowNode
        {
            Id = id,
            Kind = PreviewNodeKind.Pause
        };
    }

    /// <summary>
    ///     Builds a linear Start → [middle nodes…] → End graph from the supplied middle nodes, wiring one edge between
    ///     each consecutive pair. Start carries a seed text containing the "SEED" marker (used by the negative test).
    /// </summary>
    private static PreviewWorkflowDefinition BuildLinear(params PreviewWorkflowNode[] middle)
    {
        var start = new PreviewWorkflowNode
        {
            Id = "start",
            Kind = PreviewNodeKind.Start
        };
        var end = new PreviewWorkflowNode
        {
            Id = "end",
            Kind = PreviewNodeKind.End
        };

        var nodes = new List<PreviewWorkflowNode>
        {
            start
        };
        nodes.AddRange(middle);
        nodes.Add(end);

        var edges = new List<PreviewWorkflowEdge>();
        for (var i = 0; i < nodes.Count - 1; i++)
        {
            edges.Add(new PreviewWorkflowEdge
            {
                SourceId = nodes[i].Id,
                TargetId = nodes[i + 1].Id
            });
        }

        return new PreviewWorkflowDefinition
        {
            StartText = "START_SEED_TEXT",
            Nodes = nodes,
            Edges = edges
        };
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();

        public object? GetService(Type serviceType)
        {
            return null;
        }
    }

    /// <summary>
    ///     Recording streaming <see cref="IChatClient" />. Returns scripted assistant text per agent (matched by the
    ///     agent's instructions marker carried in <see cref="ChatOptions.Instructions" />), and records the routed
    ///     ChatMessages of each invocation so tests can assert downstream-node isolation (each node sees only its
    ///     upstream output, not the full history). Drives ONLY the streaming path.
    /// </summary>
    private sealed class RecordingStreamingChatClient : IChatClient
    {
        private readonly ConcurrentDictionary<string, List<IReadOnlyList<ChatMessage>>> _invocationsByAgentId = new(StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, string> _replyByAgentId;

        public RecordingStreamingChatClient(params (string AgentId, string Reply)[] scripts)
        {
            _replyByAgentId = scripts.ToDictionary(script => script.AgentId, script => script.Reply, StringComparer.Ordinal);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // ChatClientAgent runs the streaming path; the non-streaming path must not be exercised.
            throw new NotSupportedException("The preview runner exercises the streaming GetStreamingResponseAsync path only.");
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var messageList = messages.ToList();
            var agentId = ResolveAgentId(options, messageList);
            _invocationsByAgentId
                .GetOrAdd(agentId, _ => [])
                .Add(messageList);

            var reply = _replyByAgentId.TryGetValue(agentId, out var scripted) ? scripted : $"UNSCRIPTED::{agentId}";
            yield return new ChatResponseUpdate(ChatRole.Assistant, reply);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceType == typeof(IChatClient) ? this : null;
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        public IReadOnlyList<ChatMessage> FirstInvocationMessagesFor(string agentId)
        {
            return _invocationsByAgentId[agentId][0];
        }

        public string FirstUserTurnFor(string agentId)
        {
            var first = FirstInvocationMessagesFor(agentId);
            return first.First(message => message.Role == ChatRole.User).Text ?? string.Empty;
        }

        // The agent's instructions ride ChatOptions.Instructions (set from PreviewAgentNode.Instructions =
        // "INSTR_<id>"); fall back to scanning any system message for the marker for robustness.
        private string ResolveAgentId(ChatOptions? options, IReadOnlyList<ChatMessage> messages)
        {
            var instructions = options?.Instructions;
            var marker = instructions
                         ?? messages.FirstOrDefault(message => message.Role == ChatRole.System)?.Text;
            if (marker is not null)
            {
                foreach (var id in _replyByAgentId.Keys)
                {
                    if (marker.Contains($"INSTR_{id}", StringComparison.Ordinal))
                    {
                        return id;
                    }
                }
            }

            return "unknown";
        }
    }
}
