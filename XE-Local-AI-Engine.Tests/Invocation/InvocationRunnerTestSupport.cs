namespace XE_Local_AI_Engine.Tests.Invocation;

using System.ClientModel.Primitives;
using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using OpenAI.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Implementation;
using XE_Local_AI_Engine.AI.Agent.Invocation.Orchestration;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Common.Telemetry;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Agents.Approval;
using XE_Local_AI_Engine.Client.Services.Agents.Approval.Implementation;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.Capacity.Implementation;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Interaction;
using XE_Local_AI_Engine.Client.Services.Interaction.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;
using XE_Local_AI_Engine.Client.Services.Invocation.Context;
using XE_Local_AI_Engine.Client.Services.Invocation.Dispatch;
using XE_Local_AI_Engine.Client.Services.Invocation.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation.Resilience;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.WebAccess;
using XE_Local_AI_Engine.Client.Services.WebAccess.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Implementation;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Providers.OpenAICompat;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;
using XE_Local_AI_Engine.Tests.Testing.Mocks;
using ChatFinishReason = Microsoft.Extensions.AI.ChatFinishReason;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

/// <summary>Runner construction, scripted provider streams and fakes shared by the InvocationRunner test classes.</summary>
internal static class InvocationRunnerTestSupport
{
    internal const string ReadSkillResourceToolName = AgentSkillsProvider.ReadSkillResourceToolName;

    internal const string SkillName = "demo";

    internal const string CloudModel = "cloud-model";

    // A running turn on the manual clock holds two timers: the stream-idle deadline of the current pull and the whole-turn deadline.
    internal const int IdleAndTurnDeadline = 2;

    // A turn deadline the idle-window stepping in AdvanceIdleWindowsUntilCompleteAsync cannot reach: the default 300 s is five 61 s steps away.
    internal const int IdleOnlyTurnSeconds = 3600;

    // MAF's skill-tool names, aliased once for the use sites below.
    internal const string LoadSkillToolName = AgentSkillsProvider.LoadSkillToolName;

    internal const string RunSkillScriptToolName = AgentSkillsProvider.RunSkillScriptToolName;

    internal static readonly JsonSerializerOptions AskUserArgumentOptions = new(JsonSerializerDefaults.Web);

    internal static readonly Guid SkillId = Guid.Parse("2f2f9a3e-0d1a-4c9a-9d9c-6f6f0a2b7c11");

    internal static RuntimePackage WithOfferedTools(RuntimePackage package, params string[] toolNames)
    {
        foreach (var toolName in toolNames)
        {
            package.AllowedTools.Add(new AllowedToolDto
            {
                Id = Guid.NewGuid(),
                Name = toolName,
                Location = ToolLocation.ClientLocal,
                RequiresApproval = false
            });
        }

        return package;
    }

    // The specialist's model: "small-model" is resident with a 4,096 window, any other plain id is local but not loaded yet,
    // and CloudModel routes to the cloud.
    internal static async Task<OrchestrationAgentDefinition> CaptureOrchestrationDefinitionAsync(ChatOutputCap outputCap,
        RuntimePackageBuilder packageBuilder,
        bool frozen = false,
        string specialistModel = "small-model",
        int? declaredRemoteWindow = null)
    {
        OrchestrationAgentDefinition? captured = null;
        var orchestrationFactory = Substitute.For<IOrchestrationAgentFactory>();
        orchestrationFactory.CreateAsync(Arg.Do<OrchestrationAgentDefinition>(definition => captured = definition), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>())
                            .Returns(_ => Task.FromException<IOrchestrationRunSession>(new InvalidOperationException("stop after the definition")));
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        cloudFactory.IsCloudProviderSelected(CloudModel).Returns(true);
        // A declared window, when given, is the cloud catalog's for CloudModel and the operator's for the ext: id.
        var cloudModels = Substitute.For<ICloudModelResolver>();
        cloudModels.ResolveContextWindowAsync(CloudModel, Arg.Any<CancellationToken>()).Returns(new CloudModelContextWindow
        {
            ContextTokens = declaredRemoteWindow
        });
        var runner = CreateRunner(orchestrationAgentFactory: orchestrationFactory,
            cloudModelResolver: cloudModels,
            modelTrustResolver: new FakeModelTrustResolver().Register("provider", "remote-model", contextLength: declaredRemoteWindow),
            providerResolver: CreatePerModelLlamaCppResolver(new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["qwen3.5:0.8b"] = 65_536,
                ["small-model"] = 4_096
            }),
            activeCloudFactory: cloudFactory,
            configureRuntimeSettings: settings => settings.GetChatOutputCapAsync(Arg.Any<CancellationToken>()).Returns(outputCap));
        var spec = SampleSpec();
        var package = packageBuilder.WithOrchestrationSpec(spec with
            {
                Participants =
                [
                    spec.Participants[0] with
                    {
                        ModelId = null
                    },
                    spec.Participants[1] with
                    {
                        ModelId = specialistModel
                    }
                ]
            }).Build() with
            {
                UsesFrozenBenchmarkPolicy = frozen
            };

        await RunPlainAsync(runner, package);

        return AssertEx.NotNull(captured);
    }

    // One participant round: its text (if any), then the provider's last chunk carrying only the finish reason.
    internal readonly record struct OrchestrationRound(string Key, string? Text, string FinishReason);

    internal static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationRounds(params OrchestrationRound[] rounds)
    {
        foreach (var round in rounds)
        {
            var name = round.Key == "a" ? "Triage" : "Specialist";
            if (round.Text is { } text)
            {
                yield return OrchestrationUpdate.TextFragment(text, round.Key, name);
            }

            await Task.Yield();
            yield return OrchestrationUpdate.TextFragment(string.Empty, round.Key, name) with
            {
                FinishReason = round.FinishReason
            };
        }

        yield return OrchestrationUpdate.Terminal();
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ReasoningThenContentFinishing(string finishReason, string reasoning, string content)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(reasoning)]);
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, content);
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, [])
        {
            FinishReason = new ChatFinishReason(finishReason)
        };
    }

    internal static async Task<string> ReportedApprovalRequestIdAsync(IWorkerEventDispatcher dispatcher)
    {
        string? requestId = null;
        await AssertEx.EventuallyAsync(() =>
        {
            requestId = dispatcher.ReceivedCalls()
                                  .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(IWorkerEventDispatcher.ReportApprovalRequestedAsync), StringComparison.Ordinal))
                                  .Select(call => ((ApprovalRequestPayload)call.GetArguments()[0]!).RequestId)
                                  .FirstOrDefault();
            return requestId is not null;
        }, TimeSpan.FromSeconds(5));
        return requestId!;
    }

    // Runs one approval round-trip and returns the SessionScopeEligible flag the runner published with it.
    internal static async Task<bool?> CaptureSessionScopeEligibleAsync(IInvocationAgentFactory factory, RuntimePackageBuilder package)
    {
        var dispatcher = ApprovalRecordingDispatcher(out var approvals);
        var published = new List<ApprovalLifecyclePayload>();
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(payload => published.Add(payload))).Returns(Task.CompletedTask);
        var runner = CreateRunner(factory, eventDispatcher: dispatcher);

        var turn = RunAsync(runner, package.Build());
        await AssertEx.EventuallyAsync(() => approvals.Count == 1, TimeSpan.FromSeconds(5));
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = approvals.Single().RequestId,
            Approved = true
        });
        await turn;

        return published.Single().SessionScopeEligible;
    }

    // The send path's whole offer for a tool-capable local model with the knowledge tools on, from the product's provider.
    internal static Task<IReadOnlyList<AllowedToolDto>> DefaultAssistantOfferAsync(string model)
    {
        return new LocalToolOfferProvider(new LocalAgentToolRegistry(TimeProvider.System),
                new McpToolRegistry(NullLogger<McpToolRegistry>.Instance),
                StubNodeRuntimeSettings.Create().WithToolCapableModels(model).WithKnowledgeAgentToolsEnabled(true).Build(),
                NullCustomToolScopeFactory.Instance,
                new FakeModelTrustResolver())
            .GetOfferedToolsAsync(model, isCloudModel: false);
    }

    internal static async Task<string> ReadAgentInstructionsAsync(string fileName)
    {
        await using var stream = typeof(LocalChatAgentOptions).Assembly.GetManifestResourceStream($"XE_Local_AI_Engine.AI.Agent.Instructions.{fileName}")
                                 ?? throw new InvalidOperationException($"Embedded instructions '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    internal static RuntimePackageBuilder WithLargeTools(RuntimePackageBuilder builder, int count, int paddingChars = 1800)
    {
        for (var index = 0; index < count; index++)
        {
            var padding = new string('x', paddingChars);
            builder.WithAllowedTool($"tool_{index}", parameterSchema: $$"""{"type":"object","description":"{{padding}}"}""");
        }

        return builder;
    }

    // The attachment message exactly as ChatTurnContextBuilder builds it, composed at the default 48,000-character cap.
    internal static RuntimePackage WithAttachment(RuntimePackage package, IReadOnlyList<AttachmentTextPart> parts)
    {
        const string Seed = "seed";
        var attachment = new ConversationMessageDto
        {
            Id = Guid.NewGuid(),
            Role = MessageRole.User,
            Content = ConversationAttachmentContextComposer.Compose(parts, 48_000, Seed)!,
            SortOrder = -1,
            ShortenAttachment = budget => ConversationAttachmentContextComposer.Compose(parts, budget, Seed)
        };
        return package with
        {
            ConversationContext = [attachment, .. package.ConversationContext]
        };
    }

    /// <summary>
    ///     A resolver whose model is served by the llama.cpp provider (the only one the runner warms) reporting
    ///     <paramref name="effectiveContextTokens" /> as its launched per-slot context window.
    /// </summary>
    internal static ILocalModelProviderResolver CreateLlamaCppResolver(int? effectiveContextTokens, Exception? warmFailure = null)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        if (warmFailure is not null)
        {
            provider.WarmModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromException(warmFailure));
        }

        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(effectiveContextTokens is { } effective
                    ? new LocalModelRuntimeInfo
                    {
                        EffectiveContextTokens = effective
                    }
                    : null));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        return resolver;
    }

    /// <summary>
    ///     A llama.cpp resolver whose launched per-slot window DIFFERS per model, so a turn that runs two models can
    ///     assert each send was sized against the window of the model that actually served it.
    /// </summary>
    internal static ILocalModelProviderResolver CreatePerModelLlamaCppResolver(IReadOnlyDictionary<string, int> windowsByModel)
    {
        var provider = Substitute.For<ILocalModelProvider>();
        provider.ProviderName.Returns(LlamaServerProviderConstants.ProviderName);
        provider.GetRuntimeInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(callInfo => Task.FromResult(windowsByModel.TryGetValue(callInfo.Arg<string>(), out var window)
                    ? new LocalModelRuntimeInfo
                    {
                        EffectiveContextTokens = window
                    }
                    : null));

        var resolver = Substitute.For<ILocalModelProviderResolver>();
        resolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(LlamaServerProviderConstants.ProviderName));
        resolver.ResolveProviderForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(provider));

        return resolver;
    }

    /// <summary>
    ///     One completed turn on an external model that never swaps, returning the pins in force at its send. The
    ///     `auto` case dispatches to the SAME model, so it is dispatched but not swapped.
    /// </summary>
    internal static async Task<IReadOnlyList<ExternalProviderBindingPin>> RunNoSwapTurnAndCapturePinsAsync(string effort)
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var pinsAtSend = new List<IReadOnlyList<ExternalProviderBindingPin>>();
        using var stub = new StubReasoningEffortDispatcher(Decision(ReasoningTier.Normal,
            ExternalProviderTestData.ModelId,
            "medium",
            ReasoningDispatchReasons.ShortTurn));
        var runner = CreateRunner(invocationAgentFactory: CreateExternalFallbackFactory("qwen3-1.7b", registry, recorder, [], pinsAtSend: pinsAtSend),
            providerStreamResilience: NoRetryResilience(),
            reasoningEffortDispatcherFactory: _ => stub,
            externalProviderRegistry: registry);

        await RunAsync(runner, RuntimePackageBuilder.Valid().WithModel(ExternalProviderTestData.ModelId).WithReasoningEffort(effort).Build());

        AssertEx.Equal(expected: 1, pinsAtSend.Count, "the turn must have sent exactly once");
        return pinsAtSend[0];
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> RecordThinkingOffAtEnd(IAsyncEnumerable<AgentResponseUpdate> updates, List<bool> record)
    {
        await foreach (var update in updates)
        {
            yield return update;
        }

        record.Add(ProviderCallBudget.Current?.IsThinkingOff == true);
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolRoundThenReasonedToolCall()
    {
        await foreach (var update in ToolRoundUpdates("1234 * 5678 = 7006652", finalText: null))
        {
            yield return update;
        }

        yield return new AgentResponseUpdate(ChatRole.Assistant,
            [new TextReasoningContent("Now add 98765.\n\n<tool_call>\n<function=Calculate>\n<parameter=expression>\n7006652 + 98765\n</parameter>\n</function>\n</tool_call>")]);
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, [])
        {
            FinishReason = new ChatFinishReason("stop")
        };
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ReasoningThenContent(string reasoning, params string[] content)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent(reasoning)]);
        await Task.Yield();
        foreach (var chunk in content)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }

        yield return new AgentResponseUpdate(ChatRole.Assistant, [])
        {
            FinishReason = new ChatFinishReason("stop")
        };
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ArmedReasoningUpdates(TaskCompletionSource reasoning, TaskCompletionSource release)
    {
        var armed = new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking")])
        {
            ResponseId = "chatcmpl-runner",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [LlamaServerReasoningControl.EndpointKey] = new Uri("http://127.0.0.1:5812/v1")
            }
        };
        yield return new AgentResponseUpdate(armed)
        {
            RawRepresentation = armed
        };
        reasoning.SetResult();
        await release.Task;
        yield return new AgentResponseUpdate(ChatRole.Assistant, "answer");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ThinkingThenFinish(string finishReason, bool withReasoning = true)
    {
        if (withReasoning)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("still thinking")]);
            await Task.Yield();
        }

        yield return new AgentResponseUpdate(ChatRole.Assistant, [])
        {
            FinishReason = new ChatFinishReason(finishReason)
        };
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> CapturingBudget(Action<int?> capture)
    {
        capture(ToolResultBudgetScope.Current);
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "ok");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> NarratedToolCallUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent("plan"),
            new TextContent("Let me search. "),
            new FunctionCallContent("call-1", "test-tool")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-1", "ok")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolRoundUpdates(string result, string? finalText)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "test-tool")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-1", result)
        });
        await Task.Yield();

        if (finalText is not null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, finalText);
        }
    }

    internal static bool IsClose(double? actual, double expected) =>
        actual is { } value && Math.Abs(value - expected) < 0.0001;

    /// <summary>
    ///     One stream carrying TWO terminal readings, i.e. what a tool-calling turn looks like on the wire: the second
    ///     request re-sends the conversation, so its prompt is mostly served from the prompt cache.
    /// </summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> TwoRequestTimingsUpdates()
    {
        await Task.Yield();
        yield return TimingsUpdate("first", cacheN: 0, promptN: 40, promptMs: 12.5, predictedN: 89, predictedMs: 200.25);
        yield return TimingsUpdate("second", cacheN: 2346, promptN: 283, promptMs: 456.5, predictedN: 1720, predictedMs: 1011.5);
    }

    internal static AgentResponseUpdate TimingsUpdate(string text, int cacheN, int promptN, double promptMs, int predictedN, double predictedMs)
    {
        var json = string.Create(CultureInfo.InvariantCulture,
            $"{{\"id\":\"chunk\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\","
            + $"\"choices\":[{{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{{}}}}],"
            + $"\"timings\":{{\"cache_n\":{cacheN},\"prompt_n\":{promptN},\"prompt_ms\":{promptMs},"
            + $"\"predicted_n\":{predictedN},\"predicted_ms\":{predictedMs}}}}}");
        var chunk = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(json))
                    ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
        return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, text)
        {
            RawRepresentation = chunk
        });
    }

    /// <summary>
    ///     A two-chunk stream shaped like a real llama-server one: the timings ride the FINAL chunk only, on the OpenAI
    ///     SDK object reachable through the agent update's raw representation. Deliberately the real types rather than a
    ///     stub, because the double hop (agent update -> chat update -> OpenAI chunk) is exactly what a package bump can
    ///     break without any compile error.
    /// </summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> TimingsUpdates()
    {
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "part one");

        const string finalChunk = """
                                  {"id":"chunk","object":"chat.completion.chunk","created":1,"model":"m",
                                   "choices":[{"index":0,"finish_reason":"stop","delta":{}}],
                                   "timings":{"cache_n":7,"prompt_n":123,"prompt_ms":456.5,"predicted_n":89,"predicted_ms":1011.5}}
                                  """;
        var chunk = ModelReaderWriter.Read<StreamingChatCompletionUpdate>(BinaryData.FromString(finalChunk))
                    ?? throw new InvalidOperationException("The chunk fixture did not deserialize.");
        yield return new AgentResponseUpdate(new ChatResponseUpdate(ChatRole.Assistant, " part two")
        {
            RawRepresentation = chunk,
            FinishReason = ChatFinishReason.Stop
        });
    }

    /// <summary>
    ///     Two streamed updates carrying <paramref name="first" /> then <paramref name="last" />. Real streams carry a
    ///     finish reason only on the terminal update of each segment, so the accumulator keeps the last NON-NULL one:
    ///     a trailing null must not erase the reason an earlier segment reported.
    /// </summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> FinishReasonUpdates(ChatFinishReason? first, ChatFinishReason? last)
    {
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "part one")
        {
            FinishReason = first
        };
        yield return new AgentResponseUpdate(ChatRole.Assistant, " part two")
        {
            FinishReason = last
        };
    }

    /// <summary>
    ///     The SEQUENTIAL id-less shape: one call, its result, then the same tool called again with byte-identical
    ///     arguments and its own result. Distinct dictionary instances carrying equal content, which is exactly what a
    ///     re-emitted chunk of ONE call also looks like — the closed surrogate is what tells the two apart.
    /// </summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> SequentialCallIdLessToolCallUpdates()
    {
        for (var call = 0; call < 2; call++)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent(string.Empty,
                    "test-tool",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["path"] = "README.md"
                    })
            });
            await Task.Yield();

            yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionResultContent(string.Empty, call == 0 ? "first" : "second")
            });
            await Task.Yield();
        }
    }

    /// <summary>
    ///     Two calls to the SAME tool with a blank CallId, then their two results — also blank, exactly as
    ///     Microsoft.Extensions.AI copies the call's id onto the result. Blank rather than null because both content
    ///     constructors throw on null (10.9.0), which makes the empty string the only id-less shape a provider can
    ///     actually stream.
    /// </summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> CallIdLessToolCallUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(string.Empty,
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "a"
                }),
            new FunctionCallContent(string.Empty,
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "b"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent(string.Empty, "a.txt"),
            new FunctionResultContent(string.Empty, "b.txt")
        });
        await Task.Yield();
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> RepeatedToolCallUpdates()
    {
        var repeated = new FunctionCallContent("call-1",
            "test-tool",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["path"] = "README.md"
            });

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            repeated
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            repeated
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "README.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-1", "ok")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-2",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "CHANGELOG.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-2", "ok")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ChangedArgumentsToolCallUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "README.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1",
                "test-tool",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["path"] = "CHANGELOG.md"
                })
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    // Two rounds of the SAME tool (matching call ids) both returning ToolArgumentRepairAIFunction's structured
    // "tool_disabled" marker — the real trigger is 3 consecutive invalid calls inside AI.Agent, but the runner's
    // notice logic only inspects the wire shape of the result, so a hand-built marker exercises it without depending
    // on AI.Agent internals.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolDisabledUpdates()
    {
        const string disabledMarker =
            "{\"error\":\"tool_disabled\",\"reason\":\"Tool 'test-tool' was disabled for this run after repeated invalid-argument calls.\",\"hint\":\"Do not call this tool again during this run; continue without it.\"}";

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-1", "test-tool")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-1", disabledMarker)
        });
        await Task.Yield();

        // A second call to the now-disabled tool returns the identical marker; the notice must fire only once.
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-2", "test-tool")
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionResultContent("call-2", disabledMarker)
        });
        await Task.Yield();

        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    /// <summary>
    ///     Waits until the turn holds its idle and turn deadlines: the idle timer re-arms on a pool thread, so an earlier advance steps past
    ///     nothing. The budget is a failure deadline.
    /// </summary>
    internal static Task WaitForIdleAndTurnDeadlineAsync(ManualTimeProvider clock, string message) =>
        AssertEx.EventuallyAsync(() => clock.ArmedTimerCount == IdleAndTurnDeadline, TestBudgets.Contended, message);

    // Steps the clock one idle window at a time until the run completes, each step only once the idle timer is armed: it arms after the stream's
    // signal, so an ungated step could land first and walk only the turn deadline (a run then ends on the 300 s turn deadline instead).
    internal static async Task AdvanceIdleWindowsUntilCompleteAsync(ManualTimeProvider clock, Task run)
    {
        var budget = Stopwatch.StartNew();
        while (!run.IsCompleted && budget.Elapsed < TestBudgets.Contended)
        {
            await AssertEx.EventuallyAsync(() => run.IsCompleted || clock.ArmedTimerCount == IdleAndTurnDeadline, TestBudgets.Contended, "the idle deadline was never armed");
            if (!run.IsCompleted)
            {
                clock.Advance(TimeSpan.FromSeconds(61));
            }
        }
    }

    // Pins the idle failure, so the whole-turn deadline reporting a timeout could never pass an idle-deadline test.
    internal static bool IsStreamIdleMessage(string message) =>
        message.StartsWith("Streaming stalled", StringComparison.Ordinal);

    internal static async IAsyncEnumerable<AgentResponseUpdate> ThinkThenSlowToolUpdates(Task modelDone,
        Task toolDone,
        TaskCompletionSource thinking,
        TaskCompletionSource toolStarted,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, "planning");
        thinking.TrySetResult();
        await modelDone.WaitAsync(cancellationToken);
        await foreach (var update in SlowToolUpdates(toolDone, toolStarted, cancellationToken))
        {
            yield return update;
        }
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> SlowToolUpdates(Task toolDone,
        TaskCompletionSource toolStarted,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-slow", "slow-tool")
        });

        // Runs on the next pull, the one the idle deadline now bounds.
        toolStarted.TrySetResult();

        // The framework runs the tool inside this pull; nothing streams until it returns.
        await toolDone.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call-slow", "ok")
        });
        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> SlowPrefillUpdates(bool afterToolResult,
        Task prefillDone,
        TaskCompletionSource prefillStarted,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        if (afterToolResult)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
            {
                new FunctionCallContent("call-prefill", "slow-tool")
            });
            yield return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent>
            {
                new FunctionResultContent("call-prefill", "ok")
            });
        }

        // The round's prefill: nothing streams until the prompt is evaluated.
        prefillStarted.TrySetResult();
        await prefillDone.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolRoundThenSilentAfterTextUpdates(Task never,
        TaskCompletionSource textYielded,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent("call-prefill", "slow-tool")
        });
        yield return new AgentResponseUpdate(ChatRole.Tool, new List<AIContent>
        {
            new FunctionResultContent("call-prefill", "ok")
        });
        yield return new AgentResponseUpdate(ChatRole.Assistant, "partial");

        // Runs on the next pull: the second round has streamed, and this is the pull the idle deadline bounds.
        textYielded.TrySetResult();
        await never.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "unreachable");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> SilentAfterTextUpdates(Task never,
        TaskCompletionSource textYielded,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, "hi");

        // Runs on the next pull: the runner has consumed "hi", and this is the pull the idle deadline bounds.
        textYielded.TrySetResult();
        await never.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "unreachable");
    }

    // The approval card's opaque request id is published only by the dispatcher, never by the hub send
    // (MockHubMessageSender.SentApprovals). A plain substitute with an Arg.Do recorder, so a test that
    // also asserts on the dispatcher with Received()/DidNotReceive() keeps working against the same instance.
    internal static IWorkerEventDispatcher ApprovalRecordingDispatcher(out List<ApprovalRequestPayload> approvals)
    {
        var recorded = new List<ApprovalRequestPayload>();
        approvals = recorded;

        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportApprovalRequestedAsync(Arg.Do<ApprovalRequestPayload>(payload =>
                  {
                      lock (recorded)
                      {
                          recorded.Add(payload);
                      }
                  }))
                  .Returns(Task.CompletedTask);

        return dispatcher;
    }

    internal static InvocationRunner CreateRunner(IInvocationAgentFactory? invocationAgentFactory = null,
        IRuntimePackageValidator? validator = null,
        ICapabilityReporter? capabilityReporter = null,
        WorkerNodeOptions? workerOptions = null,
        IWorkerEventDispatcher? eventDispatcher = null,
        IAsyncEnumerable<AgentResponseUpdate>? agentUpdates = null,
        IOrchestrationAgentFactory? orchestrationAgentFactory = null,
        ILocalModelProviderResolver? providerResolver = null,
        IActiveCloudChatClientFactory? activeCloudFactory = null,
        IProviderStreamResilience? providerStreamResilience = null,
        ConversationContextBudgetOptions? contextBudgetOptions = null,
        UserQuestionAnswerStash? userQuestionAnswerStash = null,
        IToolApprovalAuditRecorder? approvalAuditRecorder = null,
        IToolApprovalPolicy? approvalPolicy = null,
        IInvocationAttachmentTracker? attachmentTracker = null,
        Func<CancellationToken, Task<bool>>? toolRelevanceRead = null,
        IToolRelevanceCoreSet? toolRelevanceCoreSet = null,
        Func<IServiceProvider, IReasoningEffortDispatcher>? reasoningEffortDispatcherFactory = null,
        IExternalProviderRegistry? externalProviderRegistry = null,
        PendingToolCallRegistry? pendingToolCallRegistry = null,
        WebReviewRetriever? webReviewRetriever = null,
        TimeProvider? timeProvider = null,
        IConversationContextBudgeter? contextBudgeter = null,
        IKnowledgeModelPrewarmer? knowledgeModelPrewarmer = null,
        bool knowledgeToolsEnabled = true,
        Action<INodeRuntimeSettings>? configureRuntimeSettings = null,
        InvocationReasoningControl? reasoningControl = null,
        ILogger<InvocationRunner>? logger = null,
        TimeProvider? coordinatorTimeProvider = null,
        ICloudModelResolver? cloudModelResolver = null,
        IModelTrustResolver? modelTrustResolver = null)
    {
        var resolvedContextBudgetOptions = contextBudgetOptions ?? new ConversationContextBudgetOptions();
        var resolvedFactory = invocationAgentFactory ?? CreateFactory(agentUpdates ?? CreateUpdates("ok"));
        var resolvedOrchestrationFactory = orchestrationAgentFactory ?? Substitute.For<IOrchestrationAgentFactory>();

        var resolvedValidator = validator ?? Substitute.For<IRuntimePackageValidator>();
        if (validator is null)
        {
            resolvedValidator.Validate(Arg.Any<RuntimePackage>(), Arg.Any<bool>()).Returns(RuntimePackageValidationResult.Success);
        }

        var resolvedCapabilityReporter = capabilityReporter ?? Substitute.For<ICapabilityReporter>();
        if (capabilityReporter is null)
        {
            resolvedCapabilityReporter.VerifyOllamaAndModelAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        }

        // Default to the Ollama provider so the existing tests keep exercising the VerifyOllamaAndModelAsync preflight
        // path (a non-Ollama provider intentionally bypasses it). Tests can pass their own resolver to cover routing.
        var resolvedProviderResolver = providerResolver ?? Substitute.For<ILocalModelProviderResolver>();
        if (providerResolver is null)
        {
            resolvedProviderResolver.ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                                    .Returns(Task.FromResult(OllamaLocalModelProvider.OllamaProviderName));
        }

        // Default: no cloud provider selected (IsCloudProviderSelected returns false), so the warm path resolves the
        // local provider exactly as before. A cloud-routing test injects one that returns true to prove the warm is skipped.
        var resolvedActiveCloudFactory = activeCloudFactory ?? Substitute.For<IActiveCloudChatClientFactory>();

        var resolvedEventDispatcher = eventDispatcher ?? Substitute.For<IWorkerEventDispatcher>();

        // Default to a real resilience with retry/backoff/breaker at defaults: success-path tests establish the stream
        // on the first attempt, so the wrapper is transparent. Resilience-specific behavior is covered directly in
        // ProviderStreamResilienceTests; a test can still inject its own to exercise the wired path.
        var resolvedProviderStreamResilience = providerStreamResilience
                                               ?? new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions()),
                                                   StubNodeRuntimeSettings.Create().Build(),
                                                   TimeProvider.System,
                                                   NullLogger<ProviderStreamResilience>.Instance);

        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                ["Ollama:ChatModel"] = "qwen3.5:0.8b"
                            })
                            .Build();

        var resolvedWorkerOptions = workerOptions ?? new WorkerNodeOptions
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 5
        };
        var runtimeSettings = StubNodeRuntimeSettings.Create()
                                                     .WithMaxResponseSizeMb(resolvedWorkerOptions.MaxResponseSizeMb)
                                                     .WithMaxPendingToolCallAgeMinutes(resolvedWorkerOptions.MaxPendingToolCallAgeMinutes)
                                                     // Tool relevance stays OFF by default here: every existing assertion in this file is a
                                                     // byte-identical-offer assertion. The notice-drain tests pass their own enabled read.
                                                     .WithToolRelevanceEnabled(toolRelevanceRead ?? (static _ => Task.FromResult(false)))
                                                     // On, as the stub web the retriever reads: the coordinator re-reads it before accepted content lands.
                                                     .WithWebAccessEnabled(true)
                                                     // The tests size the turn through ConversationContextBudgetOptions; the runner now reads
                                                     // these two from the accessor, so mirror them to keep each test's window and keep-count.
                                                     .WithDefaultContextTokens(resolvedContextBudgetOptions.DefaultContextTokens)
                                                     .WithContextBudgetRecentTurnKeepCount(resolvedContextBudgetOptions.RecentTurnKeepCount)
                                                     .WithKnowledgeAgentToolsEnabled(knowledgeToolsEnabled)
                                                     .Build();
        configureRuntimeSettings?.Invoke(runtimeSettings);

        // One registry instance shared by all three collaborators, exactly as the DI graph wires it: a second copy
        // would let a call be registered in one dictionary and resolved against another. A test that has to observe
        // the registry directly (the stale sweep) passes its own and holds that same reference.
        var resolvedPendingToolCallRegistry = pendingToolCallRegistry ?? new PendingToolCallRegistry();
        var resolvedTimeProvider = timeProvider ?? TimeProvider.System;

        return new InvocationRunner(new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
            resolvedFactory,
            resolvedOrchestrationFactory,
            resolvedValidator,
            resolvedCapabilityReporter,
            resolvedProviderResolver,
            new LocalRuntimeWarmer(resolvedProviderResolver, resolvedActiveCloudFactory, modelTrustResolver ?? new FakeModelTrustResolver(),
                cloudModelResolver ?? Substitute.For<ICloudModelResolver>(), NullLogger<LocalRuntimeWarmer>.Instance, TimeProvider.System, new GpuWorkGate()),
            resolvedProviderStreamResilience,
            contextBudgeter ?? new ConversationContextBudgeter(new HeuristicTokenEstimator(), Options.Create(resolvedContextBudgetOptions), runtimeSettings),
            Options.Create(resolvedContextBudgetOptions),
            Options.Create(new ProviderResilienceOptions()),
            Options.Create(new AgentToolPipelineOptions()),
            Options.Create(new ProviderCallBudgetOptions()),
            toolRelevanceCoreSet ?? new FakeToolRelevanceCoreSet(),
            configuration,
            runtimeSettings,
            new ToolApprovalCoordinator(new Lazy<IWorkerEventDispatcher>(() => resolvedEventDispatcher),
                resolvedPendingToolCallRegistry,
                approvalAuditRecorder ?? Substitute.For<IToolApprovalAuditRecorder>(),
                approvalPolicy ?? NodeToolApprovalPolicy.FromSettings(settings: null),
                userQuestionAnswerStash ?? new UserQuestionAnswerStash(TimeProvider.System),
                webReviewRetriever ?? WebReviewTestServer.IdleRetriever,
                runtimeSettings,
                NullLogger<ToolApprovalCoordinator>.Instance,
                coordinatorTimeProvider ?? TimeProvider.System),
            new ApiToolCallBridge(resolvedPendingToolCallRegistry, TimeProvider.System),
            new InvocationLifecycleTracker(attachmentTracker ?? CreateAttachmentTracker(), resolvedPendingToolCallRegistry, runtimeSettings, resolvedTimeProvider),
            externalProviderRegistry ?? new FakeExternalProviderRegistry(),
            // The runner opens ONE scope per `auto` turn and resolves the dispatcher from it. The default provider
            // registers nothing, so a test that never sends `auto` proves — by not throwing — that no scope is used.
            CreateScopeFactory(reasoningEffortDispatcherFactory),
            knowledgeModelPrewarmer ?? Substitute.For<IKnowledgeModelPrewarmer>(),
            reasoningControl ?? new InvocationReasoningControl(Substitute.For<ILlamaServerNativeClient>(), NullLogger<InvocationReasoningControl>.Instance),
            logger ?? NullLogger<InvocationRunner>.Instance,
            resolvedTimeProvider);
    }

    /// <summary>
    ///     A real container holding only the reasoning-effort dispatcher, registered SCOPED exactly as the composition
    ///     root registers it — so a test drives the same resolve-from-a-scope path the product does.
    /// </summary>
    internal static IServiceScopeFactory CreateScopeFactory(Func<IServiceProvider, IReasoningEffortDispatcher>? dispatcherFactory)
    {
        var services = new ServiceCollection();
        if (dispatcherFactory is not null)
        {
            services.AddScoped(dispatcherFactory);
        }

        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    internal static async Task RunAsync(InvocationRunner runner,
        RuntimePackage package,
        IInvocationGenerationAdmissionPolicy? generationAdmissionPolicy = null,
        CancellationToken cancellationToken = default)
    {
        var context = InvocationExecutionContext.CreatePlain(package,
            Guid.NewGuid(),
            generationAdmissionPolicy: generationAdmissionPolicy);
        await runner.RunAsync(context, cancellationToken);
    }

    internal static async Task RunPlainAsync(InvocationRunner runner, RuntimePackage package, CancellationToken cancellationToken = default)
    {
        var context = InvocationExecutionContext.CreatePlain(package, Guid.Empty);
        await runner.RunAsync(context, cancellationToken);
    }

    // A REAL tracker, not a substitute: the deadline tests turn on its ref-counting and its AttachmentChanged event
    // firing exactly on the zero boundaries, and a stub that got either wrong would make them pass for the wrong reason.
    internal static InvocationAttachmentTracker CreateAttachmentTracker()
    {
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        return new InvocationAttachmentTracker(new Lazy<IWorkerEventDispatcher>(() => dispatcher), TimeProvider.System);
    }

    // The single registered turn's linked source on the runner's own InvocationLifecycleTracker, or null when no turn is registered.
    internal static CancellationTokenSource? GetActiveInvocationCancellationTokenSource(InvocationRunner runner)
    {
        var tracker = GetPrivateField(runner, "_lifecycleTracker");
        var turns = (IDictionary)GetPrivateField(tracker, "_turns");
        var turn = turns.Values.Cast<object>().SingleOrDefault();
        return turn is null ? null : (CancellationTokenSource?)AssertEx.NotNull(turn.GetType().GetProperty("Source")).GetValue(turn);
    }

    internal static object GetPrivateField(object target, string name)
    {
        var field = AssertEx.NotNull(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic));
        return AssertEx.NotNull(field.GetValue(target));
    }

    // ---- reasoning-effort dispatch helpers -----------------------------------------------------------------------

    /// <summary>
    ///     A dispatcher that answers with one fixed decision. Records how many times it was invoked so a test can
    ///     assert both "never" and "exactly once".
    /// </summary>
    internal sealed class StubReasoningEffortDispatcher : IReasoningEffortDispatcher, IDisposable
    {
        private readonly ReasoningDispatchDecision _decision;

        public StubReasoningEffortDispatcher(ReasoningDispatchDecision decision)
        {
            _decision = decision;
        }

        public int Invocations { get; private set; }

        /// <summary>Set when the SCOPE that produced this instance is torn down — this is a scoped registration.</summary>
        public long? DisposedAtTicks { get; private set; }

        public Task<ReasoningDispatchDecision> DispatchAsync(ReasoningDispatchRequest request, CancellationToken cancellationToken)
        {
            Invocations++;
            return Task.FromResult(_decision);
        }

        public void Dispose()
        {
            DisposedAtTicks ??= Stopwatch.GetTimestamp();
        }
    }

    /// <summary>A stand-in for the capacity ledger reservation, recording WHEN it was released.</summary>
    internal sealed class SpyReservation : IDisposable
    {
        public int DisposeCount { get; private set; }

        public long? DisposedAtTicks { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            DisposedAtTicks ??= Stopwatch.GetTimestamp();
        }
    }

    internal static ReasoningDispatchDecision Decision(ReasoningTier tier,
        string model,
        string effort,
        string reasonCode,
        IDisposable? reservation = null)
    {
        return new ReasoningDispatchDecision
        {
            Tier = tier,
            Model = model,
            Effort = effort,
            MaxOutputTokens = null,
            SupportsThinking = true,
            ReasoningBudgetEnforceable = true,
            ReasonCode = reasonCode,
            CapacityReservation = reservation
        };
    }

    /// <summary>
    ///     A factory whose stream fails for ONE model and succeeds for every other, recording each definition it was
    ///     asked to build. That is how a swapped send is made to fail while the original model's re-run completes.
    /// </summary>
    internal static IInvocationAgentFactory CreateModelRoutedFactory(string failingModel, List<InvocationAgentDefinition> observed, bool emitATokenBeforeFailing = false)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   observed.Add(definition);
                   var fails = string.Equals(definition.ModelId, failingModel, StringComparison.Ordinal);
                   Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updates = fails
                       ? _ => emitATokenBeforeFailing ? TokenThenThrowingUpdates() : ThrowingUpdates()
                       : _ => CreateUpdates("ok");

                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updates, onSessionObserved: null),
                       Session = null,
                       SeedMessages = definition.OmitSystemPrompt
                           ? definition.ConversationContext
                           : definition.ConversationContext.Prepend(new ChatMessage(ChatRole.System, definition.Instructions)).ToList()
                   });
               });

        return factory;
    }

    /// <summary>
    ///     Like <see cref="CreateModelRoutedFactory" />, except every non-failing model sends through the REAL
    ///     <see cref="ExternalOpenAiChatClient" /> over a recording transport — so the pin check that refuses a changed
    ///     binding is the production one, running inside the runner's own ambient pin scope, rather than a copy of its
    ///     rule restated in the test.
    /// </summary>
    internal static IInvocationAgentFactory CreateExternalFallbackFactory(string failingModel,
        IExternalProviderRegistry registry,
        OpenAiWireRecorder recorder,
        List<InvocationAgentDefinition> observed,
        Action? onFastModelSend = null,
        List<IReadOnlyList<ExternalProviderBindingPin>>? pinsAtSend = null)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   observed.Add(definition);
                   var fails = string.Equals(definition.ModelId, failingModel, StringComparison.Ordinal);
                   Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updates = fails
                       ? _ => FailingFastModelUpdates(onFastModelSend)
                       : token => ExternalSendUpdates(registry, definition.ModelId, recorder, pinsAtSend, token);

                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updates, onSessionObserved: null),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    /// <summary>The fast model going away at the send boundary, with the operator's edit landing as it does.</summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> FailingFastModelUpdates(Action? onSend)
    {
        onSend?.Invoke();
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("the fast model is gone"));
    }

    /// <summary>One real external send, made from inside the turn's ambient pin scope.</summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> ExternalSendUpdates(IExternalProviderRegistry registry,
        string modelId,
        OpenAiWireRecorder recorder,
        List<IReadOnlyList<ExternalProviderBindingPin>>? pinsAtSend,
        [EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        pinsAtSend?.Add(ExternalProviderBindingPinScope.Current);
        using var client = new ExternalOpenAiChatClient(registry, modelId, recorder.CreateHandler);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text);
    }

    /// <summary>Streams one chunk and then fails, so the turn has recorded first output before the failure.</summary>
    internal static async IAsyncEnumerable<AgentResponseUpdate> TokenThenThrowingUpdates()
    {
        yield return new AgentResponseUpdate(ChatRole.Assistant, "partial");
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("stream failed"));
    }

    /// <summary>Retry OFF, so a failing stream surfaces once and the test observes exactly the runner's own behavior.</summary>
    internal static IProviderStreamResilience NoRetryResilience()
    {
        return new ProviderStreamResilience(Options.Create(new ProviderResilienceOptions()),
            StubNodeRuntimeSettings.Create().WithProviderRetryEnabled(false).Build(),
            TimeProvider.System,
            NullLogger<ProviderStreamResilience>.Instance);
    }

    internal static IInvocationAgentFactory CreateFactory(IAsyncEnumerable<AgentResponseUpdate> updates, Action<InvocationAgentDefinition>? onCreate = null, Action<bool>? onSessionObserved = null)
    {
        return CreateFactory(_ => updates, onCreate, onSessionObserved);
    }

    internal static IInvocationAgentFactory CreateFactory(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory, Action<InvocationAgentDefinition>? onCreate = null,
        Action<bool>? onSessionObserved = null)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   onCreate?.Invoke(definition);
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updatesFactory, onSessionObserved),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    internal static IInvocationAgentFactory CreateGenerationSpyFactory(Action onGeneration)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(_ => CreateUpdates("ok"), onStreamingRun: onGeneration),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> CreateUpdates(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    // Records "stream" the moment the agent's streaming is first pulled, so a test can assert the readiness (warm) phase
    // ran BEFORE any streaming began.
    internal static async IAsyncEnumerable<AgentResponseUpdate> WarmOrderingUpdates(ConcurrentQueue<string> events)
    {
        events.Enqueue("stream");
        yield return new AgentResponseUpdate(ChatRole.Assistant, "ok");
        await Task.Yield();
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ApprovalRequestUpdates()
    {
        // Stands in for FunctionInvokingChatClient surfacing an approval request for an ApprovalRequiredAIFunction:
        // the runner must detect this, run the approval round-trip, and resume threadlessly.
        var toolCall = new ToolCallContent("call-run-in-agent-home");
        var approvalRequest = new ToolApprovalRequestContent("approval-run-in-agent-home", toolCall);
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            approvalRequest
        });
        await Task.Yield();
    }

    // An approval-gated segment whose assistant turn carries a very large chunk of model reasoning, so the folded history
    // the tool-loop re-budgets on the approval resume overruns the window by that reasoning alone.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ReasoningHeavyApprovalRequestUpdates(int reasoningChars)
    {
        var toolCall = new ToolCallContent("call-run-in-agent-home");
        var approvalRequest = new ToolApprovalRequestContent("approval-run-in-agent-home", toolCall);
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new TextReasoningContent(new string(c: 'r', reasoningChars)),
            approvalRequest
        });
        await Task.Yield();
    }

    // A package carrying ONE resolved skill, plus the offered tool the approval path needs (the runner only retains the
    // segment updates it folds on resume when the offer list is non-empty — see approvalPossible). The skill tools
    // themselves are never in the offer: they reach the model through MAF's context provider.
    internal static RuntimePackageBuilder SkillPackage(Guid conversationId, int version = 1, bool imported = false)
    {
        return RuntimePackageBuilder.Valid()
                                    .WithConversationId(conversationId)
                                    .WithAllowedTool("run_in_agent_home")
                                    .WithSkills(new ResolvedSkill(SkillId, SkillName, "A skill.", "Skill body.", version, imported));
    }

    internal static IInvocationAgentFactory SkillApprovalFactory(string toolName, string skillName)
    {
        // Segments 1 and 3 are the first segment of turn one and turn two; the even segments are the resumes that follow
        // each approval decision. A suppressed prompt still resumes, so the segment count is the same either way.
        var segment = 0;
        return CreateFactory(_ =>
        {
            segment++;
            return segment is 1 or 3 ? SkillApprovalRequestUpdates(toolName, skillName) : CreateUpdates("done");
        });
    }

    // Stands in for MAF's AgentSkillsProvider surfacing an approval request for one of its ApprovalRequiredAIFunction
    // skill tools. Unlike ApprovalRequestUpdates above, the tool call is a concrete FunctionCallContent carrying the
    // model's arguments, because the skill and resource names the memo key is built from live nowhere else.
    internal static async IAsyncEnumerable<AgentResponseUpdate> SkillApprovalRequestUpdates(string toolName, string skillName, string? resourceName = null)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["skillName"] = skillName
        };

        if (resourceName is not null)
        {
            arguments["resourceName"] = resourceName;
        }

        var toolCall = new FunctionCallContent($"call-{toolName}", toolName, arguments);
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent($"approval-{toolName}", toolCall)
        });
        await Task.Yield();
    }

    internal const string CustomToolName = "custom__weather";

    internal static RuntimePackageBuilder CustomToolPackage(Guid conversationId, int version = 1, bool isFixed = true)
    {
        return RuntimePackageBuilder.Valid()
                                    .WithConversationId(conversationId)
                                    .WithAllowedTool(CustomToolName)
                                    .WithCustomTools(new ResolvedCustomTool(CustomToolName, version, isFixed));
    }

    internal static IInvocationAgentFactory CustomToolApprovalFactory(string toolName)
    {
        var segment = 0;
        return CreateFactory(_ =>
        {
            segment++;
            return segment is 1 or 3 ? CustomToolApprovalRequestUpdates(toolName) : CreateUpdates("done");
        });
    }

    // Stands in for the FunctionInvokingChatClient surfacing the approval request for the approval-wrapped custom tool.
    // The custom-tool memo reads only the tool NAME (not the arguments), so the args here are incidental.
    internal static async IAsyncEnumerable<AgentResponseUpdate> CustomToolApprovalRequestUpdates(string toolName)
    {
        var toolCall = new FunctionCallContent($"call-{toolName}", toolName, new Dictionary<string, object?>(StringComparer.Ordinal));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent($"approval-{toolName}", toolCall)
        });
        await Task.Yield();
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> TwoApprovalRequestUpdates()
    {
        // A parallel-tool-call turn surfacing TWO approval requests in ONE segment: the runner must present
        // and answer BOTH, not just the last. Distinct tool-call CallIds so the dedup keeps them separate.
        var firstApproval = new ToolApprovalRequestContent("approval-1", new ToolCallContent("call-1"));
        var secondApproval = new ToolApprovalRequestContent("approval-2", new ToolCallContent("call-2"));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            firstApproval,
            secondApproval
        });
        await Task.Yield();
    }

    // Stands in for FunctionInvokingChatClient surfacing the approval request for the approval-wrapped ask_user tool.
    // The concrete FunctionCallContent is what carries the tool NAME and the model's raw arguments, and both are what
    // the runner branches and parses on.
    internal static async IAsyncEnumerable<AgentResponseUpdate> AskUserRequestUpdates(IDictionary<string, object?> arguments)
    {
        var approvalRequest = new ToolApprovalRequestContent("approval-ask-user", new FunctionCallContent("call-ask-user", AskUserTool.ToolName, arguments));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            approvalRequest
        });
        await Task.Yield();
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> WebFetchRequestUpdates()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>($$"""{"url":"{{WebReviewTestServer.PageUrl}}"}""")!;
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent("approval-web-fetch", new FunctionCallContent("call-web-fetch", WebFetchToolDefinition.ToolName, arguments))
        });
        await Task.Yield();
    }

    // Stands in for the framework executing the approved call in the resumed segment, inside the runner's review scope.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ExecuteWebFetchHandlerUpdates(ConcurrentQueue<string> results)
    {
        results.Enqueue(await new WebFetchToolHandler
        {
            ResolveCallId = static () => "call-web-fetch"
        }.ExecuteAsync("{}"));
        yield return new AgentResponseUpdate(ChatRole.Assistant, "done");
    }

    // Waits for the Nth web card, answers it and returns it: consents and reviews share the one approval transport.
    internal static async Task<ApprovalLifecyclePayload> ResolveWebCardAsync(InvocationRunner runner,
        ConcurrentQueue<ApprovalLifecyclePayload> cards,
        int expectedCount,
        bool approved)
    {
        await AssertEx.EventuallyAsync(() => cards.Count == expectedCount, TimeSpan.FromSeconds(10));
        var card = cards.Last();
        runner.ResolveApprovalResult(new ApprovalResolvedEvent
        {
            RequestId = card.RequestId,
            Approved = approved
        });
        return card;
    }

    internal static (IWorkerEventDispatcher Dispatcher, ConcurrentQueue<ApprovalLifecyclePayload> Cards) WebReviewDispatcher()
    {
        var cards = new ConcurrentQueue<ApprovalLifecyclePayload>();
        var dispatcher = Substitute.For<IWorkerEventDispatcher>();
        dispatcher.ReportApprovalLifecycleAsync(Arg.Do<ApprovalLifecyclePayload>(cards.Enqueue)).Returns(Task.CompletedTask);
        return (dispatcher, cards);
    }

    internal static IInvocationAgentFactory RealPipelineFactory(IChatClient model, UserQuestionAnswerStash stash)
    {
        var registry = new ClientLocalToolRegistry([new AskUserToolHandler(stash), new WebFetchToolHandler(), new WebSearchToolHandler()],
            Options.Create(new AgentToolPipelineOptions()));
        var tools = new List<AITool>();
        foreach (var name in new[]
                 {
                     WebFetchToolDefinition.ToolName,
                     WebSearchToolDefinition.ToolName,
                     AskUserTool.ToolName
                 })
        {
            AssertEx.True(registry.TryResolve(name, out var tool));
            tools.Add(tool!);
        }

        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   var chatClient = model.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = InvocationAgentFactory.BuildApprovalValidatedAgent(chatClient,
                           "web-review",
                           "Result review gate end to end.",
                           tools,
                           NullLoggerFactory.Instance,
                           new ServiceCollection().BuildServiceProvider(),
                           instructions: definition.Instructions),
                       Session = null,
                       SeedMessages = definition.ConversationContext.Prepend(new ChatMessage(ChatRole.System, definition.Instructions)).ToList()
                   });
               });

        return factory;
    }

    // The production approval boundary over a real ChatClientAgent and FunctionInvokingChatClient, carrying the definition's
    // skills through the real MAF skills provider and no other tool.
    internal static IInvocationAgentFactory RealSkillPipelineFactory(IChatClient model)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   var chatClient = model.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = InvocationAgentFactory.BuildApprovalValidatedAgent(chatClient,
                           "skill-approval",
                           "Skill approval end to end.",
                           [],
                           NullLoggerFactory.Instance,
                           new ServiceCollection().BuildServiceProvider(),
                           skillsProvider: InvocationSkillsProvider.Create(AssertEx.NotNull(definition.Skills))),
                       Session = null,
                       SeedMessages = definition.ConversationContext.Prepend(new ChatMessage(ChatRole.System, definition.Instructions)).ToList()
                   });
               });

        return factory;
    }

    internal static RuntimePackage DenialPackage(Guid conversationId) =>
        RuntimePackageBuilder.Valid()
                             .WithConversationId(conversationId)
                             .WithAllowedTool(DenialScriptedChatClient.GuardedToolName, requiresApproval: true)
                             .WithAllowedTool(DenialScriptedChatClient.OtherToolName)
                             .Build();

    // The production approval boundary over a real ChatClientAgent and FunctionInvokingChatClient, with production's run options.
    internal static IInvocationAgentFactory DenialPipelineFactory(IChatClient model)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   var chatClient = model.AsBuilder().UseFunctionInvocation(NullLoggerFactory.Instance).Build();
                   AITool[] tools =
                   [
                       new ApprovalRequiredAIFunction(AIFunctionFactory.Create(static () => "ran", DenialScriptedChatClient.GuardedToolName)),
                       AIFunctionFactory.Create(static () => "other", DenialScriptedChatClient.OtherToolName)
                   ];
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = InvocationAgentFactory.BuildApprovalValidatedAgent(chatClient,
                           "denial",
                           "Denial withhold end to end.",
                           tools,
                           NullLoggerFactory.Instance,
                           new ServiceCollection().BuildServiceProvider()),
                       Session = null,
                       SeedMessages = definition.ConversationContext.Prepend(new ChatMessage(ChatRole.System, definition.Instructions)).ToList(),
                       RunOptions = new ChatClientAgentRunOptions
                       {
                           ChatOptions = new ChatOptions()
                       }
                   });
               });

        return factory;
    }

    // The model: calls the guarded tool until a result for it is back, then records the result and answers. Records every round's tool offer.
    internal sealed class DenialScriptedChatClient : IChatClient
    {
        public const string GuardedToolName = "run_in_agent_home";
        public const string OtherToolName = "list_agent_home";

        private readonly int _callsPerRound;
        private int _calls;

        public DenialScriptedChatClient(int callsPerRound = 1)
        {
            _callsPerRound = callsPerRound;
        }

        public List<string[]> OfferedTools { get; } = [];

        public string? ToolResult { get; private set; }

        public List<string> ToolResults { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            lock (OfferedTools)
            {
                OfferedTools.Add([.. (options?.Tools ?? []).Select(static tool => tool.Name)]);
            }

            var results = messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                ToolResult = results[0].Result?.ToString() ?? string.Empty;
                ToolResults.AddRange(results.Select(static result => result.Result?.ToString() ?? string.Empty));
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "answered without it")));
            }

            List<AIContent> calls =
            [
                .. Enumerable.Range(1, _callsPerRound)
                             .Select(index => new FunctionCallContent(string.Create(CultureInfo.InvariantCulture, $"call-guarded-{call}-{index}"), GuardedToolName))
            ];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, calls)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }

    // The model: round one calls load_skill; once the result (or the rejection) is back it records it and answers.
    internal sealed class LoadSkillScriptedChatClient : IChatClient
    {
        public const string SkillBody = "The codeword is heron.";

        public string? LoadSkillResult { get; private set; }

        public string? FinalText { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().FirstOrDefault() is { } result)
            {
                LoadSkillResult = result.Result?.ToString() ?? string.Empty;
                FinalText = "all done";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, FinalText)));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent("call-load-skill", LoadSkillToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["skillName"] = SkillName
                })
            ])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }

    // The model: round one calls web_fetch, web_search and ask_user in parallel; once all three have results it records them and answers.
    internal sealed class WebReviewScriptedChatClient : IChatClient
    {
        public const string FetchCallId = "call-web-fetch";
        public const string SearchCallId = "call-web-search";
        public const string AskCallId = "call-ask-user";

        public Dictionary<string, string> Results { get; } = new(StringComparer.Ordinal);

        public string? FinalText { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var results = messages.SelectMany(static message => message.Contents).OfType<FunctionResultContent>().ToList();
            if (results.Count > 0)
            {
                foreach (var result in results)
                {
                    Results[result.CallId] = result.Result?.ToString() ?? string.Empty;
                }

                FinalText = "all done";
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, FinalText)));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
            [
                new FunctionCallContent(FetchCallId, WebFetchToolDefinition.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["url"] = WebReviewTestServer.PageUrl
                }),
                new FunctionCallContent(SearchCallId, WebSearchToolDefinition.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["query"] = "tidal power"
                }),
                new FunctionCallContent(AskCallId, AskUserTool.ToolName, ValidAskUserArguments())
            ])));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(IChatClient) ? this : null;

        public void Dispose()
        {
        }
    }

    // An ask_user call whose CallId is a non-null EMPTY STRING — the shape the approval dedup already guards against.
    internal static async IAsyncEnumerable<AgentResponseUpdate> BlankCallIdAskUserUpdates()
    {
        var approvalRequest = new ToolApprovalRequestContent("approval-ask-user", new FunctionCallContent(string.Empty, AskUserTool.ToolName, ValidAskUserArguments()));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            approvalRequest
        });
        await Task.Yield();
    }

    // A well-formed ask_user call, shaped exactly as a provider hands it back (JsonElement values in the argument bag).
    internal static Dictionary<string, object?> ValidAskUserArguments()
    {
        const string Json = """
                            {
                              "questions": [
                                {
                                  "header": "Auth method",
                                  "question": "Which auth method?",
                                  "options": [
                                    { "label": "OAuth device flow", "description": "Works headless.", "recommended": true },
                                    { "label": "API key" }
                                  ]
                                }
                              ]
                            }
                            """;

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(Json, AskUserArgumentOptions)!;
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> BlankCallIdApprovalUpdates()
    {
        // The SAME approval (a stable Id, but NO CallId) surfaced as two DISTINCT instances across two streamed chunks —
        // exercises the Id-based dedup fallback (reference identity alone would not collapse two instances). Dedup must
        // present it exactly once; a blank CallId must never bypass dedup and prompt twice for one call.
        var first = new ToolApprovalRequestContent("approval-no-callid", new FunctionCallContent(string.Empty, "run_in_agent_home"));
        var second = new ToolApprovalRequestContent("approval-no-callid", new FunctionCallContent(string.Empty, "run_in_agent_home"));
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            first
        });
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            second
        });
        await Task.Yield();
    }

    // A factory that records the messages passed to EACH streaming segment (so a resume test can assert the folded
    // approval responses reached the agent) while returning per-segment updates from the supplied factory.
    internal static IInvocationAgentFactory CreateMessageCapturingFactory(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory,
        Action<IReadOnlyList<ChatMessage>> onMessages)
    {
        var factory = Substitute.For<IInvocationAgentFactory>();
        factory.CreateAsync(Arg.Any<InvocationAgentDefinition>(), Arg.Any<CancellationToken>())
               .Returns(callInfo =>
               {
                   var definition = callInfo.Arg<InvocationAgentDefinition>();
                   return Task.FromResult(new InvocationAgentContext
                   {
                       Agent = new FakeAIAgent(updatesFactory, onSessionObserved: null, onMessagesObserved: onMessages),
                       Session = null,
                       SeedMessages = definition.ConversationContext
                                                .Prepend(new ChatMessage(ChatRole.System, definition.Instructions))
                                                .ToList()
                   });
               });

        return factory;
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> CreateMixedUpdates(params (string? Text, string? Thinking)[] chunks)
    {
        foreach (var (text, thinking) in chunks)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(thinking))
            {
                contents.Add(new TextReasoningContent(thinking));
            }

            if (!string.IsNullOrEmpty(text))
            {
                contents.Add(new TextContent(text));
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, contents);
            await Task.Yield();
        }
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> CreateUpdatesWithUsage(params (string Text, UsageDetails? Usage)[] chunks)
    {
        foreach (var (text, usage) in chunks)
        {
            var contents = new List<AIContent>();
            if (!string.IsNullOrEmpty(text))
            {
                contents.Add(new TextContent(text));
            }

            if (usage is not null)
            {
                contents.Add(new UsageContent(usage));
            }

            yield return new AgentResponseUpdate(ChatRole.Assistant, contents);
            await Task.Yield();
        }
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> BlockingUpdates(Task gate, TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        yield return new AgentResponseUpdate(ChatRole.Assistant, "chunk");
        await gate.WaitAsync(cancellationToken);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "tail");
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> WaitForCancellation(TaskCompletionSource started, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    // Parks the stream on an EXTERNAL gate rather than on the cancellation token, so the test alone decides when the
    // parked run is released. That is what makes the reverse-registration-order test deterministic: the release comes
    // from a callback the test registers after the runner's, and no registration of this stream's own can run earlier.
    internal static async IAsyncEnumerable<AgentResponseUpdate> WaitForRelease(Task release,
        TaskCompletionSource started,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        started.TrySetResult();
        await release;
        cancellationToken.ThrowIfCancellationRequested();
        yield break;
    }

    internal static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationFailure(string message)
    {
        await Task.Yield();
        yield return OrchestrationUpdate.Failed(message, "a", "Triage");
    }

    internal static WorkerNodeOptions OneMinutePendingToolCallAge() =>
        new()
        {
            NodeName = "worker",
            MaxResponseSizeMb = 10,
            MaxPendingToolCallAgeMinutes = 1
        };

    /// <summary>
    ///     A tool call parked <paramref name="age" /> ago and never answered, in the shape
    ///     <c>ToolApprovalCoordinator.RequestToolApprovalAsync</c> leaves in the shared registry while a turn waits on
    ///     the operator's card. Seeded directly rather than through a second parked invocation because the only thing
    ///     these two tests assert is that <c>RunAsync</c>'s finally reaches the sweep at all; the registration and the
    ///     sweep's effect on it are proven end to end, against the real coordinator and bridge, in
    ///     <c>ToolApprovalCoordinatorTests</c>.
    /// </summary>
    internal static Task<bool> ParkStaleToolCall(PendingToolCallRegistry registry, TimeSpan age)
    {
        var approvalCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Calls[$"stale-{Guid.NewGuid():N}"] = new PendingToolCall
        {
            InvocationId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow - age,
            ApprovalCompletion = approvalCompletion
        };
        return approvalCompletion.Task;
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ThrowingUpdates()
    {
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("stream failed"));
    }

    // The four helpers below stand in for the two seams that write during a turn but sit several awaited frames BELOW
    // the runner: the provider-call budget middleware, which registers each round's tool-schema token estimate, and the
    // send-time relevance hop, which leaves the notice counts on the ambient scope. Both are AsyncLocal, so a fake
    // agent stream — enumerated inside the runner's own scopes — reaches exactly the instances the turn under test
    // seeded, which is the whole point: neither seam can be injected from out here.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolsFilteredUpdates(int hidden, int total, string text)
    {
        RecordRelevanceDecision(hidden, total);
        yield return new AgentResponseUpdate(ChatRole.Assistant, text);
        await Task.Yield();
    }

    // A first segment that both filters and requests an approval, so the resume segment's own decision can be shown not
    // to re-drain.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolsFilteredApprovalRequestUpdates(int hidden, int total)
    {
        RecordRelevanceDecision(hidden, total);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "working on it");
        await Task.Yield();
        yield return new AgentResponseUpdate(ChatRole.Assistant, new List<AIContent>
        {
            new ToolApprovalRequestContent("approval-run-in-agent-home", new ToolCallContent("call-run-in-agent-home"))
        });
        await Task.Yield();
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolSchemaBudgetedUpdates(params int[] toolSchemaTokensPerRound)
    {
        RegisterProviderRounds(toolSchemaTokensPerRound);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "Hello");
        await Task.Yield();
    }

    // Rounds are registered BEFORE the park, so the estimate the cancelled path reports is a real one rather than zero.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolSchemaBudgetedThenParkedUpdates(int toolSchemaTokens,
        TaskCompletionSource started,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        RegisterProviderRounds([toolSchemaTokens]);
        started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        yield break;
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ToolSchemaBudgetedThenThrowingUpdates(int toolSchemaTokens)
    {
        RegisterProviderRounds([toolSchemaTokens]);
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new InvalidOperationException("stream failed"));
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ReservedFloorCapturingUpdates(List<int> floors)
    {
        floors.Add(AssertEx.NotNull(ProviderCallBudget.Current, "The runner seeds the provider-call budget before the agent runs.").Options.ReservedOutputTokenFloor);
        yield return new AgentResponseUpdate(ChatRole.Assistant, "Hello");
        await Task.Yield();
    }

    internal static void RegisterProviderRounds(IReadOnlyList<int> toolSchemaTokensPerRound)
    {
        var budget = AssertEx.NotNull(ProviderCallBudget.Current, "The runner seeds the provider-call budget before the agent runs.");
        foreach (var toolSchemaTokens in toolSchemaTokensPerRound)
        {
            budget.RegisterProviderRound(estimatedInputTokens: 10, toolSchemaTokens);
        }
    }

    // What the hop's single-flight factory does once one array's decision is computed: Interlocked.Exchange, so the
    // pair the runner drains always describes ONE array rather than a sum across two.
    internal static void RecordRelevanceDecision(int hidden, int total)
    {
        var state = AssertEx.NotNull(ToolRelevanceScope.Current, "The runner seeds the relevance scope before the agent runs.");
        Interlocked.Exchange(ref state.PendingNoticeHiddenCount, hidden);
        Interlocked.Exchange(ref state.PendingNoticeTotalCount, total);
    }

    // The shape an HTTP client timeout takes: a TaskCanceledException on a token this node does not own, raised while
    // every runner/caller token is still live.
    internal static async IAsyncEnumerable<AgentResponseUpdate> ProviderTimeoutUpdates()
    {
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout elapsing."));
    }

    internal static async IAsyncEnumerable<AgentResponseUpdate> ApprovalExpiredUpdates()
    {
        await Task.Yield();
        yield return await Task.FromException<AgentResponseUpdate>(new ApprovalExpiredException("run_python"));
    }

    internal static IOrchestrationAgentFactory CreateOrchestrationFactory(IAsyncEnumerable<OrchestrationUpdate> updates, out Ref<FakeOrchestrationRunSession> sessionRef)
    {
#pragma warning disable CA2000 // The runner owns disposal of the session via its `await using`; the test asserts Disposed.
        return CreateOrchestrationFactory(new FakeOrchestrationRunSession(updates), out sessionRef);
#pragma warning restore CA2000
    }

    internal static IOrchestrationAgentFactory CreateOrchestrationFactory(FakeOrchestrationRunSession session, out Ref<FakeOrchestrationRunSession> sessionRef)
    {
        var capturedRef = new Ref<FakeOrchestrationRunSession>
        {
            Value = session
        };
        sessionRef = capturedRef;

        var factory = Substitute.For<IOrchestrationAgentFactory>();
        factory.CreateAsync(Arg.Any<OrchestrationAgentDefinition>(), Arg.Any<IReadOnlyList<ChatMessage>>(), Arg.Any<CancellationToken>())
               .Returns(_ => Task.FromResult<IOrchestrationRunSession>(capturedRef.Value));

        return factory;
    }

    internal static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationTextUpdates(params string[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return OrchestrationUpdate.TextFragment(chunk, "a", "Triage");
            await Task.Yield();
        }

        yield return OrchestrationUpdate.Terminal();
    }

    internal static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationParticipantTransitionUpdates()
    {
        yield return OrchestrationUpdate.TextFragment("triage", "a", "Triage");
        await Task.Yield();
        yield return OrchestrationUpdate.TextFragment("specialist", "b", "Specialist");
        yield return OrchestrationUpdate.Terminal();
    }

    // The gated approval stream: yields the approval request, then BLOCKS on the session's ApprovalGate before the
    // post-approval text/terminal. The gate is only completed by RespondToApprovalAsync, so if the runner drops or
    // mis-keys the resume the enumeration hangs and the test times out — proving the resume actually fired.
    internal static async IAsyncEnumerable<OrchestrationUpdate> OrchestrationGatedApprovalThenText(FakeOrchestrationRunSession session,
        string requestId,
        string toolName,
        string finalText)
    {
        yield return OrchestrationUpdate.Approval(requestId, toolName, "a", "Triage");
        await session.ApprovalGate;
        yield return OrchestrationUpdate.TextFragment(finalText, "a", "Triage");
        yield return OrchestrationUpdate.Terminal();
    }

    internal static OrchestrationSpec SampleSpec()
    {
        return new OrchestrationSpec
        {
            TriageParticipantKey = "a",
            MaxTurnsPerAgent = 6,
            ReturnToPrevious = false,
            Participants =
            [
                new OrchestrationSpecParticipant
                {
                    Key = "a",
                    Name = "Triage",
                    Description = "Routes work",
                    Instructions = "You are the triage agent.",
                    ModelId = "qwen3:8b",
                    Tools = []
                },
                new OrchestrationSpecParticipant
                {
                    Key = "b",
                    Name = "Specialist",
                    Description = "Does the work",
                    Instructions = "You are the specialist.",
                    ModelId = "qwen3:8b",
                    Tools = []
                }
            ],
            Edges =
            [
                new OrchestrationSpecEdge
                {
                    FromKey = "a",
                    ToKey = "b",
                    Reason = "specialist work"
                }
            ]
        };
    }

    internal sealed class Ref<T>
    {
        public T? Value { get; set; }
    }

    internal readonly record struct HarnessTerminalMeasurement(long Value, string? Provider, string? Outcome, bool? Orchestration);

    internal sealed class HarnessMetricCapture : IDisposable
    {
        private readonly ConcurrentBag<long> _handoffs = [];
        private readonly MeterListener _listener = new();
        private readonly ConcurrentBag<HarnessTerminalMeasurement> _terminals = [];

        public HarnessMetricCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (string.Equals(instrument.Meter.Name, NodeMetrics.MeterName, StringComparison.Ordinal)
                    && instrument.Name is "agent_harness_invocation_total" or "agent_harness_handoffs")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            {
                if (string.Equals(instrument.Name, "agent_harness_handoffs", StringComparison.Ordinal))
                {
                    _handoffs.Add(measurement);
                    return;
                }

                string? provider = null;
                string? outcome = null;
                bool? orchestration = null;
                foreach (var tag in tags)
                {
                    if (string.Equals(tag.Key, "provider", StringComparison.Ordinal))
                    {
                        provider = tag.Value as string;
                    }
                    else if (string.Equals(tag.Key, "outcome", StringComparison.Ordinal))
                    {
                        outcome = tag.Value as string;
                    }
                    else if (string.Equals(tag.Key, "orchestration", StringComparison.Ordinal))
                    {
                        orchestration = tag.Value as bool?;
                    }
                }

                _terminals.Add(new HarnessTerminalMeasurement(measurement, provider, outcome, orchestration));
            });
            _listener.Start();
        }

        public IReadOnlyList<long> Handoffs => [.. _handoffs];

        public IReadOnlyList<HarnessTerminalMeasurement> Terminals => [.. _terminals];

        public void Dispose()
        {
            _listener.Dispose();
        }
    }

    internal sealed class FakeOrchestrationRunSession : IOrchestrationRunSession
    {
        // Completed by RespondToApprovalAsync. A gated update stream awaits this before yielding its post-approval
        // portion, so the approval test only reaches its terminal if the runner actually resumes the held session —
        // a dropped or mis-keyed RespondToApprovalAsync leaves the gate uncompleted and the test times out (fails).
        private readonly TaskCompletionSource<(bool Approved, string? Reason)> _approvalGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Func<FakeOrchestrationRunSession, IAsyncEnumerable<OrchestrationUpdate>> _updatesFactory;

        public FakeOrchestrationRunSession(IAsyncEnumerable<OrchestrationUpdate> updates)
        {
            _updatesFactory = _ => updates;
        }

        public FakeOrchestrationRunSession(Func<FakeOrchestrationRunSession, IAsyncEnumerable<OrchestrationUpdate>> updatesFactory)
        {
            _updatesFactory = updatesFactory;
        }

        public bool Disposed { get; private set; }

        public List<(string RequestId, bool Approved, string? Reason)> ApprovalResponses { get; } = [];

        // The gated update stream awaits this; it only resolves once RespondToApprovalAsync is called for the matching
        // RequestId, proving the runner's resume actually drove the held session.
        public Task<(bool Approved, string? Reason)> ApprovalGate => _approvalGate.Task;

        public IAsyncEnumerable<OrchestrationUpdate> WatchAsync(CancellationToken cancellationToken = default)
        {
            return _updatesFactory(this);
        }

        public Task RespondToApprovalAsync(string requestId, bool approved, string? reason, CancellationToken cancellationToken = default)
        {
            ApprovalResponses.Add((requestId, approved, reason));
            _approvalGate.TrySetResult((approved, reason));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The real budgeter, with every result it handed the runner kept in call order.</summary>
    internal sealed class RecordingContextBudgeter : IConversationContextBudgeter
    {
        public RecordingContextBudgeter(IConversationContextBudgeter inner)
        {
            Inner = inner;
        }

        public IConversationContextBudgeter Inner { get; }

        public List<ConversationBudgetResult> Results { get; } = [];

        public ConversationBudgetResult Budget(IReadOnlyList<ChatMessage> messages,
            int contextTokenCapacity,
            int reservedOutputTokens,
            string? systemPrompt = null,
            IReadOnlyList<string>? toolDefinitions = null,
            string? modelName = null)
        {
            var result = Inner.Budget(messages, contextTokenCapacity, reservedOutputTokens, systemPrompt, toolDefinitions, modelName);
            Results.Add(result);
            return result;
        }
    }

    internal sealed class FakeAIAgent : AIAgent
    {
        private readonly Action<IReadOnlyList<ChatMessage>>? _onMessagesObserved;
        private readonly Action<bool>? _onSessionObserved;
        private readonly Action? _onStreamingRun;
        private readonly Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> _updatesFactory;

        public FakeAIAgent(IAsyncEnumerable<AgentResponseUpdate> updates)
            : this(_ => updates)
        {
        }

        public FakeAIAgent(Func<CancellationToken, IAsyncEnumerable<AgentResponseUpdate>> updatesFactory,
            Action<bool>? onSessionObserved = null,
            Action<IReadOnlyList<ChatMessage>>? onMessagesObserved = null,
            Action? onStreamingRun = null)
        {
            _updatesFactory = updatesFactory;
            _onSessionObserved = onSessionObserved;
            _onMessagesObserved = onMessagesObserved;
            _onStreamingRun = onStreamingRun;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new FakeAgentSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new FakeAgentSession());
        }

        protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            _onSessionObserved?.Invoke(session is null);
            _onMessagesObserved?.Invoke(messages.ToList());
            _onStreamingRun?.Invoke();
            return _updatesFactory(cancellationToken);
        }
    }

    internal sealed class MinimumEffectiveContextAdmissionPolicy : IInvocationGenerationAdmissionPolicy
    {
        public const string EffectiveContextUnavailableMessage = "Effective context unavailable.";
        private readonly int _requiredContextTokens;

        public MinimumEffectiveContextAdmissionPolicy(int requiredContextTokens)
        {
            _requiredContextTokens = requiredContextTokens;
        }

        public InvocationGenerationAdmissionContext? LastContext { get; private set; }

        public Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastContext = context;

            if (context.EffectiveContextTokens is null)
            {
                return Task.FromResult(InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextUnavailable));
            }

            if (context.EffectiveContextTokens < _requiredContextTokens)
            {
                return Task.FromResult(InvocationGenerationAdmissionDecision.Reject(InvocationGenerationAdmissionReasonCodes.EffectiveContextInsufficient));
            }

            return Task.FromResult(InvocationGenerationAdmissionDecision.Allow);
        }
    }

    internal sealed class RejectingAdmissionPolicy : IInvocationGenerationAdmissionPolicy
    {
        private readonly string _reasonCode;

        public RejectingAdmissionPolicy(string reasonCode)
        {
            _reasonCode = reasonCode;
        }

        public Task<InvocationGenerationAdmissionDecision> EvaluateAsync(InvocationGenerationAdmissionContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(InvocationGenerationAdmissionDecision.Reject(_reasonCode));
        }
    }

    internal sealed class FakeAgentSession : AgentSession
    {
    }
}
