namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows.Implementation;
using XE_Local_AI_Engine.Client.Services.Tools;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The response-schema warning is about the <c>Microsoft.Extensions.AI.OpenAI</c> strict-schema rewrite, and the
///     llama.cpp lane does not suffer it — <c>DeferredLlamaServerChatClient.ApplyResponseSchemaPassthrough</c>
///     writes the authored schema onto the request body, which the adapter then leaves alone. So a node pinned to a
///     model llama-server serves must stop being told its bounds are dropped, while every other node must keep hearing
///     it. The parser cannot make that call — it has no route to the model-to-provider map — so the definition service
///     does, and this is the class that pins where the line falls.
/// </summary>
/// <remarks>
///     The service is built directly rather than resolved from a host: <c>ValidateAsync</c> touches neither the store
///     nor the tool catalog for a graph with no <c>Tool</c> node, and the one seam that matters is the provider
///     resolver, which has no real form outside a fully composed node. The PARSER's own warning set is unchanged and
///     stays pinned in <see cref="GraphWorkflowGraphTests" />.
/// </remarks>
[Category(TestCategories.Unit)]
public sealed class GraphWorkflowResponseSchemaWarningTests
{
    private const string LocalModel = "qwen3:8b";

    private const string CloudRoutedModel = "some-other-model";

    [Test]
    public async Task ValidateAsync_WhenTheNodesModelIsServedByLlamaServer_DropsTheResponseSchemaWarning()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName, installed: true);

        var result = await service.ValidateAsync(AgentGraph(LocalModel));

        AssertEx.True(result.IsValid, "the warning was never blocking, and narrowing it must not change that.");
        AssertEx.Empty(result.Warnings,
            "llama-server now receives the schema as authored, so telling the author their maxLength is dropped would be false.");
    }

    [Test]
    public async Task ValidateAsync_WhenTheNodesModelIsServedByAnotherProvider_KeepsTheResponseSchemaWarning()
    {
        var service = BuildService(CloudRoutedModel, "ollama");

        var result = await service.ValidateAsync(AgentGraph(CloudRoutedModel));

        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"one node, one warning: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("agent", warning.Key);
        AssertEx.Contains(warning.Message, "maxLength", message: "the rewrite still happens on every runtime but llama.cpp.");
    }

    /// <summary>
    ///     A node with no model pin inherits whatever the agent definition or the node default resolves to at RUN start,
    ///     which is not known here. Keeping the warning is the cheap error: a warning nobody needed costs an author one
    ///     read, a silence they did need costs them a bound they believed.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WhenTheNodePinsNoModel_KeepsTheResponseSchemaWarning()
    {
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(_ => Task.FromResult(LlamaServerProviderConstants.ProviderName));
        var service = BuildService(providers);

        var result = await service.ValidateAsync(AgentGraph(model: null));

        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"one node, one warning: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("agent", warning.Key);
        await providers.DidNotReceive().ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The lookup reads the store through a fresh scope and a map read lease, so it can fail for reasons that have
    ///     nothing to do with the graph. Validation is warning-only and never blocked before: a store fault must not
    ///     turn the editor's probe into a 500, and the warning it could not rule out stays.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WhenTheProviderLookupFaults_KeepsTheWarningAndDoesNotThrow()
    {
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(LocalModel, Arg.Any<CancellationToken>())
                 .Returns<Task<string>>(_ => throw new InvalidOperationException("the model-provider map is unavailable"));
        var service = BuildService(providers);

        var result = await service.ValidateAsync(AgentGraph(LocalModel));

        AssertEx.True(result.IsValid, "an infrastructure fault in a warning lookup must not make the graph invalid either.");
        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"one node, one warning: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("agent", warning.Key);
    }

    /// <summary>
    ///     The same catch, reached by the route an author can actually take. A model pin carrying an internal newline is
    ///     well-formed JSON and the parser has no rule against it, so it reaches the resolver — where the map read
    ///     lease's key normalisation rejects it. Nothing about that is the graph's fault, and it must not 500 the
    ///     editor's probe.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WhenTheModelPinIsMalformed_KeepsTheWarningAndDoesNotThrow()
    {
        const string MalformedPin = "foo\nbar";
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(MalformedPin, Arg.Any<CancellationToken>())
                 .Returns<Task<string>>(_ => throw new ArgumentException("model name contains a line break"));
        var service = BuildService(providers);

        // Escaped in the document, so what the parser hands the resolver is the real newline the stub is keyed on.
        var result = await service.ValidateAsync(AgentGraph(@"foo\nbar"));

        AssertEx.True(result.IsValid);
        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"one node, one warning: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("agent", warning.Key);
        // Reached by the schema filter and again by the Agent pin's installed check; both swallow the fault.
        await providers.Received().ResolveProviderNameForModelAsync(MalformedPin, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     The OTHER warning kind travels the same list, and narrowing must not take it with it. This graph's successor
    ///     is reached only through a Pause, so it earns the pause-context warning while its Agent node's schema warning
    ///     is dropped for llama-server — proving the filter is per-warning rather than per-node or wholesale.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WithALocalSchemaNodeAndAPauseSuccessor_KeepsOnlyThePauseWarning()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName, installed: true);

        var result = await service.ValidateAsync(PauseSuccessorGraph(LocalModel));

        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"the pause warning and nothing else: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("after", warning.Key, "the pause-context warning is keyed on the node that loses the approved content.");
        AssertEx.Contains(warning.Message, "Pause", message: "the surviving warning must be the pause one, not a reworded schema one.");
    }

    /// <summary>
    ///     An LLM call pinned to a model the run would refuse as not installed must say so at validate, naming node and
    ///     model, and still validate: the warning channel never blocks a save.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WhenAnLlmCallPinsAnUninstalledModel_WarnsNamingTheNodeAndTheModel()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName);

        var result = await service.ValidateAsync(LlmCallGraph(LocalModel));

        AssertEx.True(result.IsValid, "a missing model is a warning, never an error.");
        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"one node, one warning: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("analyze", warning.Key);
        AssertEx.Contains(warning.Message, $"'{LocalModel}'", message: "the author has to know which pin to fix.");
    }

    /// <summary>The same pin to a model the runtime gate accepts says nothing, so the warning means what the run will do.</summary>
    [Test]
    public async Task ValidateAsync_WhenAnLlmCallPinsAnInstalledLocalModel_DoesNotWarn()
    {
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(LocalModel, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(LlamaServerProviderConstants.ProviderName));
        var ggufModels = Substitute.For<IGgufModelStore>();
        ggufModels.ExistsAsync(LocalModel, Arg.Any<CancellationToken>()).Returns(true);
        var service = BuildService(providers, ggufModels);

        var result = await service.ValidateAsync(LlmCallGraph(LocalModel));

        AssertEx.Empty(result.Warnings);
    }

    /// <summary>
    ///     A cloud pin mirrors the executor's admission, not the installed-GGUF gate: while unattended cloud runs are off
    ///     the warning names the switch; once on, the run would proceed, so validation says nothing.
    /// </summary>
    [Test]
    [Arguments(ModelTrustLocality.Cloud)]
    [Arguments(ModelTrustLocality.Unresolved)]
    public async Task ValidateAsync_WhenAnLlmCallPinsACloudModel_WarnsAboutTheSwitchOnlyWhileUnattendedCloudRunsAreOff(ModelTrustLocality locality)
    {
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(CloudRoutedModel, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult("external"));

        var off = await BuildService(providers, locality: locality).ValidateAsync(LlmCallGraph(CloudRoutedModel));
        var on = await BuildService(providers, locality: locality, allowCloudUnattendedRuns: true).ValidateAsync(LlmCallGraph(CloudRoutedModel));

        AssertEx.True(off.IsValid, "a cloud pin is a warning, never an error.");
        var warning = AssertEx.NotNull(off.Warnings.SingleOrDefault(warning => warning.Key == "analyze"), $"one switch warning expected: {string.Join(" | ", off.Warnings)}");
        AssertEx.Contains(warning.Message, "Let cloud models run unattended", message: "the warning names the switch that would admit the node, not a GGUF to install.");
        AssertEx.Contains(warning.Message, "grant unattended runs on the model's external connection", message: "the warning names the connection grant as the alternative.");
        AssertEx.Empty(on.Warnings.Where(warning => warning.Key == "analyze" && !warning.Message.Contains("maxLength", StringComparison.Ordinal)),
            "with the switch on the run would proceed, so validation must not call the node unrunnable.");
    }

    [Test]
    public async Task ValidateAsync_WhenAnLlmCallPinsACloudModelWhoseConnectionGrantsUnattendedRuns_DoesNotWarnWithTheSwitchOff()
    {
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(CloudRoutedModel, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult("external"));

        var result = await BuildService(providers, locality: ModelTrustLocality.Cloud, grants: new ExternalProviderCloudGrants
            {
                UnattendedRuns = true
            })
            .ValidateAsync(LlmCallGraph(CloudRoutedModel));

        AssertEx.Empty(result.Warnings.Where(warning => warning.Key == "analyze" && !warning.Message.Contains("maxLength", StringComparison.Ordinal)),
            "the executor admits the node under the grant, so validation must not call it unrunnable.");
    }

    /// <summary>
    ///     The tester's case: an invalid graph used to throw before any warning ran, so the editor saw the error and never learnt the
    ///     pinned model was missing too. Validate reports both lists; it still refuses the graph.
    /// </summary>
    [Test]
    public async Task ValidateAsync_WhenTheGraphHasErrorsAndAnUninstalledPin_ReportsTheErrorsAndTheWarning()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName);
        // A per-node error (joinPolicy 'Any' on a node with one inbound edge), so the graph still parses and has an LLM call to warn about.
        var graph = LlmCallGraph(LocalModel).Replace("\"kind\": \"LlmCall\",", "\"kind\": \"LlmCall\", \"joinPolicy\": \"Any\",", StringComparison.Ordinal);

        var result = await service.ValidateAsync(graph);

        AssertEx.False(result.IsValid);
        AssertEx.Contains(result.Errors, error => error.Key == "analyze" && error.Message.Contains("joinPolicy", StringComparison.Ordinal));
        AssertEx.Contains(result.Warnings, warning => warning.Key == "analyze" && warning.Message.Contains($"'{LocalModel}'", StringComparison.Ordinal),
            $"the missing-model warning must ride out beside the errors: {string.Join(" | ", result.Warnings)}");
    }

    [Test]
    public async Task ValidateAsync_WhenAnAgentPinsAnUninstalledLlamaServerModel_WarnsNamingTheNodeAndTheModel()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName);

        var result = await service.ValidateAsync(PlainAgentGraph(LocalModel));

        AssertEx.True(result.IsValid, "a missing model is a warning, never an error.");
        var warning = AssertEx.NotNull(result.Warnings.SingleOrDefault(), $"one node, one warning: {string.Join(" | ", result.Warnings)}");
        AssertEx.Equal("agent", warning.Key);
        AssertEx.Contains(warning.Message, $"'{LocalModel}'");
    }

    [Test]
    public async Task ValidateAsync_WhenAnAgentPinsAnInstalledLlamaServerModel_DoesNotWarn()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName, installed: true);

        var result = await service.ValidateAsync(PlainAgentGraph(LocalModel));

        AssertEx.Empty(result.Warnings);
    }

    /// <summary>An Ollama model is no GGUF in this node's registry, yet the agent run serves it, so it must not be called missing.</summary>
    [Test]
    public async Task ValidateAsync_WhenAnAgentPinsAnOllamaModel_DoesNotWarn()
    {
        var service = BuildService(LocalModel, "ollama");

        var result = await service.ValidateAsync(PlainAgentGraph(LocalModel));

        AssertEx.Empty(result.Warnings);
    }

    /// <summary>An unpinned LLM call runs the node default, unknown here, so there is nothing to warn about.</summary>
    [Test]
    public async Task ValidateAsync_WhenAnLlmCallPinsNoModel_DoesNotWarn()
    {
        var service = BuildService(LocalModel, LlamaServerProviderConstants.ProviderName);

        var result = await service.ValidateAsync(LlmCallGraph(model: null));

        AssertEx.Empty(result.Warnings);
    }

    private static IGraphWorkflowDefinitionService BuildService(string model, string providerName, bool installed = false)
    {
        var providers = Substitute.For<ILocalModelProviderResolver>();
        providers.ResolveProviderNameForModelAsync(model, Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(providerName));
        var ggufModels = Substitute.For<IGgufModelStore>();
        ggufModels.ExistsAsync(model, Arg.Any<CancellationToken>()).Returns(installed);
        return BuildService(providers, ggufModels);
    }

    // The registry is empty unless a test says otherwise.
    private static IGraphWorkflowDefinitionService BuildService(ILocalModelProviderResolver providers,
        IGgufModelStore? ggufModels = null,
        ModelTrustLocality locality = ModelTrustLocality.Local,
        bool allowCloudUnattendedRuns = false,
        ExternalProviderCloudGrants? grants = null)
    {
        var trust = Substitute.For<IModelTrustResolver>();
        trust.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(locality);
        trust.ResolveCloudGrantsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(grants ?? ExternalProviderCloudGrants.None);
        return new GraphWorkflowDefinitionService(Substitute.For<IGraphWorkflowStore>(),
            Substitute.For<IToolInvocationService>(), StubNodeRuntimeSettings.Create().WithAllowCloudModelUnattendedRuns(allowCloudUnattendedRuns).Build(),
            providers,
            ggufModels ?? Substitute.For<IGgufModelStore>(),
            trust,
            Options.Create(new GraphWorkflowOptions()));
    }

    private static string LlmCallGraph(string? model)
    {
        var pin = model is null ? string.Empty : $""", "model": "{model}" """;
        return $$"""
                 { "schemaVersion": 1,
                   "nodes": [{ "key": "start", "kind": "Start" },
                             { "key": "analyze", "kind": "LlmCall", "config": { "prompt": "Summarize it."{{pin}} } },
                             { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                   "edges": [{ "key": "e1", "from": "start", "to": "analyze" }, { "key": "e2", "from": "analyze", "to": "done" }] }
                 """;
    }

    // One Agent node with no response schema, so the only warning it can earn is about its pin.
    private static string PlainAgentGraph(string model) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "agent", "kind": "Agent", "config": { "model": "{{model}}", "instructions": "Judge it." } },
                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "agent" }, { "key": "e2", "from": "agent", "to": "done" }] }
          """;

    // One Agent node whose schema earns the warning on every count: a relocated keyword, an optional property and an
    // object the runtime would close.
    private static string AgentGraph(string? model)
    {
        var pin = model is null ? string.Empty : $""" "model": "{model}", """;
        return $$"""
                 { "schemaVersion": 1,
                   "nodes": [{ "key": "start", "kind": "Start" },
                             { "key": "agent", "kind": "Agent",
                               "config": { {{pin}} "instructions": "Judge it.",
                                           "responseJsonSchema": { "type": "object",
                                                                   "properties": { "summary": { "type": "string", "maxLength": 3 },
                                                                                   "notes": { "type": "string" } } } } },
                             { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
                   "edges": [{ "key": "e1", "from": "start", "to": "agent" }, { "key": "e2", "from": "agent", "to": "done" }] }
                 """;
    }

    // The same Agent node, plus a Pause whose only successor therefore receives the decision document rather than the
    // approved content — the second warning kind, keyed on 'after'.
    private static string PauseSuccessorGraph(string model) =>
        $$"""
          { "schemaVersion": 1,
            "nodes": [{ "key": "start", "kind": "Start" },
                      { "key": "agent", "kind": "Agent",
                        "config": { "model": "{{model}}", "instructions": "Judge it.",
                                    "responseJsonSchema": { "type": "object",
                                                            "properties": { "summary": { "type": "string", "maxLength": 3 },
                                                                            "notes": { "type": "string" } } } } },
                      { "key": "gate", "kind": "Pause",
                        "config": { "prompt": "Approve?", "allowedDecisions": ["Approve"], "requireComment": false } },
                      { "key": "after", "kind": "Agent", "config": { "instructions": "Carry on." } },
                      { "key": "done", "kind": "End", "config": { "outcome": "completed" } }],
            "edges": [{ "key": "e1", "from": "start", "to": "agent" },
                      { "key": "e2", "from": "agent", "to": "gate" },
                      { "key": "e3", "from": "gate", "to": "after",
                        "condition": { "path": "output.decision", "op": "Eq", "value": "Approve" } },
                      { "key": "e4", "from": "after", "to": "done" }] }
          """;
}
