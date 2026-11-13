namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Capacity;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Tests.Invocation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>An Agent node's <c>repository</c> option through the REAL runner, tool loop and <c>read_file</c> handler; only the model is scripted.</summary>
/// <remarks>The model calls <c>read_file</c> on <c>notes.txt</c> once and answers with the tool's result, so the node's text IS what the handler read.</remarks>
[Category(TestCategories.Integration)]
public sealed class GraphWorkflowAgentRepositoryTests
{
    private const string NoWorkspace = "No project workspace is available";

    [ClassDataSource<GraphWorkflowRepositoryHostFixture>(Shared = SharedType.PerClass)]
    public required GraphWorkflowRepositoryHostFixture Host { get; init; }

    /// <summary>
    ///     The risk the convergence design left open: the scope the executor opens around its invocation reaches a tool handler
    ///     the framework calls from inside the runner's tool loop.
    /// </summary>
    [Test]
    public async Task ARepositoryNode_ReadsItsBoundProjectThroughTheRealRunnerAndToolLoop()
    {
        var projectId = Host.BindProject("repository-sentinel-41");
        await using var harness = new GraphWorkflowHarness(Host);
        var agentDefinitionId = await SeedAgentAsync(harness, ["read_file"]);

        var analyze = await RunAsync(harness, Graph(agentDefinitionId, """, "repository": true"""), Input(projectId));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, analyze.Status, analyze.Error);
        AssertEx.Contains(Text(analyze), "repository-sentinel-41");
    }

    /// <summary>The run input names a project either way: the option, not the input, opens the scope, so the read goes to AgentHome.</summary>
    [Test]
    [Arguments("")]
    [Arguments(""", "repository": false""")]
    public async Task ANodeWithoutTheOption_ReadsNoRepositoryEvenWhenTheRunNamesOne(string option)
    {
        var projectId = Host.BindProject("repository-sentinel-off");
        await using var harness = new GraphWorkflowHarness(Host);
        var agentDefinitionId = await SeedAgentAsync(harness, ["read_file"]);

        var analyze = await RunAsync(harness, Graph(agentDefinitionId, option), Input(projectId));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, analyze.Status, analyze.Error);
        AssertEx.Contains(Text(analyze), NoWorkspace);
        AssertEx.False(Text(analyze).Contains("repository-sentinel-off", StringComparison.Ordinal));
    }

    /// <summary>Each refusal fails the node as a configuration error before capacity, naming why and never a host path.</summary>
    [Test]
    [Arguments("no-project", "names no Development project ('developmentProjectId')")]
    [Arguments("unknown-project", "repository cannot be resolved (reconnect it on the Development page)")]
    [Arguments("unsafe-binding", "repository cannot be resolved (reconnect it on the Development page)")]
    [Arguments("no-repository-tool", "its agent lists none of them")]
    [Arguments("cloud-model", "would let the cloud model")]
    public async Task ARepositoryNode_IsRefusedBeforeCapacity(string reason, string expected)
    {
        var model = reason == "cloud-model"
            ? GraphWorkflowModels.Grant("graph-cloud-repository", new ExternalProviderCloudGrants
            {
                UnattendedRuns = true
            })
            : $"graph-repository-{reason}";
        await using var harness = new GraphWorkflowHarness(Host);
        var agentDefinitionId = await SeedAgentAsync(harness, reason == "no-repository-tool" ? [] : ["read_file"]);
        var input = reason switch
        {
            "no-project" => null,
            "unknown-project" => Input(Guid.NewGuid()),
            "unsafe-binding" => Input(Host.BindUnsafeProject()),
            _ => Input(Host.BindProject("repository-sentinel-refused"))
        };

        var analyze = await RunAsync(harness, Graph(agentDefinitionId, $$""", "repository": true, "model": "{{model}}" """), input);

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, analyze.FailureClass);
        AssertEx.Contains(analyze.Error, expected);
        AssertEx.False(AssertEx.NotNull(analyze.Error).Contains(GraphWorkflowRepositoryHostFixture.UnsafePath, StringComparison.Ordinal), "a binding failure never names the host path.");
        AssertEx.Empty(Capacity(harness).ReservationsFor(model), "refused before capacity.");
    }

    /// <summary>A repository node's output is node-local data: a cloud LLM call bound to it is refused while local data is withheld, like one fed by a knowledge-base Tool.</summary>
    [Test]
    public async Task ACloudLlmCall_FedByARepositoryNode_IsRefusedWhileLocalDataIsWithheld()
    {
        var model = GraphWorkflowModels.Grant("graph-cloud-repository-consumer", new ExternalProviderCloudGrants
        {
            UnattendedRuns = true
        });
        var projectId = Host.BindProject("repository-sentinel-downstream");
        await using var harness = new GraphWorkflowHarness(Host);
        var agentDefinitionId = await SeedAgentAsync(harness, ["read_file"]);

        var consume = await RunAsync(harness, DownstreamGraph(agentDefinitionId, model), Input(projectId), "consume");

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Succeeded, (await harness.ReadNodeRunAsync(consume.RunId, "analyze")).Status, "the local repository node itself runs.");
        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, consume.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, consume.FailureClass);
        AssertEx.Contains(consume.Error, "Agent node 'analyze' ('repository')");
        AssertEx.Contains(consume.Error, $"to this LLM call on the cloud model '{model}'");
        AssertEx.Empty(Capacity(harness).ReservationsFor(model), "refused before capacity.");
    }

    /// <summary>Development Mode off at startup registers no binding service, which reads as its own refusal.</summary>
    [Test]
    public async Task ARepositoryNode_IsRefusedWhileDevelopmentModeIsOff()
    {
        await using var host = GraphWorkflowRepositoryHostFixture.DevelopmentModeOff();
        await using var harness = new GraphWorkflowHarness(host);
        using (var scope = harness.Services.CreateScope())
        {
            AssertEx.Null(scope.ServiceProvider.GetService<IDevelopmentRepositoryBindingService>(), "the precondition: the module registered nothing.");
        }

        var agentDefinitionId = await SeedAgentAsync(harness, ["read_file"]);

        var analyze = await RunAsync(harness, Graph(agentDefinitionId, """, "repository": true"""), Input(Guid.NewGuid()));

        AssertEx.Equal(GraphWorkflowNodeRunStatus.Failed, analyze.Status);
        AssertEx.Equal(GraphWorkflowFailureClass.ValidationFailed, analyze.FailureClass);
        AssertEx.Contains(analyze.Error, "Development Mode is off on this node");
    }

    [Test]
    public async Task TheRepositoryOption_SurvivesASaveAndReload()
    {
        await using var harness = new GraphWorkflowHarness(Host);
        var definitionId = await harness.SeedDefinitionAsync(Graph(Guid.NewGuid(), """, "repository": true"""));

        await using var scope = harness.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<IGraphWorkflowDefinitionService>().GetAsync(definitionId);

        AssertEx.True(AssertEx.NotNull(GraphWorkflowGraph.Parse(stored.GraphJson).Nodes["analyze"].Config as GraphWorkflowAgentConfig).Repository);
    }

    private static string Graph(Guid agentDefinitionId, string options) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "analyze", "kind": "Agent", "config": { "instructions": "Read notes.txt.", "agentDefinitionId": "{{agentDefinitionId}}"{{options}} } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "analyze" },
              { "key": "e2", "from": "analyze", "to": "done" }
            ]
          }
          """;

    // Start → analyze (repository Agent) → consume (cloud LlmCall bound to analyze's output) → End.
    private static string DownstreamGraph(Guid agentDefinitionId, string model) =>
        $$"""
          {
            "schemaVersion": 1,
            "nodes": [
              { "key": "start", "kind": "Start" },
              { "key": "analyze", "kind": "Agent", "config": { "instructions": "Read notes.txt.", "agentDefinitionId": "{{agentDefinitionId}}", "repository": true } },
              { "key": "consume", "kind": "LlmCall", "config": { "prompt": "Summarize it.", "model": "{{model}}", "inputBindings": { "found": "upstream.analyze" } } },
              { "key": "done", "kind": "End", "config": { "outcome": "completed" } }
            ],
            "edges": [
              { "key": "e1", "from": "start", "to": "analyze" },
              { "key": "e2", "from": "analyze", "to": "consume" },
              { "key": "e3", "from": "consume", "to": "done" }
            ]
          }
          """;

    private static string Input(Guid projectId) =>
        JsonSerializer.Serialize(new
        {
            developmentProjectId = projectId
        });

    private static async Task<GraphWorkflowNodeRunSnapshot> RunAsync(GraphWorkflowHarness harness, string graphJson, string? inputJson, string nodeKey = "analyze")
    {
        var runId = await harness.StartRunAsync(graphJson, inputJson);
        GraphWorkflowNodeRunSnapshot? settled = null;
        await AssertEx.EventuallyAsync(async () =>
            {
                var nodeRun = await harness.ReadNodeRunAsync(runId, nodeKey);
                if (GraphWorkflowStateMachine.IsTerminal(nodeRun.Status))
                {
                    settled = nodeRun;
                    return true;
                }

                _ = await harness.AdvanceAsync(runId);
                return false;
            },
            TestBudgets.Contended,
            $"Run {runId} left its agent node unsettled.");

        return settled!;
    }

    private static async Task<Guid> SeedAgentAsync(GraphWorkflowHarness harness, IReadOnlyList<string> allowedToolNames)
    {
        await using var scope = harness.Services.CreateAsyncScope();
        var definition = await scope.ServiceProvider.GetRequiredService<IAgentDefinitionStore>()
                                    .AddAsync(new AgentDefinitionInput
                                    {
                                        Name = $"Repository agent {Guid.NewGuid():N}",
                                        Description = null,
                                        Instructions = "raw instructions",
                                        ModelProfile = GraphWorkflowModels.LocalDefault,
                                        ReasoningEffort = null,
                                        Kind = AgentDefinitionKind.Single,
                                        AllowedToolNames = allowedToolNames,
                                        ToolApprovals = new Dictionary<string, bool>(StringComparer.Ordinal),
                                        OrchestrationTopologyJson = null
                                    });
        return definition.Id;
    }

    private static string Text(GraphWorkflowNodeRunSnapshot nodeRun)
    {
        using var document = JsonDocument.Parse(AssertEx.NotNull(nodeRun.OutputJson, "a settled agent node always carries its output document."));
        return AssertEx.NotNull(document.RootElement.GetProperty("output").GetProperty("text").GetString());
    }

    private static FakeGraphWorkflowCapacity Capacity(GraphWorkflowHarness harness) =>
        (FakeGraphWorkflowCapacity)harness.Services.GetRequiredService<ICapacityService>();
}

/// <summary>The agent-lane host with the REAL runner over a scripted model offering the host's own <c>read_file</c>; bindings answer from bound projects.</summary>
public sealed class GraphWorkflowRepositoryHostFixture : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>The host path an unsafe binding's exception names, which no refusal may repeat.</summary>
    public const string UnsafePath = "/srv/private/unsafe-repository";

    private readonly ConcurrentDictionary<Guid, string> _projects = new();
    private readonly ConcurrentBag<string> _roots = [];

    public GraphWorkflowRepositoryHostFixture() =>
        Factory = GraphWorkflowAgentHostFixture.NewFactory(Configure);

    private GraphWorkflowRepositoryHostFixture(TestServerWebAppFactory factory) =>
        Factory = factory;

    public TestServerWebAppFactory Factory { get; }

    /// <summary>A host of its own started with Development Mode off, so no binding service is registered; the turn is refused before any model call.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership transfers to the returned fixture, which disposes it.")]
    public static GraphWorkflowRepositoryHostFixture DevelopmentModeOff() =>
        new(new TestServerWebAppFactory
        {
            EnableDevelopmentMode = false,
            AdditionalConfiguration = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GraphWorkflows:Enabled"] = "true"
            },
            ConfigureAdditionalTestServices = static services =>
            {
                services.RemoveAll<IGraphWorkflowDispatcherSignal>();
                services.AddSingleton<IGraphWorkflowDispatcherSignal, RecordingGraphWorkflowDispatcherSignal>();
                services.RemoveAll<ILocalDefaultChatModelResolver>();
                services.AddSingleton<ILocalDefaultChatModelResolver, FakeGraphWorkflowLocalDefaultModel>();
                services.RemoveAll<IModelCapabilityResolver>();
                services.AddSingleton<IModelCapabilityResolver, FakeGraphWorkflowModelCapabilities>();
            }
        });

    /// <summary>Binds a new project to a fresh temporary repository holding <c>notes.txt</c> with <paramref name="notes" />.</summary>
    public Guid BindProject(string notes)
    {
        var root = Directory.CreateTempSubdirectory("xe-graph-repository-").FullName;
        File.WriteAllText(Path.Combine(root, "notes.txt"), notes);
        _roots.Add(root);
        var projectId = Guid.NewGuid();
        _projects[projectId] = root;
        return projectId;
    }

    /// <summary>A project whose binding fails the workspace security check with a message naming <see cref="UnsafePath" />.</summary>
    public Guid BindUnsafeProject()
    {
        var projectId = Guid.NewGuid();
        _projects[projectId] = UnsafePath;
        return projectId;
    }

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        foreach (var root in _roots)
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void Configure(IServiceCollection services)
    {
        services.RemoveAll<IInvocationRunner>();
        services.AddSingleton<IInvocationRunner>(provider => InvocationRunnerTestSupport.CreateRunner(ReadFileAgentFactory(provider),
            lazyEventDispatcher: new Lazy<IWorkerEventDispatcher>(provider.GetRequiredService<IWorkerEventDispatcher>)));

        var bindings = Substitute.For<IDevelopmentRepositoryBindingService>();
        bindings.ResolveProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => Binding(call.ArgAt<Guid>(0)));
        services.RemoveAll<IDevelopmentRepositoryBindingService>();
        services.AddSingleton(bindings);
    }

    private DevelopmentRepositoryBinding Binding(Guid projectId)
    {
        if (!_projects.TryGetValue(projectId, out var root))
        {
            throw new DevelopmentNotFoundException($"Development project '{projectId}' was not found.");
        }

        return root == UnsafePath
            ? throw new DevelopmentWorkspaceSecurityException($"The repository at '{UnsafePath}' is not a safe workspace.")
            : new DevelopmentRepositoryBinding
            {
                ProjectId = projectId,
                SelectedFolderId = Guid.NewGuid(),
                Alias = "repository",
                RepositoryRoot = root,
                RepositoryIdentityHash = "identity"
            };
    }

    private static IInvocationAgentFactory ReadFileAgentFactory(IServiceProvider provider)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   AssertEx.True(provider.GetRequiredService<IClientLocalToolRegistry>().TryResolve("read_file", out var readFile));
                   var chatClient = new ReadNotesChatClient().AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = InvocationAgentFactory.BuildApprovalValidatedAgent(chatClient,
                           "graph-repository",
                           "A graph Agent node reading its bound repository.",
                           [readFile!],
                           NullLoggerFactory.Instance,
                           provider,
                           instructions: definition.Instructions),
                       Session = null,
                       SeedMessages = definition.ConversationContext.Prepend(new ChatMessage(ChatRole.System, definition.Instructions)).ToList()
                   });
               });
        return factory;
    }

    /// <summary>Round one calls <c>read_file</c> on <c>notes.txt</c>; round two answers with the tool's result verbatim.</summary>
    private sealed class ReadNotesChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("The runner streams.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().FirstOrDefault() is { } result)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, Convert.ToString(result.Result, CultureInfo.InvariantCulture))
                {
                    FinishReason = ChatFinishReason.Stop
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent("call-read", "read_file", new Dictionary<string, object?>
            {
                ["path"] = "notes.txt"
            })])
            {
                FinishReason = ChatFinishReason.ToolCalls
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }
}
