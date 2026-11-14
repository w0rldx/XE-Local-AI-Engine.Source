namespace XE_Local_AI_Engine.Tests.Proxy;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Proxy;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The behavior of the raw-model proxy forwarder: it provisions the requested local model and reverse-proxies the
///     request VERBATIM to the llama-server child's own OpenAI endpoint. The load-bearing guarantees under test are that
///     it forwards to the exact supervisor-provided loopback URL (no SSRF from the caller's path), that it is
///     llama.cpp-only and refuses any unknown/cloud model name with a 404 (so an external tool can never reach cloud
///     credentials), and that a busy runtime is a retryable 503 rather than a hang.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class LocalModelProxyForwarderTests
{
    private const string InstalledModel = "test-model";

    // Fields the Messages fold must carry through unchanged, unknown ones and a number's own spelling included.
    private const string SystemFoldTail = ",\"thinking\":{\"type\":\"adaptive\"},\"context_management\":{\"edits\":[{\"type\":\"clear\",\"keep\":1.50}]},\"stream\":true}";

    // Bodies every route must refuse: a repeated top-level name, and a repeated name inside a message object.
    private const string DuplicateTopLevelKey = "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"input\":\"hi\",\"messages\":[]}";
    private const string DuplicateKeyInAMessage = "{\"model\":\"test-model\",\"messages\":[{\"role\":\"system\",\"content\":\"S\",\"role\":\"user\"}],\"input\":\"hi\"}";

    private static readonly Uri ChildEndpoint = new("http://127.0.0.1:18100/v1");

    [Test]
    public async Task WriteModelsAsync_ProjectsTheInstalledLlamaCatalogAsAnOpenAiModelList()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext(body: string.Empty, out var responseBody);

        await forwarder.WriteModelsAsync(context);

        using var document = JsonDocument.Parse(responseBody.ToArray());
        AssertEx.Equal("list", document.RootElement.GetProperty("object").GetString());
        var data = document.RootElement.GetProperty("data");
        AssertEx.Equal(1, data.GetArrayLength());
        AssertEx.Equal(InstalledModel, data[0].GetProperty("id").GetString());
        AssertEx.Equal("model", data[0].GetProperty("object").GetString());
        AssertEx.Equal(LlamaServerProviderConstants.ProviderName, data[0].GetProperty("owned_by").GetString());
    }

    [Test]
    public async Task ForwardChatCompletions_WithAKnownModel_ForwardsVerbatimToTheChildAndStreamsTheResponseBack()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "text/event-stream", "data: hello\n\n");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}", out var responseBody);

        await forwarder.ForwardChatCompletionsAsync(context);

        _ = supervisor.Received(1).EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>());
        AssertEx.NotNull(upstream.LastRequest);
        AssertEx.Equal("http://127.0.0.1:18100/v1/chat/completions", upstream.LastRequest!.RequestUri!.AbsoluteUri);
        AssertEx.Equal("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}", upstream.LastRequestBody);
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.Equal("data: hello\n\n", Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    [Test]
    public async Task ForwardChatCompletions_PropagatesTheChildStatusCode()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.BadRequest, "application/json", "{\"error\":\"bad\"}");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WithAnUnknownModel_Returns404AndNeverProvisionsOrForwards()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        // A cloud-ish / unmapped model name is exactly the case that must never reach any other provider.
        var context = BuildContext("{\"model\":\"gpt-5.6-terra\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest);
        _ = supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ForwardChatCompletions_WithNoModelField_Returns400()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WithMalformedJson_Returns400()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("not json at all", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheRuntimeCannotProvision_Returns503WithRetryAfter()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns<Task<LlamaServerEndpoint>>(_ => throw new LlamaRuntimeException("At capacity."));
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Equal("5", context.Response.Headers.RetryAfter.ToString());
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheModelIsBeingEjected_Returns503()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.TryAcquireInferenceLease(InstalledModel, ModelRole.Chat).Returns(LlamaServerLeaseAcquisition.Evicting);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenProfilingOwnsTheKey_ReEnsuresAndForwardsAfterProfilingEnds()
    {
        // Profiling replaced the process between EnsureRunning and the lease lookup. Forwarding on the endpoint we
        // already resolved would reach the measurement process, whose port the spawn commonly inherits.
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.TryAcquireInferenceLease(InstalledModel, ModelRole.Chat)
                  .Returns(LlamaServerLeaseAcquisition.ProfilingOwned, LlamaServerLeaseAcquisition.NotRunning);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        _ = supervisor.Received(2).EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>());
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.NotNull(upstream.LastRequest);
    }

    [Test]
    public async Task ForwardChatCompletions_WhenProfilingNeverReleasesTheKey_Returns503WithoutForwarding()
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.TryAcquireInferenceLease(InstalledModel, ModelRole.Chat).Returns(LlamaServerLeaseAcquisition.ProfilingOwned);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Null(upstream.LastRequest, "Nothing may be forwarded to the measurement process.");
    }

    [Test]
    public async Task ForwardEmbeddings_TargetsTheEmbeddingRoleAndTheEmbeddingsSubpath()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{\"data\":[]}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Embedding, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint
                  {
                      ModelName = InstalledModel,
                      Role = ModelRole.Embedding,
                      BaseAddress = ChildEndpoint
                  });
        var context = BuildContext("{\"model\":\"test-model\",\"input\":\"hi\"}", out _);

        await forwarder.ForwardEmbeddingsAsync(context);

        _ = supervisor.Received(1).EnsureRunningAsync(InstalledModel, ModelRole.Embedding, Arg.Any<CancellationToken>());
        AssertEx.Equal("http://127.0.0.1:18100/v1/embeddings", upstream.LastRequest!.RequestUri!.AbsoluteUri);
    }

    [Test]
    public async Task ForwardChatCompletions_ToATwoSlotChatProcess_PinsTheForegroundSlotOverTheCallersOwn()
    {
        // The second slot is the node's background slot: an external request landing there runs beside a user turn and keeps its cells.
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint
                  {
                      ModelName = InstalledModel,
                      Role = ModelRole.Chat,
                      BaseAddress = ChildEndpoint,
                      ParallelSlots = LlamaServerChatSlots.Count
                  });
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"id_slot\":1}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        using var forwarded = JsonDocument.Parse(AssertEx.NotNull(upstream.LastRequestBody));
        AssertEx.Equal(LlamaServerChatSlots.Foreground, forwarded.RootElement.GetProperty("id_slot").GetInt32());
        AssertEx.Equal("test-model", forwarded.RootElement.GetProperty("model").GetString());
        AssertEx.Equal("hi", forwarded.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheChildConnectionFails_Returns503WithRetryAfter()
    {
        // The child exited/refused after provisioning — an HttpRequestException before any response bytes. This must map
        // to the same retryable 503 as an at-capacity spawn, not fall through to a generic 500.
        using var upstream = new ThrowingHandler();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        AssertEx.Equal("5", context.Response.Headers.RetryAfter.ToString());
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheUpstreamStreamStalls_AbortsWithinTheIdleDeadlineInsteadOfHanging()
    {
        // A child that sends response headers then goes silent WITHOUT closing the socket. Without the inter-read idle
        // deadline the forward would hang forever (infinite client timeout) and never release the inference lease.
        using var upstream = new StallingHandler();
        var forwarder = CreateForwarder(out _, out _, upstream, idleTimeout: TimeSpan.FromMilliseconds(150));
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        // WaitAsync turns a regression (the hang returning) into a fast, legible failure instead of a stuck test run.
        await forwarder.ForwardChatCompletionsAsync(context).WaitAsync(TimeSpan.FromSeconds(5));

        AssertEx.True(context.RequestAborted.IsCancellationRequested,
            "The idle watchdog must abort the request when the upstream goes silent, so the inference lease is released.");
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheUpstreamDiesMidEventStream_EndsWithAnErrorFrameAndDone()
    {
        // A forced eject kills the child mid-stream: the open body ends as HttpIOException(ResponseEnded). That is an
        // expected operator action, so the caller must get a reason and a stream terminator rather than a stream that
        // stops mid-token (and the node must not log it as an unhandled pipeline error).
        using var upstream = new DyingHandler("text/event-stream");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[],\"stream\":true}", out var responseBody);

        await forwarder.ForwardChatCompletionsAsync(context);

        var written = Encoding.UTF8.GetString(responseBody.ToArray());
        AssertEx.True(written.StartsWith(DyingStream.PartialLine, StringComparison.Ordinal),
            $"The bytes already relayed must survive. Got: {written}");
        // The child died mid-line, so the frame must open its OWN event: appended straight onto the partial line it
        // would read as "…\"content\":\"heldata: {\"error\"…" and no SSE parser would ever dispatch it.
        AssertEx.Contains(written, "\n\ndata: {\"error\":{\"message\":");
        AssertEx.Contains(written, "\"type\":\"server_error\"");
        AssertEx.True(written.EndsWith("data: [DONE]\n\n", StringComparison.Ordinal),
            $"An SSE client needs the stream terminator after the error frame. Got: {written}");
        AssertEx.False(written.Contains("finish_reason", StringComparison.Ordinal),
            "A finish_reason chunk would claim the generation completed cleanly, which it did not.");
        AssertEx.False(context.RequestAborted.IsCancellationRequested,
            "An event stream is ended deliberately, not aborted — the caller must be able to read the terminal frames.");
    }

    [Test]
    public async Task ForwardChatCompletions_WhenTheUpstreamDiesMidNonEventStream_AbortsInsteadOfWritingAFrame()
    {
        // A half-written JSON document cannot be repaired into a valid one, so the only honest end is the same abort
        // the idle watchdog uses.
        using var upstream = new DyingHandler("application/json");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out var responseBody);

        await forwarder.ForwardChatCompletionsAsync(context);

        AssertEx.True(context.RequestAborted.IsCancellationRequested,
            "A truncated JSON body must abort the connection rather than pretend to be a complete document.");
        AssertEx.False(Encoding.UTF8.GetString(responseBody.ToArray()).Contains("\"error\"", StringComparison.Ordinal),
            "No error envelope may be appended to a half-written JSON document.");
    }

    [Test]
    [Arguments("messages")]
    [Arguments("messages/count_tokens")]
    [Arguments("responses")]
    [Arguments("responses/input_tokens")]
    public async Task NativeWireFormatRoute_ForwardsVerbatimToItsOwnSubpathOnTheChatRoleAndRelaysTheEventStream(string route)
    {
        const string requestBody = "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}";
        const string streamed = "event: message_start\ndata: {\"type\":\"message_start\"}\n\n";
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "text/event-stream", streamed);
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        var context = BuildContext(requestBody, out var responseBody);

        await ForwardRouteAsync(forwarder, route, context);

        _ = supervisor.Received(1).EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>());
        AssertEx.Equal($"http://127.0.0.1:18100/v1/{route}", AssertEx.NotNull(upstream.LastRequest).RequestUri!.AbsoluteUri);
        AssertEx.Equal(requestBody, upstream.LastRequestBody);
        AssertEx.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        AssertEx.Equal("text/event-stream", context.Response.ContentType);
        AssertEx.Equal(streamed, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    [Test]
    [Arguments("messages")]
    [Arguments("responses")]
    public async Task NativeWireFormatRoute_ToATwoSlotChatProcess_PinsTheForegroundSlotOverTheCallersOwn(string route)
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint
                  {
                      ModelName = InstalledModel,
                      Role = ModelRole.Chat,
                      BaseAddress = ChildEndpoint,
                      ParallelSlots = LlamaServerChatSlots.Count
                  });
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[],\"input\":\"hi\",\"id_slot\":1}", out _);

        await ForwardRouteAsync(forwarder, route, context);

        using var forwarded = JsonDocument.Parse(AssertEx.NotNull(upstream.LastRequestBody));
        AssertEx.Equal(LlamaServerChatSlots.Foreground, forwarded.RootElement.GetProperty("id_slot").GetInt32());
        AssertEx.Equal("hi", forwarded.RootElement.GetProperty("input").GetString());
    }

    [Test]
    // Top-level system absent / a string / blocks, system-message content a string / blocks, one and several: folded in order.
    [Arguments("messages", "", "{\"role\":\"system\",\"content\":\"S1\"}", "[{\"type\":\"text\",\"text\":\"S1\"}]")]
    [Arguments("messages", ",\"system\":\"T\"", "{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"S1\",\"cache_control\":{\"type\":\"ephemeral\"}}]}",
        "[{\"type\":\"text\",\"text\":\"T\"},{\"type\":\"text\",\"text\":\"S1\",\"cache_control\":{\"type\":\"ephemeral\"}}]")]
    [Arguments("messages", ",\"system\":[{\"type\":\"text\",\"text\":\"T1\"},{\"type\":\"text\",\"text\":\"T2\"}]",
        "{\"role\":\"system\",\"content\":\"S1\"},{\"role\":\"assistant\",\"content\":\"A\"},{\"role\":\"system\",\"content\":[{\"type\":\"text\",\"text\":\"S2\"}]}",
        "[{\"type\":\"text\",\"text\":\"T1\"},{\"type\":\"text\",\"text\":\"T2\"},{\"type\":\"text\",\"text\":\"S1\"},{\"type\":\"text\",\"text\":\"S2\"}]")]
    [Arguments("messages/count_tokens", ",\"system\":\"T\"", "{\"role\":\"system\",\"content\":\"S1\"}",
        "[{\"type\":\"text\",\"text\":\"T\"},{\"type\":\"text\",\"text\":\"S1\"}]")]
    public async Task MessagesSurface_FoldsSystemRoleMessagesIntoTheTopLevelSystemAndKeepsEveryOtherField(string route, string system, string systemMessages, string expectedSystem)
    {
        // Claude Code sends a system-role message after a user turn; a template that wants the system message first answers 500.
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext($"{{\"model\":\"test-model\"{system},\"messages\":[{{\"role\":\"user\",\"content\":\"hi\"}},{systemMessages}]{SystemFoldTail}", out _);

        await ForwardRouteAsync(forwarder, route, context);

        using var forwarded = JsonDocument.Parse(AssertEx.NotNull(upstream.LastRequestBody));
        var root = forwarded.RootElement;
        using var expected = JsonDocument.Parse(expectedSystem);
        AssertEx.True(JsonElement.DeepEquals(expected.RootElement, root.GetProperty("system")),
            $"Got system: {root.GetProperty("system").GetRawText()}");
        var messages = root.GetProperty("messages").EnumerateArray().ToList();
        AssertEx.False(messages.Exists(message => message.GetProperty("role").GetString() == "system"), "No system-role message may stay in messages.");
        AssertEx.Equal("hi", messages[0].GetProperty("content").GetString());
        AssertEx.Equal("test-model", root.GetProperty("model").GetString());
        AssertEx.Equal("{\"edits\":[{\"type\":\"clear\",\"keep\":1.50}]}", root.GetProperty("context_management").GetRawText());
        AssertEx.Equal("{\"type\":\"adaptive\"}", root.GetProperty("thinking").GetRawText());
        AssertEx.True(root.GetProperty("stream").GetBoolean());
        AssertEx.False(root.TryGetProperty("id_slot", out _), "A single-slot process gets no slot pin.");
    }

    [Test]
    // No system-role message, or one this fold cannot read: the child gets the caller's exact bytes.
    [Arguments("messages", "{\"model\":\"test-model\",\"system\":\"T\",\"messages\":[{\"role\":\"user\",\"content\":\"h\\u00e9\"}],\"keep\":1.50}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"},{\"role\":\"system\",\"content\":7}]}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"system\":7,\"messages\":[{\"role\":\"user\",\"content\":\"hi\"},{\"role\":\"system\",\"content\":\"S\"}]}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"messages\":[\"hi\",null,{\"role\":7}]}")]
    [Arguments("messages/count_tokens", "{\"model\":\"test-model\",\"messages\":[{\"role\":\"user\",\"content\":\"h\\u00e9\"}],\"keep\":1.50}")]
    public async Task MessagesSurface_WithNothingToFold_ForwardsTheOriginalBytes(string route, string body)
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext(body, out _);

        await ForwardRouteAsync(forwarder, route, context);

        AssertEx.Equal(body, upstream.LastRequestBody);
    }

    [Test]
    public async Task MessagesSurface_ToATwoSlotChatProcess_FoldsSystemMessagesAndPinsTheForegroundSlot()
    {
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>())
                  .Returns(new LlamaServerEndpoint
                  {
                      ModelName = InstalledModel,
                      Role = ModelRole.Chat,
                      BaseAddress = ChildEndpoint,
                      ParallelSlots = LlamaServerChatSlots.Count
                  });
        var context = BuildContext("{\"model\":\"test-model\",\"system\":\"T\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"},{\"role\":\"system\",\"content\":\"S1\"}],\"id_slot\":1}", out _);

        await ForwardRouteAsync(forwarder, "messages", context);

        using var forwarded = JsonDocument.Parse(AssertEx.NotNull(upstream.LastRequestBody));
        AssertEx.Equal(LlamaServerChatSlots.Foreground, forwarded.RootElement.GetProperty("id_slot").GetInt32());
        AssertEx.Equal("[{\"type\":\"text\",\"text\":\"T\"},{\"type\":\"text\",\"text\":\"S1\"}]", forwarded.RootElement.GetProperty("system").GetRawText());
        AssertEx.Equal(1, forwarded.RootElement.GetProperty("messages").GetArrayLength());
    }

    [Test]
    [Arguments("messages", "no-model", StatusCodes.Status400BadRequest, "invalid_request_error")]
    [Arguments("messages", "malformed", StatusCodes.Status400BadRequest, "invalid_request_error")]
    [Arguments("messages", "unknown-model", StatusCodes.Status404NotFound, "not_found_error")]
    [Arguments("messages", "cannot-provision", StatusCodes.Status503ServiceUnavailable, "overloaded_error")]
    [Arguments("messages", "connection-refused", StatusCodes.Status503ServiceUnavailable, "overloaded_error")]
    [Arguments("messages/count_tokens", "unknown-model", StatusCodes.Status404NotFound, "not_found_error")]
    [Arguments("messages/count_tokens", "cannot-provision", StatusCodes.Status503ServiceUnavailable, "overloaded_error")]
    public async Task MessagesSurface_ErrorsTheProxyWritesItself_UseTheAnthropicEnvelope(string route, string failure, int status, string anthropicType)
    {
        using var context = await RunFailureAsync(route, failure);

        AssertEx.Equal(status, context.Http.Response.StatusCode);
        var root = context.Body.RootElement;
        AssertEx.Equal("error", root.GetProperty("type").GetString(), "An Anthropic client reads the top-level type first.");
        AssertEx.Equal(anthropicType, root.GetProperty("error").GetProperty("type").GetString());
        AssertEx.False(string.IsNullOrWhiteSpace(root.GetProperty("error").GetProperty("message").GetString()), "The envelope must carry a reason.");
        AssertEx.False(root.GetProperty("error").TryGetProperty("code", out _), "The OpenAI code field does not belong in the Anthropic envelope.");
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            AssertEx.Equal("5", context.Http.Response.Headers.RetryAfter.ToString());
        }
    }

    [Test]
    [Arguments("responses", "no-model", StatusCodes.Status400BadRequest, "invalid_request_error")]
    [Arguments("responses", "unknown-model", StatusCodes.Status404NotFound, "invalid_request_error")]
    [Arguments("responses", "cannot-provision", StatusCodes.Status503ServiceUnavailable, "server_error")]
    [Arguments("responses/input_tokens", "unknown-model", StatusCodes.Status404NotFound, "invalid_request_error")]
    [Arguments("chat/completions", "unknown-model", StatusCodes.Status404NotFound, "invalid_request_error")]
    [Arguments("chat/completions", "cannot-provision", StatusCodes.Status503ServiceUnavailable, "server_error")]
    public async Task OpenAiSurfaces_ErrorsTheProxyWritesItself_KeepTheOpenAiEnvelope(string route, string failure, int status, string openAiType)
    {
        using var context = await RunFailureAsync(route, failure);

        AssertEx.Equal(status, context.Http.Response.StatusCode);
        var root = context.Body.RootElement;
        AssertEx.False(root.TryGetProperty("type", out _), "The OpenAI envelope has no top-level type.");
        AssertEx.Equal(openAiType, root.GetProperty("error").GetProperty("type").GetString());
        AssertEx.True(root.GetProperty("error").TryGetProperty("code", out _), "The OpenAI envelope carries a code field.");
    }

    [Test]
    [Arguments("messages", "{\"model\":\"test-model\"}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"messages\":null}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"messages\":\"hi\"}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"messages\":{\"role\":\"user\"}}")]
    [Arguments("messages", "{\"model\":\"test-model\",\"Messages\":[]}")]
    [Arguments("messages/count_tokens", "{\"model\":\"test-model\",\"input\":\"hi\"}")]
    public async Task MessagesSurface_WithoutAMessagesArray_Answers400ItselfAndNeverProvisionsOrForwards(string route, string body)
    {
        // The child answers a missing "messages" with a 500 server_error; a client would retry what can never succeed.
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        var context = BuildContext(body, out var responseBody);

        await ForwardRouteAsync(forwarder, route, context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var document = JsonDocument.Parse(responseBody.ToArray());
        AssertEx.Equal("invalid_request_error", document.RootElement.GetProperty("error").GetProperty("type").GetString());
        AssertEx.Null(upstream.LastRequest);
        _ = supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [Arguments("chat/completions", DuplicateTopLevelKey)]
    [Arguments("chat/completions", DuplicateKeyInAMessage)]
    [Arguments("embeddings", DuplicateTopLevelKey)]
    [Arguments("embeddings", DuplicateKeyInAMessage)]
    [Arguments("messages", DuplicateTopLevelKey)]
    [Arguments("messages", DuplicateKeyInAMessage)]
    [Arguments("messages/count_tokens", DuplicateTopLevelKey)]
    [Arguments("messages/count_tokens", DuplicateKeyInAMessage)]
    [Arguments("responses", DuplicateTopLevelKey)]
    [Arguments("responses", DuplicateKeyInAMessage)]
    [Arguments("responses/input_tokens", DuplicateTopLevelKey)]
    [Arguments("responses/input_tokens", DuplicateKeyInAMessage)]
    public async Task EveryProxyRoute_WithARepeatedPropertyName_Answers400InItsOwnEnvelopeAndNeverProvisionsOrForwards(string route, string body)
    {
        // A JsonObject throws a non-JSON exception on a repeated name, so a rewrite of such a body would end as an unhandled 500.
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        var context = BuildContext(body, out var responseBody);

        await ForwardRouteAsync(forwarder, route, context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        using var document = JsonDocument.Parse(responseBody.ToArray());
        AssertEx.Equal("invalid_request_error", document.RootElement.GetProperty("error").GetProperty("type").GetString());
        AssertEx.Equal(route.StartsWith("messages", StringComparison.Ordinal), document.RootElement.TryGetProperty("type", out _),
            "Only the Anthropic envelope has a top-level type.");
        AssertEx.Null(upstream.LastRequest);
        _ = supervisor.DidNotReceive().EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MessagesSurface_WithManySystemEntries_FoldsEveryOneInOrderAndKeepsTheRestInOrder()
    {
        const int pairs = 20_000;
        var entries = Enumerable.Range(0, pairs).Select(static i => $"{{\"role\":\"user\",\"content\":\"U{i}\"}},{{\"role\":\"system\",\"content\":\"S{i}\"}}");
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext($"{{\"model\":\"test-model\",\"messages\":[{string.Join(',', entries)}]}}", out _);

        await forwarder.ForwardMessagesAsync(context);

        using var forwarded = JsonDocument.Parse(AssertEx.NotNull(upstream.LastRequestBody));
        var system = forwarded.RootElement.GetProperty("system").EnumerateArray().Select(static block => block.GetProperty("text").GetString()).ToList();
        var messages = forwarded.RootElement.GetProperty("messages").EnumerateArray().Select(static message => message.GetProperty("content").GetString()).ToList();
        AssertEx.True(Enumerable.Range(0, pairs).Select(static i => $"S{i}").SequenceEqual(system), "Every system entry must be folded, in order.");
        AssertEx.True(Enumerable.Range(0, pairs).Select(static i => $"U{i}").SequenceEqual(messages), "Every other entry must stay, in order.");
    }

    [Test]
    [Arguments(503, 17, "17")]
    [Arguments(503, 0, "5")]
    [Arguments(529, 30, "30")]
    [Arguments(529, 0, "5")]
    [Arguments(500, 17, "")]
    public async Task MessagesSurface_UpstreamOverloadedError_KeepsARetryAfterHintWhenReWrapped(int status, int upstreamRetryAfterSeconds, string expectedRetryAfter)
    {
        // An upstream Retry-After of 0 here means "the child sent none".
        using var upstream = new CapturingHandler((HttpStatusCode)status, "application/json", "{\"error\":{\"message\":\"Loading model\"}}")
        {
            RetryAfter = upstreamRetryAfterSeconds > 0 ? new RetryConditionHeaderValue(TimeSpan.FromSeconds(upstreamRetryAfterSeconds)) : null
        };
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out _);

        await forwarder.ForwardMessagesAsync(context);

        AssertEx.Equal(status, context.Response.StatusCode);
        AssertEx.Equal(expectedRetryAfter, context.Response.Headers.RetryAfter.ToString());
    }

    [Test]
    [Arguments("responses")]
    [Arguments("chat/completions")]
    public async Task OpenAiSurfaces_DoNotRequireAMessagesArray(string route)
    {
        using var upstream = Idle();
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"input\":\"hi\"}", out _);

        await ForwardRouteAsync(forwarder, route, context);

        AssertEx.NotNull(upstream.LastRequest, "Only the Messages surface requires a messages array.");
    }

    [Test]
    [Arguments("messages", 400, "{\"error\":{\"code\":400,\"message\":\"[json.exception.parse_error.101] bad\",\"type\":\"invalid_request_error\"}}", "invalid_request_error",
        "[json.exception.parse_error.101] bad")]
    [Arguments("messages", 400,
        "{\"error\":{\"code\":400,\"message\":\"request (26408 tokens) exceeds the available context size (16384 tokens), try increasing it\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":26408,\"n_ctx\":16384}}",
        "invalid_request_error", "prompt is too long: 26408 tokens > 16384 maximum")]
    [Arguments("messages", 400, "{\"error\":{\"message\":\"too big\",\"type\":\"exceed_context_size_error\"}}", "invalid_request_error", "prompt is too long: too big")]
    [Arguments("messages/count_tokens", 400, "{\"error\":{\"code\":400,\"message\":\"request (9 tokens) exceeds\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":9,\"n_ctx\":8}}",
        "invalid_request_error", "prompt is too long: 9 tokens > 8 maximum")]
    [Arguments("messages", 500, "{\"error\":{\"code\":500,\"message\":\"boom\",\"type\":\"server_error\"}}", "api_error", "boom")]
    [Arguments("messages", 503, "{\"error\":{\"code\":503,\"message\":\"Loading model\",\"type\":\"unavailable_error\"}}", "overloaded_error", "Loading model")]
    [Arguments("messages", 404, "{\"error\":\"File Not Found\"}", "not_found_error", "File Not Found")]
    [Arguments("messages", 500, "not json", "api_error", "The local model runtime answered HTTP 500.")]
    public async Task MessagesSurface_UpstreamJsonError_IsReWrappedIntoTheAnthropicEnvelopeKeepingTheStatus(string route,
        int status,
        string upstreamBody,
        string anthropicType,
        string message)
    {
        using var upstream = new CapturingHandler((HttpStatusCode)status, "application/json", upstreamBody);
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out var responseBody);

        await ForwardRouteAsync(forwarder, route, context);

        AssertEx.Equal(status, context.Response.StatusCode);
        using var document = JsonDocument.Parse(responseBody.ToArray());
        AssertEx.Equal("error", document.RootElement.GetProperty("type").GetString());
        AssertEx.Equal(anthropicType, document.RootElement.GetProperty("error").GetProperty("type").GetString());
        AssertEx.Equal(message, document.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Test]
    [Arguments("responses")]
    [Arguments("chat/completions")]
    public async Task OpenAiSurfaces_UpstreamJsonError_IsRelayedVerbatim(string route)
    {
        const string upstreamBody = "{\"error\":{\"code\":400,\"message\":\"too long\",\"type\":\"exceed_context_size_error\",\"n_prompt_tokens\":9,\"n_ctx\":8}}";
        using var upstream = new CapturingHandler(HttpStatusCode.BadRequest, "application/json", upstreamBody);
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"input\":\"hi\"}", out var responseBody);

        await ForwardRouteAsync(forwarder, route, context);

        AssertEx.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        AssertEx.Equal(upstreamBody, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    [Test]
    public async Task MessagesSurface_SuccessfulJsonResponse_IsRelayedVerbatim()
    {
        // The model field echoes the child's GGUF path; the proxy never rewrites a body it did not author.
        const string upstreamBody = "{\"id\":\"chatcmpl-1\",\"type\":\"message\",\"model\":\"/models/test.gguf\",\"content\":[]}";
        using var upstream = new CapturingHandler(HttpStatusCode.OK, "application/json", upstreamBody);
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[]}", out var responseBody);

        await forwarder.ForwardMessagesAsync(context);

        AssertEx.Equal(upstreamBody, Encoding.UTF8.GetString(responseBody.ToArray()));
    }

    [Test]
    [Arguments("messages", "{\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":")]
    [Arguments("responses", "{\"type\":\"error\",\"code\":\"server_error\",\"message\":")]
    public async Task NativeWireFormatRoute_WhenTheUpstreamDiesMidEventStream_EndsWithTheSurfacesOwnErrorEvent(string route, string expectedDataPrefix)
    {
        using var upstream = new DyingHandler("text/event-stream");
        var forwarder = CreateForwarder(out _, out _, upstream);
        var context = BuildContext("{\"model\":\"test-model\",\"messages\":[],\"input\":\"hi\",\"stream\":true}", out var responseBody);

        await ForwardRouteAsync(forwarder, route, context);

        var written = Encoding.UTF8.GetString(responseBody.ToArray());
        AssertEx.True(written.StartsWith(DyingStream.PartialLine, StringComparison.Ordinal), $"The bytes already relayed must survive. Got: {written}");
        AssertEx.Contains(written, "\n\nevent: error\ndata: " + expectedDataPrefix);
        AssertEx.False(written.Contains("[DONE]", StringComparison.Ordinal), "Neither the Messages nor the Responses stream protocol has a [DONE] terminator.");
        AssertEx.True(written.EndsWith("\n\n", StringComparison.Ordinal), "The error event must be dispatched, so it ends on a blank line.");
        var dataLine = written.Split('\n').Last(static line => line.StartsWith("data: ", StringComparison.Ordinal));
        using var frame = JsonDocument.Parse(dataLine["data: ".Length..]);
        AssertEx.Equal("error", frame.RootElement.GetProperty("type").GetString());
        AssertEx.False(context.RequestAborted.IsCancellationRequested, "An event stream is ended deliberately, not aborted.");
    }

    private static Task ForwardRouteAsync(LocalModelProxyForwarder forwarder, string route, HttpContext context) =>
        route switch
        {
            "chat/completions" => forwarder.ForwardChatCompletionsAsync(context),
            "embeddings" => forwarder.ForwardEmbeddingsAsync(context),
            "messages" => forwarder.ForwardMessagesAsync(context),
            "messages/count_tokens" => forwarder.ForwardMessagesCountTokensAsync(context),
            "responses" => forwarder.ForwardResponsesAsync(context),
            "responses/input_tokens" => forwarder.ForwardResponsesInputTokensAsync(context),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, "No forwarder entry point for this route.")
        };

    /// <summary>Drives one route into a failure the proxy answers itself, and parses what it wrote.</summary>
    private static async Task<FailureResult> RunFailureAsync(string route, string failure)
    {
        HttpMessageHandler upstream = failure == "connection-refused" ? new ThrowingHandler() : Idle();
        var forwarder = CreateForwarder(out _, out var supervisor, upstream);
        if (failure == "cannot-provision")
        {
            supervisor.EnsureRunningAsync(InstalledModel, ModelRole.Chat, Arg.Any<CancellationToken>())
                      .Returns<Task<LlamaServerEndpoint>>(_ => throw new LlamaRuntimeException("At capacity."));
        }

        var body = failure switch
        {
            "no-model" => "{\"messages\":[]}",
            "malformed" => "not json at all",
            "unknown-model" => "{\"model\":\"claude-sonnet-5-5\",\"messages\":[]}",
            _ => "{\"model\":\"test-model\",\"messages\":[]}"
        };
        var context = BuildContext(body, out var responseBody);

        try
        {
            await ForwardRouteAsync(forwarder, route, context);
        }
        finally
        {
            upstream.Dispose();
        }

        return new FailureResult
        {
            Http = context,
            Body = JsonDocument.Parse(responseBody.ToArray())
        };
    }

    private sealed class FailureResult : IDisposable
    {
        public required HttpContext Http { get; init; }

        public required JsonDocument Body { get; init; }

        public void Dispose()
        {
            Body.Dispose();
        }
    }

    private static CapturingHandler Idle()
    {
        return new CapturingHandler(HttpStatusCode.OK, "application/json", "{}");
    }

    private static LocalModelProxyForwarder CreateForwarder(out IGgufModelStore ggufStore,
        out ILlamaServerProcessSupervisor supervisor,
        HttpMessageHandler upstream,
        TimeSpan? idleTimeout = null)
    {
        ggufStore = Substitute.For<IGgufModelStore>();
        ggufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
                 .Returns<IReadOnlyList<LocalModelDescriptor>>(_ => [InstalledDescriptor()]);

        supervisor = Substitute.For<ILlamaServerProcessSupervisor>();
        supervisor.EnsureRunningAsync(Arg.Any<string>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
                  .Returns(callInfo => new LlamaServerEndpoint
                  {
                      ModelName = callInfo.ArgAt<string>(0),
                      Role = callInfo.ArgAt<ModelRole>(1),
                      BaseAddress = ChildEndpoint
                  });

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        // disposeHandler:false — the test owns the handler via `using var`, so the client must not dispose it too.
        httpClientFactory.CreateClient(LocalModelProxyForwarder.HttpClientName)
                         .Returns(_ => new HttpClient(upstream, disposeHandler: false));

        return new LocalModelProxyForwarder(ggufStore, SubstitutedRuntimeOrchestration.Over(supervisor), httpClientFactory, NullLogger<LocalModelProxyForwarder>.Instance, idleTimeout);
    }

    private static LocalModelDescriptor InstalledDescriptor()
    {
        return new LocalModelDescriptor
        {
            ModelName = InstalledModel,
            ProviderName = LlamaServerProviderConstants.ProviderName,
            IsAvailable = true,
            SizeBytes = 1024,
            ModifiedAt = DateTimeOffset.UnixEpoch,
            MaxContextTokens = 4096
        };
    }

    private static DefaultHttpContext BuildContext(string body, out MemoryStream responseBody)
    {
        var context = new DefaultHttpContext();
        // A lifetime feature so context.Abort() (used by the idle watchdog) is a well-defined no-op under test rather
        // than an NRE, and so RequestAborted is a real, uncancelled token.
        context.Features.Set<IHttpRequestLifetimeFeature>(new StubRequestLifetime());
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Request.ContentType = "application/json";
        responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        return context;
    }

    private sealed class StubRequestLifetime : IHttpRequestLifetimeFeature
    {
        private bool _aborted;

        // A fresh read after Abort() reports cancellation; the forwarder captured RequestAborted (uncancelled) at entry,
        // so its in-flight token is unaffected — exactly the real feature's contract, without owning a disposable CTS.
        public CancellationToken RequestAborted
        {
            get => _aborted ? new CancellationToken(canceled: true) : CancellationToken.None;
            set => _ = value;
        }

        public void Abort()
        {
            _aborted = true;
        }
    }

    /// <summary>Stands in for the llama-server child: captures the forwarded request and returns a canned response.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _contentType;
        private readonly string _body;

        public CapturingHandler(HttpStatusCode statusCode, string contentType, string body)
        {
            _statusCode = statusCode;
            _contentType = contentType;
            _body = body;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        public RetryConditionHeaderValue? RetryAfter { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            response.Headers.RetryAfter = RetryAfter;
            return response;
        }
    }

    /// <summary>Stands in for a child that exited or refused the connection: the send itself fails.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"));
        }
    }

    /// <summary>Stands in for a child that sends response headers then goes silent without closing the socket.</summary>
    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new StallingStream())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }

    /// <summary>Stands in for a child killed MID-RESPONSE (forced eject): some bytes arrive, then the body ends prematurely.</summary>
    private sealed class DyingHandler : HttpMessageHandler
    {
        private readonly string _contentType;

        public DyingHandler(string contentType)
        {
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DyingStream())
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            return Task.FromResult(response);
        }
    }

    /// <summary>
    ///     Yields one chunk, then fails the way a killed llama-server's open body does — the exact shape
    ///     <see cref="XE_Local_AI_Engine.Tests.Providers.LlamaServer.DeferredLlamaServerChatClientServerGoneTests" />
    ///     pins as "the server is gone".
    /// </summary>
    private sealed class DyingStream : Stream
    {
        // A PARTIAL SSE line: a child is killed wherever it happens to be, which is far more often mid-line than on
        // an event boundary. Anything appended without its own terminator is swallowed into this broken event.
        internal const string PartialLine = "data: {\"choices\":[{\"delta\":{\"content\":\"hel";

        private static readonly byte[] FirstChunk = Encoding.UTF8.GetBytes(PartialLine);

        private bool _delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_delivered)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            }

            _delivered = true;
            FirstChunk.CopyTo(buffer);
            return ValueTask.FromResult(FirstChunk.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>A readable stream whose reads never complete until the read's own token is cancelled (the idle deadline).</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
