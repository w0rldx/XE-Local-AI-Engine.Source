namespace XE_Local_AI_Engine.AI.Agent.Tests.Chat;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.AI.Agent.Chat;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.AI.Agent.Invocation;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ProviderCallBudgetChatClientTests
{
    [Test]
    public async Task GetResponseAsync_WithoutAmbientBudget_PassesMessagesThroughUnchanged()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        var messages = ManyMessagesWithHugeToolResult();
        _ = await sut.GetResponseAsync(messages, SmallWindowOptions());

        // No scope was seeded, so the middleware is a transparent pass-through (eval / preview paths stay byte-identical).
        var received = inner.ReceivedMessageSets.Single();
        AssertEx.Equal(messages.Count, received.Count);
    }

    [Test]
    public async Task GetResponseAsync_WithAmbientBudget_ReBudgetsEachProviderRoundBeforeSending()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        var messages = ManyMessagesWithHugeToolResult();
        // ReservedOutputTokenFloor = 0 so the tiny 200-token test window is entirely available to the input; the
        // excerpted round then fits and is delivered (this test exercises per-round excerpting, not the over-window
        // rejection — that has its own test above).
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   OversizedToolResultExcerptChars = 40,
                   ReservedOutputTokenFloor = 0
               }))
        {
            // Simulates an inner tool-loop round: FunctionInvokingChatClient appended a huge tool result and called the
            // provider again. The boundary must re-budget it (the outer runner never sees this round).
            _ = await sut.GetResponseAsync(messages, SmallWindowOptions());
        }

        var received = inner.ReceivedMessageSets.Single();
        var pending = received.SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                              .First(content => string.Equals(content.CallId, "big", StringComparison.Ordinal));
        AssertEx.Contains(pending.Result?.ToString() ?? string.Empty, "[truncated:");
    }

    [Test]
    public async Task GetResponseAsync_WhenCumulativeCallCeilingExceeded_ThrowsTypedError()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "hi")
        };

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   MaxProviderCallsPerInvocation = 2
               }))
        {
            _ = await sut.GetResponseAsync(messages);
            _ = await sut.GetResponseAsync(messages);

            // The third round trips the cumulative call ceiling — a runaway loop is terminated with a typed error
            // BEFORE the provider is called, rather than hanging.
            _ = await AssertEx.ThrowsAsync<ProviderCallBudgetExceededException>(async () => await sut.GetResponseAsync(messages));
        }

        AssertEx.Equal(expected: 2, inner.ReceivedMessageSets.Count);
    }

    [Test]
    public async Task GetResponseAsync_WhenCumulativeTokenCeilingExceeded_ThrowsTypedError()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        // A single large round (~2004 estimated tokens) exceeds a tiny cumulative-token ceiling.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 8000))
        };
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   MaxCumulativeInputTokens = 1024,
                   DefaultContextTokens = 1_000_000
               }))
        {
            _ = await AssertEx.ThrowsAsync<ProviderCallBudgetExceededException>(async () => await sut.GetResponseAsync(messages, new ChatOptions()));
        }
    }

    [Test]
    public async Task GetResponseAsync_WhenInputFillsMostOfTheWindow_NarrowsTheReasoningBudgetToWhatIsLeft()
    {
        // The provider-side clamp only sees the launched window, so on a long conversation it still permits a budget
        // bigger than the tokens remaining after the prompt — and a reasoning phase that eats the remainder returns no
        // answer. This hop knows both numbers, so it narrows the marker to half of what the input actually leaves.
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        // ~1000 tokens of input (4 chars/token) against a 2000-token window: ~1000 left, so the budget becomes ~500 —
        // far below the 24576 a "high" effort carries.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 4000))
        };
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["num_ctx"] = 2000,
                [ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey] = 24576
            }
        };

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions { ReservedOutputTokenFloor = 0 }))
        {
            _ = await sut.GetResponseAsync(messages, options);
        }

        var sent = AssertEx.NotNull(inner.ReceivedOptions.Single());
        AssertEx.True(sent.AdditionalProperties!.TryGetValue<int>(ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey, out var narrowed));
        AssertEx.True(narrowed < 24576, "a budget larger than the window can hold must not survive this hop");
        AssertEx.True(narrowed <= 1000, $"the budget must fit the room the prompt leaves, but was {narrowed}");
        AssertEx.Equal(expected: 24576, options.AdditionalProperties[ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey],
            "the caller's options must not be mutated; narrowing clones");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheBudgetAlreadyFits_LeavesTheOptionsInstanceAlone()
    {
        // The overwhelming majority of rounds: a short prompt in a big window. Nothing should be cloned.
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        var messages = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["num_ctx"] = 65536,
                [ReasoningOptionsResolver.LlamaReasoningBudgetMarkerKey] = 2048
            }
        };

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions { ReservedOutputTokenFloor = 0 }))
        {
            _ = await sut.GetResponseAsync(messages, options);
        }

        AssertEx.True(ReferenceEquals(options, inner.ReceivedOptions.Single()),
            "a budget that already fits must pass the same options instance through");
    }

    [Test]
    public async Task GetResponseAsync_WhenRoundIrreduciblyExceedsWindow_ThrowsAndNeverCallsInner()
    {
        using var inner = new FailIfCalledChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        // A single oversized user message: not a tool result (cannot be excerpted) and the last/only message (cannot be
        // dropped), so the budgeter reduces nothing and the round stays over a tiny window — irreducible.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 8000))
        };
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 16,
                   ReservedOutputTokenFloor = 0
               }))
        {
            var exception = await AssertEx.ThrowsAsync<ProviderContextWindowExceededException>(async () => await sut.GetResponseAsync(messages, new ChatOptions()));

            // Bounded diagnostics are carried for logging; the surfaced Message is the fixed, path-free constant.
            AssertEx.True(exception.EstimatedTokens > exception.WindowTokens, "the estimate must exceed the window for an irreducible round");
            AssertEx.Equal(ProviderContextWindowExceededException.RoundExceedsWindowMessage, exception.Message);
        }

        // The provider must NEVER be called with a guaranteed-over-window round.
        AssertEx.False(inner.WasCalled, "the inner client must not be called when the round is irreducibly over the window");
    }

    [Test]
    public async Task GetResponseAsync_RejectsAgainstEffectiveNumCtxWindow_NotTheConfiguredDefault()
    {
        using var inner = new FailIfCalledChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        // The effective launched window (num_ctx AdditionalProperties, fed by the runtime's /props value) is
        // tiny while the configured default is huge. An irreducible round must be rejected against the EFFECTIVE window
        // — proving the propagated effective context, not the config default, bounds the round.
        const int EffectiveWindow = 32;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 8000))
        };
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["num_ctx"] = EffectiveWindow
            }
        };
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_000_000,
                   ReservedOutputTokenFloor = 0
               }))
        {
            var exception = await AssertEx.ThrowsAsync<ProviderContextWindowExceededException>(async () => await sut.GetResponseAsync(messages, options));
            // Sized off num_ctx (32, less the safety margin) rather than the 1,000,000 default — which is the point.
            AssertEx.Equal(TokenEstimatorCalibrationStore.ApplySafetyMargin(EffectiveWindow), exception.WindowTokens);
        }

        AssertEx.False(inner.WasCalled, "the round must be rejected against the effective window before the inner client is called.");
    }

    [Test]
    public async Task GetResponseAsync_WhenTheEstimateSitsJustUnderTheWindow_StillExcerpts()
    {
        // The margin's whole point. The char heuristic under-counts by roughly a tenth on markdown and JSON, so a round
        // estimated at ~0.9x the window is in truth AT or OVER it — and passing it through means a provider rejection
        // instead of a trim. Budgeting against TokenEstimatorCalibrationStore.EstimateSafetyFactor catches it.
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        // ~4 chars/token plus per-message framing: 3,400 characters lands near 900 estimated tokens against a 1,000
        // window — comfortably inside the old full-window budget, outside the 850 the margin allows.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "first"),
            new(ChatRole.Tool, new string('x', 3_400)),
            new(ChatRole.User, "latest")
        };
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_000,
                   ReservedOutputTokenFloor = 0,
                   OversizedToolResultExcerptChars = 100
               }))
        {
            _ = await sut.GetResponseAsync(messages, new ChatOptions());
        }

        var sent = inner.ReceivedMessageSets.Single();
        var toolText = sent.First(message => message.Role == ChatRole.Tool).Text;
        AssertEx.True(toolText.Length < 3_400, $"the oversized result must be excerpted inside the margin, was {toolText.Length} chars.");
        AssertEx.Contains(toolText, "[truncated:");
    }

    [Test]
    public async Task GetStreamingResponseAsync_WhenRoundIrreduciblyExceedsWindow_ThrowsAndNeverCallsInner()
    {
        using var inner = new FailIfCalledChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, new string('x', 8000))
        };
        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 16,
                   ReservedOutputTokenFloor = 0
               }))
        {
            await AssertEx.ThrowsAsync<ProviderContextWindowExceededException>(async () =>
            {
                await foreach (var _ in sut.GetStreamingResponseAsync(messages, new ChatOptions()).ConfigureAwait(false))
                {
                    // The budget rejection fires before the first chunk is pulled, so no update is ever yielded.
                }
            });
        }

        AssertEx.False(inner.WasCalled, "the inner client must not be streamed when the round is irreducibly over the window");
    }

    [Test]
    public async Task ApplyBudget_WithLargeToolSet_ReducesMessageBudgetAndTrims()
    {
        // The exact same messages fit the window with no tools, but a large tool set's serialized schemas count against
        // the same input window and push the round over — forcing a history drop that would not otherwise happen. This is
        // the under-count the tool-schema estimate fixes: ignoring options.Tools rounds a tool-heavy agent through. The
        // droppable message is deliberately large and the window sits ABOVE the post-drop total, so trimming brings the
        // round back under the window (a deliverable round) rather than leaving it irreducibly over — that over-window
        // case has its own dedicated rejection test below.
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "system prompt"),
            new(ChatRole.User, new string('a', 2000)),
            new(ChatRole.User, new string('b', 200)),
            new(ChatRole.User, new string('c', 200))
        };

        var budgetOptions = new ProviderCallBudgetOptions
        {
            // The window whose safety-margined budget is 650. Derived, not a bare 650: the budgeter measures against
            // EstimateSafetyFactor of the window, so a bare 650 would leave the tool-LESS control arm already trimming
            // and the comparison would prove nothing about the tool schemas.
            DefaultContextTokens = (int)Math.Ceiling(650 / TokenEstimatorCalibrationStore.EstimateSafetyFactor),
            ReservedOutputTokenFloor = 0,
            RecentMessagesToKeep = 2,
            OversizedToolResultExcerptChars = 100_000
        };

        int withoutToolsCount;
        using (var inner = new CapturingChatClient())
            using (var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance))
                using (ProviderCallBudget.BeginScope(budgetOptions))
                {
                    _ = await sut.GetResponseAsync(messages, new ChatOptions()).ConfigureAwait(false);
                    withoutToolsCount = inner.ReceivedMessageSets.Single().Count;
                }

        int withToolsCount;
        using (var inner = new CapturingChatClient())
            using (var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance))
                using (ProviderCallBudget.BeginScope(budgetOptions))
                {
                    _ = await sut.GetResponseAsync(messages, new ChatOptions
                    {
                        Tools = ManyTools(5)
                    }).ConfigureAwait(false);
                    withToolsCount = inner.ReceivedMessageSets.Single().Count;
                }

        AssertEx.Equal(messages.Count, withoutToolsCount);
        AssertEx.True(withToolsCount < withoutToolsCount, "counting the tool schemas must shrink the message budget and drop history the tool-less round kept");
    }

    [Test]
    public async Task GetResponseAsync_WithAmbientBudget_RecordsProviderAndToolSchemaCost()
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 16_384,
                   ReservedOutputTokenFloor = 0
               }))
        {
            _ = await sut.GetResponseAsync([new ChatMessage(ChatRole.User, "measure this round")], new ChatOptions
            {
                Tools = ManyTools(2)
            });
            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal(expected: 1, snapshot.ProviderCalls);
        AssertEx.True(snapshot.EstimatedInputTokens > 0);
        AssertEx.True(snapshot.ToolSchemaTokens > 0);
        AssertEx.True(snapshot.MaximumToolSchemaTokens > 0);
        AssertEx.True(snapshot.ProviderRoundElapsedMs >= 0);
    }

    [Test]
    public async Task GetStreamingResponseAsync_RecordsWholeProviderRoundLifetimeIncludingBackpressure()
    {
        using var inner = new TwoUpdateChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);
        ProviderCallEfficiencySnapshot snapshot;

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions()))
        {
            var updateCount = 0;
            await foreach (var update in sut.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "stream")]))
            {
                GC.KeepAlive(update);
                updateCount++;
                if (updateCount == 1)
                {
                    // The provider response remains open until the next pull. The round-elapsed metric intentionally
                    // includes this backpressure rather than reporting only active MoveNextAsync wait time.
                    await Task.Delay(TimeSpan.FromMilliseconds(40));
                }
            }

            snapshot = ProviderCallBudget.Current!.CaptureEfficiencySnapshot();
        }

        AssertEx.Equal(expected: 1, snapshot.ProviderCalls);
        AssertEx.True(snapshot.ProviderRoundElapsedMs >= 25,
            $"Expected provider-round elapsed time to include consumer backpressure; measured {snapshot.ProviderRoundElapsedMs:0.###} ms.");
    }

    [Test]
    public async Task ChatClientAgent_ForwardsTheTurnsModelIdDownToTheBudgetBoundary()
    {
        // FOLLOWUPS #5 suspected options.ModelId was null at this hop for a llama.cpp turn — which would have meant the
        // calibrated divisor never applied during the two recorded overflows, a cheaper root cause than any of this.
        // It is NOT null. InvocationAgentFactory sets ChatOptions.ModelId = definition.ModelId on the per-turn run
        // options, and MAF's ChatClientAgent CLONES those options and never clears ModelId (Microsoft.Agents.AI 1.17.0,
        // CreateConfiguredChatOptions merges the agent-level options INTO the clone and only fills ModelId when the
        // clone's is null). That agent hop is the only non-obvious link, so it is the one pinned here. The value that
        // arrives is also the exact string ModelRoutingLocalChatClient keys the deferred llama.cpp client on, which is
        // the string DeferredLlamaServerChatClient hands the calibration scheduler — so the divisor lookup and the
        // /tokenize write share a key by construction.
        using var inner = new CapturingChatClient();
        using var budgeted = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance);
        var agent = new ChatClientAgent(budgeted,
            instructions: null,
            name: "xe-worker",
            description: null,
            tools: null,
            loggerFactory: NullLoggerFactory.Instance,
            services: null);

        _ = await agent.RunAsync([new ChatMessage(ChatRole.User, "hi")],
                session: null,
                new ChatClientAgentRunOptions
                {
                    ChatOptions = new ChatOptions
                    {
                        ModelId = ModelId
                    }
                })
            .ConfigureAwait(false);

        AssertEx.Equal(ModelId, inner.ReceivedOptions.Single()?.ModelId);
    }

    [Test]
    public async Task ApplyBudget_WhenTheModelIsObservedToCostMoreThanEstimated_TrimsTheRoundEarlier()
    {
        // Identical history, identical window, identical estimator arithmetic. The only difference is that real rounds
        // of this model have been observed to cost half again what the char heuristic predicts, so the window the
        // estimate is measured against shrinks and more of the round's history goes before it is sent — instead of the
        // round reaching llama.cpp and coming back as an exceed_context_size_error the budgeter believed impossible.
        var neutral = new TokenEstimatorCalibrationStore();
        var calibrated = new TokenEstimatorCalibrationStore();
        calibrated.RecordObservedUsage(ModelId, estimatedTokens: 10_000, observedInputTokens: 15_000);

        var neutralDelivered = await DeliverLongRoundAsync(neutral).ConfigureAwait(false);
        var calibratedDelivered = await DeliverLongRoundAsync(calibrated).ConfigureAwait(false);

        AssertEx.True(calibratedDelivered < neutralDelivered,
            $"A model observed to cost more than estimated must trim earlier; delivered {calibratedDelivered} message(s) against {neutralDelivered} uncalibrated.");
    }

    [Test]
    public async Task GetResponseAsync_WithAmbientBudget_RecordsTheRoundsObservedInputTokens()
    {
        var store = new RecordingCalibrationStore();
        using var inner = new CapturingChatClient
        {
            ResponseUsage = new UsageDetails
            {
                InputTokenCount = 1_500
            }
        };
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance, store);

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_000_000,
                   ReservedOutputTokenFloor = 0
               }))
        {
            _ = await sut.GetResponseAsync(LongRound(), LargeWindowOptions()).ConfigureAwait(false);
        }

        // This hop is the only place holding both numbers for the SAME request: the estimate it just computed for the
        // message set it sent, and the provider's own count for that set.
        var observation = store.Observations.Single();
        AssertEx.Equal(ModelId, observation.ModelName);
        AssertEx.Equal(expected: 1_500L, observation.ObservedInputTokens);
        AssertEx.True(observation.EstimatedTokens > 0, "The recorded estimate must be the round's own estimated input, not zero.");
    }

    [Test]
    public async Task GetStreamingResponseAsync_WithAmbientBudget_RecordsTheTerminalUsageChunk()
    {
        var store = new RecordingCalibrationStore();
        using var inner = new CapturingChatClient
        {
            ResponseUsage = new UsageDetails
            {
                InputTokenCount = 2_048
            }
        };
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance, store);

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_000_000,
                   ReservedOutputTokenFloor = 0
               }))
        {
            await foreach (var update in sut.GetStreamingResponseAsync(LongRound(), LargeWindowOptions()).ConfigureAwait(false))
            {
                GC.KeepAlive(update);
            }
        }

        var observation = store.Observations.Single();
        AssertEx.Equal(ModelId, observation.ModelName);
        AssertEx.Equal(expected: 2_048L, observation.ObservedInputTokens);
    }

    [Test]
    public async Task GetResponseAsync_WithoutAmbientBudget_RecordsNothing()
    {
        var store = new RecordingCalibrationStore();
        using var inner = new CapturingChatClient
        {
            ResponseUsage = new UsageDetails
            {
                InputTokenCount = 1_500
            }
        };
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance, store);

        // The eval / preview-workflow runners drive the shared client with no scope. They are a pass-through, and a
        // pass-through has no estimate to pair the usage with, so they must teach the calibration nothing.
        _ = await sut.GetResponseAsync(LongRound(), LargeWindowOptions()).ConfigureAwait(false);

        AssertEx.Empty(store.Observations);
    }

    [Test]
    public async Task GetResponseAsync_WhenTheProviderReportsNoUsage_RecordsNothing()
    {
        var store = new RecordingCalibrationStore();
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance, store);

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_000_000,
                   ReservedOutputTokenFloor = 0
               }))
        {
            _ = await sut.GetResponseAsync(LongRound(), LargeWindowOptions()).ConfigureAwait(false);
        }

        AssertEx.Empty(store.Observations);
    }

    [Test]
    public async Task GetResponseAsync_WithNoModelId_RecordsNothing()
    {
        var store = new RecordingCalibrationStore();
        using var inner = new CapturingChatClient
        {
            ResponseUsage = new UsageDetails
            {
                InputTokenCount = 1_500
            }
        };
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance, store);

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_000_000,
                   ReservedOutputTokenFloor = 0
               }))
        {
            _ = await sut.GetResponseAsync(LongRound(), new ChatOptions()).ConfigureAwait(false);
        }

        // Calibration is per model. An unnamed round is sent, but it belongs to no key and teaches nothing.
        AssertEx.Empty(store.Observations);
    }

    private const string ModelId = "qwen3.8-27b:Q4_K_M";

    /// <summary>
    ///     Runs one long round through the boundary against <paramref name="store" /> and returns how many messages
    ///     actually reached the provider. The window is deliberately just under the round's estimate so the trim depth
    ///     is what the effective window decides.
    /// </summary>
    private static async Task<int> DeliverLongRoundAsync(ITokenEstimatorCalibrationStore store)
    {
        using var inner = new CapturingChatClient();
        using var sut = new ProviderCallBudgetChatClient(inner, NullLogger<ProviderCallBudgetChatClient>.Instance, store);

        using (ProviderCallBudget.BeginScope(new ProviderCallBudgetOptions
               {
                   DefaultContextTokens = 1_400,
                   ReservedOutputTokenFloor = 0,
                   RecentMessagesToKeep = 2
               }))
        {
            _ = await sut.GetResponseAsync(LongRound(),
                    new ChatOptions
                    {
                        ModelId = ModelId
                    })
                .ConfigureAwait(false);
        }

        return inner.ReceivedMessageSets.Single().Count;
    }

    /// <summary>Twelve plain user messages of ~100 estimated tokens each — a round large enough to be worth trimming.</summary>
    private static List<ChatMessage> LongRound()
    {
        var messages = new List<ChatMessage>(12);
        for (var index = 0; index < 12; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, new string((char)('a' + index), 400)));
        }

        return messages;
    }

    private static ChatOptions LargeWindowOptions()
    {
        return new ChatOptions
        {
            ModelId = ModelId,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["num_ctx"] = 1_000_000
            }
        };
    }

    private sealed record ObservedUsageWrite(string ModelName, long EstimatedTokens, long ObservedInputTokens);

    /// <summary>
    ///     A real store that also records every observation written to it, so a test can assert the exact triple the
    ///     boundary paired rather than only the correction it produced.
    /// </summary>
    private sealed class RecordingCalibrationStore : ITokenEstimatorCalibrationStore
    {
        private readonly TokenEstimatorCalibrationStore _inner = new();

        public List<ObservedUsageWrite> Observations { get; } = [];

        public int ResolveDivisor(string? modelName)
        {
            return _inner.ResolveDivisor(modelName);
        }

        public void SetDivisor(string modelName, int charsPerToken)
        {
            _inner.SetDivisor(modelName, charsPerToken);
        }

        public void RecordObservedUsage(string modelName, long estimatedTokens, long observedInputTokens)
        {
            Observations.Add(new ObservedUsageWrite(modelName, estimatedTokens, observedInputTokens));
            _inner.RecordObservedUsage(modelName, estimatedTokens, observedInputTokens);
        }

        public double ResolveObservedCorrection(string? modelName)
        {
            return _inner.ResolveObservedCorrection(modelName);
        }
    }

    private static IList<AITool> ManyTools(int count)
    {
        var tools = new List<AITool>(count);
        for (var index = 0; index < count; index++)
        {
            tools.Add(AIFunctionFactory.Create((string query) => query,
                name: $"search_documents_{index}",
                description: "Searches the indexed knowledge base and returns the most relevant passages for the supplied query string."));
        }

        return tools;
    }

    private static ChatOptions SmallWindowOptions()
    {
        return new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["num_ctx"] = 200
            }
        };
    }

    private static List<ChatMessage> ManyMessagesWithHugeToolResult()
    {
        return
        [
            new ChatMessage(ChatRole.System, "system prompt"),
            new ChatMessage(ChatRole.User, "please search"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("big", "search", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("big", new string('y', 4000))])
        ];
    }

    // Fails the test if the provider boundary ever forwards a round to it. Used to prove the inner client is NEVER
    // called for an irreducibly over-window round (both the sync and streaming paths reject before delegating).
    private sealed class FailIfCalledChatClient : IChatClient
    {
        public bool WasCalled { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("The inner client must not be called for an over-window round.");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            throw new InvalidOperationException("The inner client must not be streamed for an over-window round.");
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

    private sealed class CapturingChatClient : IChatClient
    {
        public List<IReadOnlyList<ChatMessage>> ReceivedMessageSets { get; } = [];

        public List<ChatOptions?> ReceivedOptions { get; } = [];

        /// <summary>
        ///     Terminal usage this fake reports for every round: on the response for the sync path, and as a
        ///     <see cref="UsageContent" /> on a final streamed update, which is the shape llama.cpp actually sends.
        ///     Null (the default) leaves every pre-existing fixture reporting no usage at all.
        /// </summary>
        public UsageDetails? ResponseUsage { get; set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ReceivedMessageSets.Add([.. messages]);
            ReceivedOptions.Add(options);
            return Task.FromResult(new ChatResponse
            {
                Usage = ResponseUsage
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            ReceivedMessageSets.Add([.. messages]);
            ReceivedOptions.Add(options);
            await Task.Yield();
            if (ResponseUsage is { } usage)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, (IList<AIContent>)[new UsageContent(usage)]);
            }
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

    private sealed class TwoUpdateChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ChatResponse());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "first");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "second");
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
