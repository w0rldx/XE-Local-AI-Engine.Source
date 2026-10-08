namespace XE_Local_AI_Engine.Tests.Providers.OpenAICompat;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompat;
using XE_Local_AI_Engine.Providers.OpenAICompat.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     End-to-end wire behavior of one external model's chat client, driven through the REAL production stack
///     (registry → endpoint guard → MEAI OpenAI chat-completions adapter → reasoning rewriting) over a recording
///     transport. Every assertion is on the serialized request that actually left the process, because the things being
///     pinned here — which header is present, which body field is set — are exactly the ones an options-level
///     assertion would report as correct while the assembled pipeline did something else.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ExternalOpenAiChatClientWireTests
{
    [Test]
    public async Task Send_UsesTheConnectionsWireIdAndNormalizedEndpoint()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.LastRequest.Uri?.AbsoluteUri);
        using var body = JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
        // The RAW wire id goes on the wire — never the namespaced ext: id, which is a node-internal routing key.
        AssertEx.Equal(ExternalProviderTestData.WireId, body.RootElement.GetProperty("model").GetString());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Send_WhenTheCallerOptionsCarryTheNamespacedId_SendsTheWireIdAndLeavesTheOptionsUntouched(bool streaming)
    {
        // Every chat, agent and work-session turn passes options whose ModelId is the node's ext: id; it used to
        // override the adapter's wire-id default, so the endpoint was asked for a model it has never heard of.
        var recorder = new OpenAiWireRecorder
        {
            Responder = _ => streaming ? OpenAiWireRecorder.Stream("{\"content\":\"ok\"}") : OpenAiWireRecorder.Completion("ok")
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);
        var options = new ChatOptions
        {
            ModelId = ExternalProviderTestData.ModelId
        };

        if (streaming)
        {
            _ = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None)
                            .ToChatResponseAsync(CancellationToken.None);
        }
        else
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        }

        using var body = JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
        AssertEx.Equal(ExternalProviderTestData.WireId, body.RootElement.GetProperty("model").GetString());
        AssertEx.Equal(ExternalProviderTestData.ModelId, options.ModelId);
    }

    [Test]
    public async Task Stream_EndingInTheUsageOnlyChunk_CompletesAndRecordsTheUsage()
    {
        // The standard include_usage terminal chunk carries "choices": [] plus "usage". Reading the reasoning patch path
        // on it indexed choices[0] inside the SDK and failed the whole turn after the answer had already streamed.
        const string Chunk = "{\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"m\",\"system_fingerprint\":\"b1\",";
        var sse = "data: " + Chunk + "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"pong\"}}]}\n\n"
                  + "data: " + Chunk + "\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{}}]}\n\n"
                  + "data: " + Chunk + "\"choices\":[],\"usage\":{\"completion_tokens\":8,\"prompt_tokens\":31,\"total_tokens\":39},\"timings\":{\"predicted_n\":8}}\n\n"
                  + "data: [DONE]\n\n";
        var recorder = new OpenAiWireRecorder
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            }
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var response = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None)
                                   .ToChatResponseAsync(CancellationToken.None);

        AssertEx.Equal("pong", response.Text);
        AssertEx.Equal(39L, AssertEx.NotNull(response.Usage).TotalTokenCount);
    }

    [Test]
    public async Task Send_WithAConfiguredKey_CarriesTheBearerHeader_AndWithoutOneCarriesNoAuthorizationAtAll()
    {
        var keyed = new OpenAiWireRecorder();
        var keyedRegistry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(), apiKey: "sk-unsloth-abc");
        using (var client = new ExternalOpenAiChatClient(keyedRegistry, ExternalProviderTestData.ModelId, keyed.CreateHandler))
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        }

        var keyless = new OpenAiWireRecorder();
        var keylessRegistry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using (var client = new ExternalOpenAiChatClient(keylessRegistry, ExternalProviderTestData.ModelId, keyless.CreateHandler))
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        }

        AssertEx.Equal("Bearer sk-unsloth-abc", keyed.LastRequest.Authorization);
        AssertEx.False(keyless.LastRequest.HasAuthorizationHeader, "a keyless connection must send no Authorization header.");
    }

    [Test]
    [Arguments(true, "medium", null, "medium")]
    // The turn's selection beats the model's registered default.
    [Arguments(true, "medium", "low", "low")]
    // Clamped to the interoperable low|medium|high set: minimal->low, xhigh->high.
    [Arguments(true, null, "minimal", "low")]
    [Arguments(true, null, "xhigh", "high")]
    [Arguments(true, null, "high", "high")]
    public async Task Send_WhenTheModelDeclaresEffortSupport_SetsReasoningEffortOnTheBody(bool supportsEffort,
        string? defaultEffort,
        string? selectedEffort,
        string expected)
    {
        var body = await SendWithEffortAsync(supportsEffort, defaultEffort, selectedEffort);

        AssertEx.Equal(expected, body.RootElement.GetProperty("reasoning_effort").GetString());
        body.Dispose();
    }

    [Test]
    // No declared effort support: the field never rides, whatever the turn selected.
    [Arguments(false, "high", "high")]
    // Explicit off and the binary "reason by default" sentinel are NOT graded levels — omitting the field is not the
    // same as sending "none", which some servers read as an instruction of its own.
    [Arguments(true, null, "none")]
    [Arguments(true, null, "on")]
    // An unrecognized selection sends nothing rather than silently falling back to the registered default.
    [Arguments(true, "high", "bogus")]
    // Nothing selected and nothing declared.
    [Arguments(true, null, null)]
    public async Task Send_WhenNoGradedEffortApplies_OmitsTheFieldEntirely(bool supportsEffort, string? defaultEffort, string? selectedEffort)
    {
        var body = await SendWithEffortAsync(supportsEffort, defaultEffort, selectedEffort);

        AssertEx.False(body.RootElement.TryGetProperty("reasoning_effort", out _), "no graded effort applies, so the field must be absent.");
        body.Dispose();
    }

    [Test]
    public async Task Send_NeverSerializesTheInProcessEffortMarker()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(supportsEffort: true));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ExternalProviderConstants.ReasoningEffortMarkerKey] = "high"
            }
        };

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);

        AssertEx.False(AssertEx.NotNull(recorder.LastRequest.Body).Contains("xe.external", StringComparison.Ordinal),
            "the in-process marker must never reach the wire.");
    }

    [Test]
    public async Task SecondTurn_DoesNotReplayTheFirstTurnsReasoningToTheServer()
    {
        // v1 decision: reasoning is never replayed. Chat Completions drops historical TextReasoningContent by design
        // (the same behavior the llama.cpp path has today), and this test is what stops a future change from silently
        // starting to ship a model's private thinking back to a remote endpoint on every follow-up turn.
        var recorder = new OpenAiWireRecorder
        {
            Responder = static index => index == 0
                ? OpenAiWireRecorder.Completion("first answer", "\"reasoning_content\":\"PRIVATE-THOUGHT\"")
                : OpenAiWireRecorder.Completion("second answer")
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        AssertEx.Contains(first.Messages[0].Contents, static content => content is TextReasoningContent { Text: "PRIVATE-THOUGHT" });

        List<ChatMessage> history = [new ChatMessage(ChatRole.User, "hi"), .. first.Messages, new ChatMessage(ChatRole.User, "again")];
        _ = await client.GetResponseAsync(history, options: null, CancellationToken.None);

        var replayed = AssertEx.NotNull(recorder.LastRequest.Body);
        AssertEx.False(replayed.Contains("PRIVATE-THOUGHT", StringComparison.Ordinal),
            "the assistant's reasoning must not be replayed to the server on a later turn.");
        AssertEx.Contains(replayed, "first answer");
    }

    [Test]
    public async Task Send_AfterTheConnectionBaseUrlChanges_RebuildsAgainstTheNewEndpoint()
    {
        // An operator edit must take effect on the next send; a cached adapter bound to the old address would keep
        // talking to an endpoint they have already moved away from.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18100"), ExternalProviderTestData.Model());
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("http://127.0.0.1:18099/v1/chat/completions", recorder.Requests[0].Uri?.AbsoluteUri);
        AssertEx.Equal("http://127.0.0.1:18100/v1/chat/completions", recorder.Requests[1].Uri?.AbsoluteUri);
    }

    [Test]
    public async Task Send_AfterTheApiKeyIsRotated_PresentsTheNewKey()
    {
        // The cached adapter's identity used to exclude the credential, so rotating or clearing a key changed nothing
        // the comparison could see and the previous key kept going on the wire — which an operator experiences as "I
        // fixed the key and it still fails", or, for a revoked key, as one that keeps working.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(), apiKey: "sk-old");
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(), apiKey: "sk-new");
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal("Bearer sk-old", recorder.Requests[0].Authorization);
        AssertEx.Equal("Bearer sk-new", recorder.Requests[1].Authorization);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Send_WithCustomHeaders_CarriesTheBearerAndEveryHeader(bool streaming)
    {
        var recorder = new OpenAiWireRecorder
        {
            Responder = _ => streaming ? OpenAiWireRecorder.Stream("{\"content\":\"ok\"}") : OpenAiWireRecorder.Completion("ok")
        };
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(),
            apiKey: "demo-jwt",
            headers: [new("X-Demo-Project", "demo"), new("X-Demo-Secret", "s3cret")]);
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        if (streaming)
        {
            _ = await client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None)
                            .ToChatResponseAsync(CancellationToken.None);
        }
        else
        {
            _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        }

        var sent = recorder.LastRequest;
        AssertEx.Equal("Bearer demo-jwt", sent.Authorization);
        AssertEx.Equal("demo", sent.Headers.GetValueOrDefault("X-Demo-Project"));
        AssertEx.Equal("s3cret", sent.Headers.GetValueOrDefault("X-Demo-Secret"));
    }

    [Test]
    public async Task Send_OnAKeylessConnectionWithCustomHeaders_SendsTheHeadersButNoAuthorization()
    {
        // A gateway that wants its token under another name gets it as a secret header with the key blank: then no
        // Authorization header may appear at all.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(),
            apiKey: null,
            headers: [new("X-Demo-Token", "demo-jwt")]);
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.False(recorder.LastRequest.HasAuthorizationHeader);
        AssertEx.Equal("demo-jwt", recorder.LastRequest.Headers.GetValueOrDefault("X-Demo-Token"));
    }

    [Test]
    public async Task Send_AfterAHeaderValueIsRotated_PresentsTheNewValue()
    {
        // Same hole as the key rotation above: the headers join the adapter identity by value, or an edited gateway
        // token would keep the previous one on the wire until something else evicted the adapter.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(),
            headers: [new("X-Demo-Project", "old")]);
        var builds = 0;
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, () =>
        {
            builds++;
            return recorder.CreateHandler();
        });

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        // A fresh but equal list must NOT rebuild the adapter; only the value edit below does.
        registry.Replace(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(), headers: [new("X-Demo-Project", "old")]);
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        AssertEx.Equal(1, builds, "an equal header list is the same endpoint identity");
        registry.Replace(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model(), headers: [new("X-Demo-Project", "new")]);
        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal(2, builds);
        AssertEx.Equal("old", recorder.Requests[0].Headers.GetValueOrDefault("X-Demo-Project"));
        AssertEx.Equal("old", recorder.Requests[1].Headers.GetValueOrDefault("X-Demo-Project"));
        AssertEx.Equal("new", recorder.Requests[2].Headers.GetValueOrDefault("X-Demo-Project"));
    }

    [Test]
    public async Task Send_WhenTheConnectionFlipsLocalToCloudMidInvocation_AbortsRatherThanRedirecting()
    {
        // The mid-invocation swap. The turn's tools were authorized against a declared-LOCAL connection — workspace,
        // knowledge base, custom tools, run_python — and a later round of the same tool loop would carry them, and the
        // node-local data already in their results, to an endpoint that never earned them.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(ExternalProviderTestData.ModelId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(locality: ExternalProviderLocality.Cloud), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WhenTheBaseUrlMovesMidInvocation_AbortsRatherThanRedirecting()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(ExternalProviderTestData.ModelId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(baseUrl: "http://attacker.example.com"), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WhenAnUnrelatedEditBumpsTheGeneration_StillSends()
    {
        // The pin compares the FACTS, not just the generation. Another connection's save moves the registry epoch, and
        // aborting every in-flight turn on an unrelated edit would be an availability bug, not a safety property.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(ExternalProviderTestData.ModelId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        // Same locality, same base address, new generation.
        registry.Replace(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WhenOnlyTheBasePathMovesMidInvocation_AbortsRatherThanRedirecting()
    {
        // Same host, same port, different prefix — two OpenAI-compatible services routinely sit behind one origin.
        // Pinning only the origin let this one through, so a pinned turn's later sends, carrying tools authorized
        // against the FIRST service's declaration, went to the second.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099/v1"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(ExternalProviderTestData.ModelId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099/proxy/v1"), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WhenOnlyTheBasePathCaseChangesMidInvocation_Aborts()
    {
        // Paths are case-sensitive routes on the servers this pins against ("/Tenant/v1" and "/tenant/v1" can be two
        // services), and Uri normalization lower-cases only scheme and host — so the pin's ordinal comparison must
        // treat a case-only path move as a change, not noise.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099/Tenant/v1"),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(ExternalProviderTestData.ModelId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(baseUrl: "http://127.0.0.1:18099/tenant/v1"), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WhenThePinWasCreatedFromACaseVariantId_IsStillPinned()
    {
        // The complement of the lookup-canonicalization case below: here the PIN is minted from the non-canonical
        // spelling (as a producer reading the NOCASE provider map would), and the client holds the canonical one. The
        // pin record canonicalizes at creation, so the two must still meet.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var variantId = ExternalProviderTestData.ModelId.Replace(ExternalProviderTestData.ConnectionId, "UNSLOTH-BOX", StringComparison.Ordinal);
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(variantId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(locality: ExternalProviderLocality.Cloud), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WhenTheClientHoldsACaseVariantOfThePinnedId_IsStillPinned()
    {
        // The client's id comes from the provider map, whose key is NOCASE, while the pin carries the registry's
        // canonical spelling. An ordinal lookup missed, the send read as "not part of a pinned invocation", and the
        // mid-invocation trust flip below then went through under the weaker unpinned check.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        var variantId = ExternalProviderTestData.ModelId.Replace(ExternalProviderTestData.ConnectionId, "UNSLOTH-BOX", StringComparison.Ordinal);
        using var client = new ExternalOpenAiChatClient(registry, variantId, recorder.CreateHandler);

        var pinned = AssertEx.NotNull(await registry.TryResolveBindingAsync(ExternalProviderTestData.ModelId, CancellationToken.None));
        using var pin = ExternalProviderBindingPinScope.Begin(new ExternalProviderBindingPin(ExternalProviderTestData.ModelId,
            pinned.Generation,
            pinned.Locality,
            pinned.BaseAddress));

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(locality: ExternalProviderLocality.Cloud), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Equal(1, recorder.Requests.Count);
    }

    [Test]
    public async Task Send_WithNoPin_WhenTheConnectionEscalatesToLocal_Aborts()
    {
        // Unpinned contexts (a background summarization, a health-adjacent send) have no tool offer to invalidate, so
        // they resolve live — but a connection that was Cloud when this client resolved it and is Local now has become
        // MORE privileged underneath a live client, and reusing it is refused.
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(locality: ExternalProviderLocality.Cloud),
            ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None);
        registry.Replace(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());

        _ = await AssertEx.ThrowsAsync<ExternalProviderBindingChangedException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));
    }

    [Test]
    public async Task Send_WhenTheModelIsNoLongerRegistered_FailsClosedInsteadOfGuessingAnEndpoint()
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(), ExternalProviderTestData.Model());
        using var client = new ExternalOpenAiChatClient(registry, "ext:deleted-box/some-model", recorder.CreateHandler);

        _ = await AssertEx.ThrowsAsync<ExternalProviderModelUnavailableException>(() =>
            client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options: null, CancellationToken.None));

        AssertEx.Empty(recorder.Requests);
    }

    private static async Task<JsonDocument> SendWithEffortAsync(bool supportsEffort, string? defaultEffort, string? selectedEffort)
    {
        var recorder = new OpenAiWireRecorder();
        var registry = new FakeExternalProviderRegistry().Add(ExternalProviderTestData.Connection(),
            ExternalProviderTestData.Model(supportsEffort, defaultEffort, supportsReasoning: true));
        using var client = new ExternalOpenAiChatClient(registry, ExternalProviderTestData.ModelId, recorder.CreateHandler);

        ChatOptions? options = null;
        if (selectedEffort is not null)
        {
            options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ExternalProviderConstants.ReasoningEffortMarkerKey] = selectedEffort
                }
            };
        }

        _ = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], options, CancellationToken.None);
        return JsonDocument.Parse(AssertEx.NotNull(recorder.LastRequest.Body));
    }
}
