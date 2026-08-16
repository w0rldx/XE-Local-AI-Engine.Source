namespace XE_Local_AI_Engine.Tests.Drafting;

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OllamaSharp;
using OllamaSharp.Models;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Drafting;
using XE_Local_AI_Engine.Client.Services.Drafting.Implementation;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The guard rails around AI-assisted drafting: fail-closed model eligibility evaluated BEFORE any provider work, a
///     non-queueing admission gate, an aggregate prompt budget, and post-parse normalization that trusts nothing the
///     model asserted. No live model, Ollama daemon or llama-server is involved — the chat client is a fake.
/// </summary>
public sealed class DefaultConfigDraftServiceTests
{
    private const string LlamaModel = "qwen3-8b:Q4_K_M";
    private static readonly DateTimeOffset DraftedAt = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task DraftAgent_WithBrief_ReturnsNormalizedDraft()
    {
        var harness = new Harness
        {
            EnvelopeJson = $$"""
                             { "name": "  Release Notes Writer  ", "description": " Writes release notes. ",
                               "instructions": "{{new string('i', 20050)}}", "rationale": "  Kept it short.  ",
                               "assumptions": ["  Markdown output  ", "   "], "confidence": 0.8 }
                             """
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent that writes release notes."))
                                  .ConfigureAwait(false);

        var draft = AssertEx.NotNull(result.Draft, "A parseable envelope must produce a draft.");
        AssertEx.Equal("Release Notes Writer", draft.Name);
        AssertEx.Equal("Writes release notes.", draft.Description);
        AssertEx.Equal(expected: 20000, draft.Content.Length, "Instructions must be clamped to the entity cap.");
        AssertEx.Equal("Kept it short.", draft.Rationale);
        AssertEx.Equal(expected: 1, draft.Assumptions.Count, "Blank assumptions must be dropped.");
        AssertEx.Equal("Markdown output", draft.Assumptions[0]);
        AssertEx.Equal(DraftedAt, draft.GeneratedAtUtc, "The draft timestamp is server-stamped from TimeProvider.");
        AssertEx.Equal(DraftContentHash.Compute(draft.Name, draft.Description, draft.Content),
            draft.ContentHash,
            "The stamped hash must be the canonical hash over the NORMALIZED draft content.");
        AssertEx.True(harness.ChatClient.WasCalled, "The draft must run on the node-local provider's client.");
        AssertEx.True(harness.ChatClient.IsDisposed, "The per-draft chat client must be disposed.");
    }

    [Test]
    public async Task DraftAgent_UnparseableModelOutput_ReturnsTypedFailure()
    {
        const string RawModelText = "I'm sorry, I cannot help with that request.";
        var harness = new Harness
        {
            EnvelopeJson = RawModelText
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.Unparseable, result.Failure);
        AssertEx.Null(result.Draft);
        AssertEx.False(result.FailureMessage?.Contains(RawModelText, StringComparison.Ordinal) ?? false,
            "A failure result must never carry raw model text.");
    }

    [Test]
    public async Task DraftAgent_EmptyInstructions_ReturnsTypedFailure()
    {
        var harness = new Harness
        {
            EnvelopeJson = """
                           { "name": "Empty", "description": "Nothing.", "instructions": "   ",
                             "rationale": null, "assumptions": [], "confidence": 0.5 }
                           """
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.Unparseable, result.Failure);
    }

    [Test]
    public async Task DraftAgent_TimeoutBudgetElapsed_ReturnsTypedFailure_GateReleased()
    {
        var harness = new Harness
        {
            HangUntilCancelled = true,
            Options = new DraftingOptions
            {
                GenerationTimeout = TimeSpan.FromMilliseconds(50)
            }
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.Unparseable, result.Failure);
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "The admission gate must be released after a timeout.");
        lease?.Dispose();
    }

    [Test]
    public async Task DraftAgent_WithNoExplicitOption_UsesTheNodeMaximumMessageRequestTimeout()
    {
        // G14: the drafting budget was a hardcoded 300s, so a raised node "Maximum message request timeout" was
        // silently ignored here. With no explicit Drafting:GenerationTimeout the budget now follows the node setting,
        // read live. A 0s setting makes the hang elapse immediately — it would run to the model's completion (and the
        // assert would see no failure) if the wiring were dropped back to a fixed default.
        var harness = new Harness
        {
            HangUntilCancelled = true,
            NodeMessageRequestTimeoutSeconds = 0
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.Unparseable, result.Failure);
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "The admission gate must be released after a timeout.");
        lease?.Dispose();
    }

    [Test]
    public async Task DraftAgent_ResolverStallElapsesBudget_ReturnsTypedFailure_GateReleased()
    {
        // The provider-map lookup takes the linked timeout token too; a stall there must surface as the same typed
        // failure as a stalled generation, not escape as an unhandled OperationCanceledException.
        var harness = new Harness
        {
            ResolverHangsUntilCancelled = true,
            Options = new DraftingOptions
            {
                GenerationTimeout = TimeSpan.FromMilliseconds(50)
            }
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.Unparseable, result.Failure);
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "The admission gate must be released after a resolver stall.");
        lease?.Dispose();
    }

    [Test]
    public async Task DraftSkill_InvalidMafName_SlugFallbackValidates()
    {
        var harness = new Harness
        {
            EnvelopeJson = """
                           { "name": "My  Awesome Skill!! ", "description": "Reviews code.",
                             "body": "## What it does\nReviews code.", "rationale": null,
                             "assumptions": [], "confidence": 0.6 }
                           """
        };

        var result = await harness.Service.DraftSkillAsync(Request("Draft a code review skill.")).ConfigureAwait(false);

        var draft = AssertEx.NotNull(result.Draft, "The skill draft must survive an invalid model-asserted name.");
        AssertEx.Equal("my-awesome-skill", draft.Name);
#pragma warning disable MAAI001 // The same experimental validator the skill service validates against.
        AssertEx.True(AgentSkillFrontmatter.ValidateName(draft.Name, out _), "The normalized name must pass MAF validation.");
#pragma warning restore MAAI001
    }

    [Test]
    public async Task DraftService_HostileEnvelope_MetadataCapped()
    {
        var harness = new Harness
        {
            EnvelopeJson = $$"""
                             { "name": "Agent", "description": "Does things.", "instructions": "Do things.",
                               "rationale": "{{new string('r', 4000)}}",
                               "assumptions": [{{string.Join(",", Enumerable.Range(0, 25).Select(_ => $"\"{new string('a', 900)}\""))}}],
                               "confidence": 42.5 }
                             """
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        var draft = AssertEx.NotNull(result.Draft, "A hostile envelope is capped, not rejected.");
        AssertEx.Equal(expected: 2000, AssertEx.NotNull(draft.Rationale).Length, "Rationale must be capped.");
        AssertEx.Equal(expected: 10, draft.Assumptions.Count, "At most ten assumptions survive.");
        AssertEx.Equal(expected: 300, draft.Assumptions[0].Length, "Each assumption must be capped.");
        AssertEx.Equal(expected: 1d, draft.Confidence, "Confidence must be clamped into [0,1].");
    }

    [Test]
    public async Task DraftService_NegativeConfidence_ClampsToZero()
    {
        var harness = new Harness
        {
            EnvelopeJson = """
                           { "name": "Agent", "description": "Does things.", "instructions": "Do things.",
                             "rationale": null, "assumptions": [], "confidence": -3 }
                           """
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal(expected: 0d, AssertEx.NotNull(result.Draft).Confidence);
    }

    [Test]
    public async Task DraftAgent_AggregateBudgetExceeded_ReturnsInvalidRequest_BeforeGate()
    {
        var harness = new Harness
        {
            Options = new DraftingOptions
            {
                MaxPromptChars = 100
            }
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(
                                      Request(new string('b', 101), DraftMode.Improve, existingContent: "current instructions"))
                                  .ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.InvalidRequest, result.Failure);
        await harness.Resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "The budget check must run BEFORE the gate is acquired.");
        lease?.Dispose();
    }

    [Test]
    public async Task DraftAgent_MissingBrief_ReturnsInvalidRequest()
    {
        var harness = new Harness();

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("   ")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.InvalidRequest, result.Failure);
    }

    [Test]
    [Arguments("unclassified")]
    [Arguments("embedding")]
    [Arguments("not-installed")]
    [Arguments("remote-ollama")]
    public async Task Eligibility_UnknownUnclassifiedCloudOrRemoteOllama_RejectsBeforeResolver(string scenario)
    {
        var harness = new Harness();
        switch (scenario)
        {
            case "unclassified":
                // No classification row at all — the case a chat picker would let through, and drafting must not.
                harness.Classifications.GetByNameAsync(LlamaModel, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ModelClassificationRecord?>(null));
                break;
            case "embedding":
                harness.Classifications.GetByNameAsync(LlamaModel, Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<ModelClassificationRecord?>(Classification(ModelKind.Embedding)));
                break;
            case "not-installed":
                // Classified Chat, but present in no local inventory (a cloud or otherwise remote model name).
                harness.GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([]));
                break;
            case "remote-ollama":
                harness.GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                       .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([]));
                harness.UseOllama(new Uri("http://198.51.100.7:11434"), LlamaModel);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.ModelNotEligible, result.Failure);
        await harness.Resolver.DidNotReceive().ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        harness.Provider.DidNotReceive().CreateChatClient(Arg.Any<LocalModelSelection>());

        // Read-only: the eligibility path must never write to the classification cache.
        await harness.Classifications.DidNotReceive()
                     .UpsertDetectedAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<ModelKind>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
        await harness.Classifications.DidNotReceive()
                     .SetOverrideAsync(Arg.Any<string>(), Arg.Any<ModelKind?>(), Arg.Any<CancellationToken>())
                     .ConfigureAwait(false);
    }

    [Test]
    public async Task Eligibility_Llamacpp_Passes()
    {
        var harness = new Harness();

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.True(result.Succeeded, "An installed, chat-classified GGUF model is eligible.");
        harness.Provider.Received(1).CreateChatClient(Arg.Is<LocalModelSelection>(selection =>
            selection.ModelName == LlamaModel && selection.ProviderName == "llamacpp"));
    }

    [Test]
    public async Task Eligibility_LoopbackOllama_Passes()
    {
        var harness = new Harness
        {
            ProviderName = "ollama"
        };
        harness.GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
               .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>([]));
        harness.UseOllama(new Uri("http://127.0.0.1:11434"), LlamaModel);

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.True(result.Succeeded, "A model installed in a LOOPBACK Ollama is eligible.");
    }

    [Test]
    public async Task Eligibility_ProviderRouteDisagreesWithAllowlist_Rejects()
    {
        // Installed as a GGUF (so eligibility clears llamacpp), but the persisted map routes it elsewhere: the resolver
        // is not a guard, so the mismatch must refuse instead of generating on an unvetted route.
        var harness = new Harness
        {
            ProviderName = "ollama"
        };

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.ModelNotEligible, result.Failure);
        harness.Provider.DidNotReceive().CreateChatClient(Arg.Any<LocalModelSelection>());
    }

    [Test]
    public async Task DraftGate_ActiveInvocation_Returns409_NoModelCall()
    {
        var harness = new Harness();
        harness.WorkerEventDispatcher.CurrentInvocation.Returns(new InvocationState());

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.NodeBusy, result.Failure);
        harness.Provider.DidNotReceive().CreateChatClient(Arg.Any<LocalModelSelection>());
    }

    [Test]
    public async Task DraftGate_ActiveInvocationCount_Returns409_NoModelCall()
    {
        var harness = new Harness();
        harness.InvocationRunner.ActiveInvocationCount.Returns(1);

        var result = await harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent.")).ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.NodeBusy, result.Failure);
        harness.Provider.DidNotReceive().CreateChatClient(Arg.Any<LocalModelSelection>());
    }

    [Test]
    public async Task DraftGate_ConcurrentDraft_Returns409_WhileFirstInFlight()
    {
        using var firstDraftStarted = new SemaphoreSlim(0, 1);
        using var releaseFirstDraft = new SemaphoreSlim(0, 1);
        var harness = new Harness
        {
            BeforeResponse = async () =>
            {
                _ = firstDraftStarted.Release();
                _ = await releaseFirstDraft.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        };

        var firstDraft = harness.Service.DraftAgentDefinitionAsync(Request("Draft an agent."));
        AssertEx.True(await firstDraftStarted.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false),
            "The first draft must reach the model before the second is attempted.");

        var secondResult = await harness.Service.DraftAgentDefinitionAsync(Request("Draft another agent.")).ConfigureAwait(false);
        _ = releaseFirstDraft.Release();
        var firstResult = await firstDraft.ConfigureAwait(false);

        AssertEx.Equal<DraftFailureKind?>(DraftFailureKind.NodeBusy, secondResult.Failure);
        AssertEx.True(firstResult.Succeeded, "The draft holding the slot must still complete.");

        // The slot is handed back exactly once, so a later draft can take it.
        AssertEx.True(harness.Gate.TryAcquire(out var lease), "The slot must be free once the first draft finishes.");
        lease?.Dispose();
    }

    [Test]
    public void DraftEnvelopes_JsonSchema_HasNoLengthOrCountBounds()
    {
        // Invariant 3: nothing sanitizes the ResponseFormat schema on the way to llama-server's grammar compiler
        // (ApplyToolSchemaCompatibility covers options.Tools only), so a maxLength/maxItems on an envelope would reach
        // the grammar compiler unfiltered and fail sampler init.
        foreach (var envelopeType in new[] { typeof(AgentDraftEnvelope), typeof(SkillDraftEnvelope) })
        {
            var schema = AIJsonUtilities.CreateJsonSchema(envelopeType).GetRawText();

            // Guard the guard: an empty/degenerate schema would make the bound assertions below vacuous.
            AssertEx.Contains(schema, "assumptions", StringComparison.OrdinalIgnoreCase,
                $"{envelopeType.Name} must derive a property-bearing schema.");

            foreach (var bound in new[] { "maxLength", "minLength", "maxItems", "minItems" })
            {
                AssertEx.False(schema.Contains(bound, StringComparison.OrdinalIgnoreCase),
                    $"{envelopeType.Name} schema must carry no '{bound}' bound: {schema}");
            }
        }
    }

    private static ConfigDraftRequest Request(string brief, DraftMode mode = DraftMode.Create, string? existingContent = null)
    {
        return new ConfigDraftRequest(mode, LlamaModel, brief, ExistingContent: existingContent);
    }

    private static ModelClassificationRecord Classification(ModelKind kind)
    {
        return new ModelClassificationRecord(LlamaModel,
            Digest: null,
            kind,
            DetectedCapabilitiesJson: null,
            OverrideKind: null,
            DetectedAtUtc: null,
            UpdatedAtUtc: 0);
    }

    /// <summary>
    ///     A node with one installed, Chat-classified GGUF served by llama.cpp, an idle invocation runner and a fake chat
    ///     client returning <see cref="EnvelopeJson" />. The inventory/classification baselines are stubbed in the
    ///     constructor so a test can override them before the service is built on first <see cref="Service" /> access.
    /// </summary>
    private sealed class Harness
    {
        private DefaultConfigDraftService? _service;

        public Harness()
        {
            Classifications.GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                           .Returns(Task.FromResult<ModelClassificationRecord?>(Classification(ModelKind.Chat)));
            GgufModelStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                          .Returns(Task.FromResult<IReadOnlyList<LocalModelDescriptor>>(
                          [
                              new LocalModelDescriptor
                              {
                                  ModelName = LlamaModel,
                                  ProviderName = "llamacpp",
                                  IsAvailable = true,
                                  SizeBytes = 1024,
                                  ModifiedAt = DraftedAt,
                                  MaxContextTokens = 8192
                              }
                          ]));
        }

        public IModelClassificationStore Classifications { get; } = Substitute.For<IModelClassificationStore>();

        public IGgufModelStore GgufModelStore { get; } = Substitute.For<IGgufModelStore>();

        public IWorkerEventDispatcher WorkerEventDispatcher { get; } = Substitute.For<IWorkerEventDispatcher>();

        public IInvocationRunner InvocationRunner { get; } = Substitute.For<IInvocationRunner>();

        public ILocalModelProviderResolver Resolver { get; } = Substitute.For<ILocalModelProviderResolver>();

        public ILocalModelProvider Provider { get; } = Substitute.For<ILocalModelProvider>();

        public DraftAdmissionGate Gate { get; private set; } = null!;

        public EnvelopeChatClient ChatClient { get; private set; } = null!;

        public string ProviderName { get; init; } = "llamacpp";

        public string EnvelopeJson { get; init; } = """
                                                    { "name": "Agent", "description": "Does things.",
                                                      "instructions": "Do things.", "body": "## Does things",
                                                      "rationale": null, "assumptions": [], "confidence": 0.7 }
                                                    """;

        public bool HangUntilCancelled { get; init; }

        public bool ResolverHangsUntilCancelled { get; init; }

        public Func<Task>? BeforeResponse { get; init; }

        public DraftingOptions Options { get; init; } = new();

        /// <summary>The node-level "Maximum message request timeout" the drafting budget follows when Options sets none.</summary>
        public int NodeMessageRequestTimeoutSeconds { get; init; } = StoredNodeSettings.DefaultMaxMessageRequestTimeoutSeconds;

        public DefaultConfigDraftService Service => _service ??= Build();

        private IOllamaApiClient? OllamaApiClient { get; set; }

        public void UseOllama(Uri endpoint, params string[] installedModelNames)
        {
            var client = Substitute.For<IOllamaApiClient>();
            client.Uri.Returns(endpoint);
            client.ListLocalModelsAsync(Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult<IEnumerable<Model>>([
                      .. installedModelNames.Select(static name => new Model
                      {
                          Name = name
                      })
                  ]));
            OllamaApiClient = client;
        }

        private DefaultConfigDraftService Build()
        {
            Provider.ProviderName.Returns(ProviderName);
            ChatClient = new EnvelopeChatClient(EnvelopeJson, HangUntilCancelled, BeforeResponse);
            Provider.CreateChatClient(Arg.Any<LocalModelSelection>()).Returns(ChatClient);
            if (ResolverHangsUntilCancelled)
            {
                // A provider-map read that never returns: the linked generation timeout is the only thing that ends it.
                Resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                        .Returns(async callInfo =>
                        {
                            await Task.Delay(Timeout.Infinite, callInfo.Arg<CancellationToken>()).ConfigureAwait(false);
                            return Provider;
                        });
            }
            else
            {
                Resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(Provider));
            }

            Gate = new DraftAdmissionGate(WorkerEventDispatcher, InvocationRunner);

            var nodeSettings = Substitute.For<INodeSettingsStore>();
            nodeSettings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
            {
                MaxMessageRequestTimeoutSeconds = NodeMessageRequestTimeoutSeconds
            });

            return new DefaultConfigDraftService(Resolver,
                GgufModelStore,
                Classifications,
                Gate,
                Microsoft.Extensions.Options.Options.Create(Options),
                nodeSettings,
                new FixedTimeProvider(DraftedAt),
                NullLogger<DefaultConfigDraftService>.Instance,
                OllamaApiClient);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
        }
    }

    /// <summary>
    ///     Minimal node-local <see cref="IChatClient" /> stand-in: returns a fixed JSON envelope so
    ///     <c>GetResponseAsync&lt;T&gt;</c> parses a structured result without a live model, or hangs until cancelled so
    ///     the generation-budget path can be exercised.
    /// </summary>
    private sealed class EnvelopeChatClient(string json, bool hangUntilCancelled, Func<Task>? beforeResponse) : IChatClient
    {
        public bool WasCalled { get; private set; }

        public bool IsDisposed { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;

            if (beforeResponse is not null)
            {
                await beforeResponse().ConfigureAwait(false);
            }

            if (hangUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, json));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            // Drafting is non-streaming; an empty stream suffices.
            await Task.CompletedTask.ConfigureAwait(false);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceType.IsInstanceOfType(this) && serviceKey is null ? this : null;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
