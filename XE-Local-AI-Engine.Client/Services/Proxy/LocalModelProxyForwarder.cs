namespace XE_Local_AI_Engine.Client.Services.Proxy;

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The inbound model proxy (OpenAI, OpenAI Responses, Anthropic Messages): provisions the requested local model, then reverse-proxies the
///     caller's request VERBATIM and streams the response back byte-for-byte.
/// </summary>
/// <remarks>
///     <b>Why byte-for-byte:</b> a llama-server child already speaks all three wire protocols natively, so forwarding raw bytes is both the smallest
///     implementation and the strongest guarantee the caller asked for — none of the node's chat orchestration (persona, tools, memory, RAG, sampling
///     defaults) sits on this path. Response bodies are never rewritten (a reply's <c>model</c> is the child's GGUF path); a request changes only by the
///     <c>id_slot</c> pin and, on Messages, by <see cref="FoldSystemMessages" />. Only the proxy's own errors and a Messages error take the caller's envelope.
/// </remarks>
internal sealed class LocalModelProxyForwarder
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient" /> for forwarding. Infinite timeout: a long generation must not be cut off by a client timeout; caller disconnect and the inter-read idle watchdog below are the cancellation signals instead.</summary>
    public const string HttpClientName = "LocalModelProxyForwarder";

    private const string JsonContentType = "application/json";

    private const int StreamCopyBufferSize = 16 * 1024;

    private const string UpstreamGoneMessage =
        "The local model runtime stopped while streaming this response (it may have been ejected). Try again shortly.";

    /// <summary>
    ///     How many times a forwarded request re-ensures around a profiling spawn before answering 503. Profiling holds
    ///     the per-key single-flight gate through its own teardown, so one re-ensure normally suffices.
    /// </summary>
    private const int MaxProfilingReEnsures = 3;

    /// <summary>
    ///     Default maximum gap between two reads from the upstream child while streaming a response.
    /// </summary>
    /// <remarks>
    ///     A child that stops producing bytes WITHOUT closing the socket would otherwise wedge the forward forever — the forwarding client has an infinite
    ///     timeout and only caller disconnect would cancel it — leaving the inference lease held so a graceful eject can never drain the model. Mirrors the
    ///     invocation path's <c>StreamIdleTimeoutSeconds</c> (60s) default, so both streaming surfaces bound a silent runtime the same way.
    /// </remarks>
    private static readonly TimeSpan DefaultUpstreamIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Terminal frames for an event-stream response whose upstream child vanished mid-stream, the expected shape of
    ///     an operator force-eject.
    /// </summary>
    /// <remarks>
    ///     Mirrors the OpenAI error envelope <see cref="WriteErrorAsync" /> writes before the headers are sent — same <c>message</c>/<c>type</c>/<c>code</c>
    ///     fields — so a client sees ONE error shape whether the failure landed before or after the status. NOT a <c>finish_reason: "stop"</c> chunk: the
    ///     generation did not complete, and saying it did would be a lie the caller cannot detect. It leads with a blank-line terminator because the child can
    ///     die MID-LINE, where appending onto a partial <c>data: {"cho</c> would swallow the frame into that broken event; on an event boundary the extra
    ///     blank lines cost nothing, since an event with no data is not dispatched.
    /// </remarks>
    private static readonly byte[] UpstreamGoneSseFrames = Encoding.UTF8.GetBytes(
        $"\n\ndata: {{\"error\":{{\"message\":\"{UpstreamGoneMessage}\",\"type\":\"server_error\",\"code\":null}}}}\n\ndata: [DONE]\n\n");

    /// <summary>The Anthropic streaming <c>error</c> event for the same mid-stream death; that protocol has no <c>[DONE]</c> terminator.</summary>
    private static readonly byte[] AnthropicUpstreamGoneSseFrames = Encoding.UTF8.GetBytes(
        $"\n\nevent: error\ndata: {{\"type\":\"error\",\"error\":{{\"type\":\"api_error\",\"message\":\"{UpstreamGoneMessage}\"}}}}\n\n");

    /// <summary>
    ///     The Responses streaming <c>error</c> event for the same mid-stream death. No <c>sequence_number</c>: the
    ///     upstream numbers none of its own events, so any value here would be invented.
    /// </summary>
    private static readonly byte[] ResponsesUpstreamGoneSseFrames = Encoding.UTF8.GetBytes(
        $"\n\nevent: error\ndata: {{\"type\":\"error\",\"code\":\"server_error\",\"message\":\"{UpstreamGoneMessage}\",\"param\":null}}\n\n");

    private readonly IGgufModelStore _ggufModelStore;
    private readonly LlamaCppRuntimeOrchestrationService _runtime;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LocalModelProxyForwarder> _logger;
    private readonly TimeSpan _upstreamIdleTimeout;

    /// <param name="upstreamIdleTimeout">
    ///     Overrides <see cref="DefaultUpstreamIdleTimeout" />. Optional so the DI container binds the default; tests
    ///     pass a small value to exercise the idle watchdog without waiting a real minute.
    /// </param>
    public LocalModelProxyForwarder(IGgufModelStore ggufModelStore,
        LlamaCppRuntimeOrchestrationService runtime,
        IHttpClientFactory httpClientFactory,
        ILogger<LocalModelProxyForwarder> logger,
        TimeSpan? upstreamIdleTimeout = null)
    {
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _upstreamIdleTimeout = upstreamIdleTimeout ?? DefaultUpstreamIdleTimeout;
    }

    /// <summary>Handles <c>GET proxy/v1/models</c> — synthesizes an OpenAI model list from the installed llama.cpp GGUF catalog.</summary>
    public async Task WriteModelsAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.RequestAborted;

        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct);
        var data = installed
                   .Where(model => model.IsAvailable)
                   .Select(model => new
                   {
                       id = model.ModelName,
                       @object = "model",
                       created = model.ModifiedAt?.ToUnixTimeSeconds() ?? 0L,
                       owned_by = LlamaServerProviderConstants.ProviderName
                   })
                   .ToArray();

        await context.Response.WriteAsJsonAsync(new
        {
            @object = "list",
            data
        }, ct);
    }

    /// <summary>The client wire format a proxied route speaks; it picks only the shape of the errors the proxy writes itself.</summary>
    private enum WireFormat
    {
        OpenAi,
        AnthropicMessages,
        OpenAiResponses
    }

    /// <summary>Handles <c>POST proxy/v1/chat/completions</c>.</summary>
    public Task ForwardChatCompletionsAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Chat, "chat/completions", WireFormat.OpenAi);

    /// <summary>Handles <c>POST proxy/v1/embeddings</c>.</summary>
    public Task ForwardEmbeddingsAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Embedding, "embeddings", WireFormat.OpenAi);

    /// <summary>Handles <c>POST proxy/v1/messages</c> (Anthropic Messages, served natively by the child).</summary>
    public Task ForwardMessagesAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Chat, "messages", WireFormat.AnthropicMessages);

    /// <summary>Handles <c>POST proxy/v1/messages/count_tokens</c>.</summary>
    public Task ForwardMessagesCountTokensAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Chat, "messages/count_tokens", WireFormat.AnthropicMessages);

    /// <summary>Handles <c>POST proxy/v1/responses</c> (OpenAI Responses, served natively by the child).</summary>
    public Task ForwardResponsesAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Chat, "responses", WireFormat.OpenAiResponses);

    /// <summary>Handles <c>POST proxy/v1/responses/input_tokens</c>.</summary>
    public Task ForwardResponsesInputTokensAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Chat, "responses/input_tokens", WireFormat.OpenAiResponses);

    /// <summary>
    ///     Provisions the model, leases it for the request's lifetime, and forwards the exchange.
    /// </summary>
    /// <remarks>
    ///     Provisioning and leasing are not atomic, so the lease result also says whether that endpoint is still ours. <c>NotRunning</c>
    ///     proceeds leaseless: <c>EnsureRunning</c> just returned an endpoint, and the upstream call self-evidences liveness. <c>Evicting</c> means the
    ///     operator is draining this process — refuse. <c>ProfilingOwned</c> means a measurement spawn took the key in between, and the allocator commonly
    ///     hands it the port the replaced process just freed, so this endpoint may BE the measurement: re-ensure, parking on the per-key gate profiling holds
    ///     through teardown, bounded so repeated measurements answer retryably.
    /// </remarks>
    private async Task ForwardAsync(HttpContext context, ModelRole role, string upstreamPath, WireFormat format)
    {
        var ct = context.RequestAborted;

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        if (!TryReadRequest(body, out var request, out var model))
        {
            await WriteErrorAsync(context, format, StatusCodes.Status400BadRequest,
                "Request body must be JSON with a non-empty \"model\" field and no repeated property names.", "invalid_request_error", ct);
            return;
        }

        // The child answers a missing "messages" with a 500; the Messages API calls that a 400, so the proxy says so itself.
        if (format == WireFormat.AnthropicMessages && request["messages"] is not JsonArray)
        {
            await WriteErrorAsync(context, format, StatusCodes.Status400BadRequest,
                "Request body must carry a \"messages\" array.", "invalid_request_error", ct);
            return;
        }

        var rewritten = format == WireFormat.AnthropicMessages && FoldSystemMessages(request);

        // Existence check against the local GGUF catalog is what makes this llama.cpp-only and refuses cloud model
        // names — an unknown or cloud model is a 404 here rather than a route to any other provider.
        if (!await IsInstalledLlamaModelAsync(model, ct))
        {
            await WriteErrorAsync(context, format, StatusCodes.Status404NotFound,
                $"The model '{model}' does not exist as a local model on this node.", "invalid_request_error", ct);
            return;
        }

        // Provision, then take an inference lease for the request's lifetime so an operator eject drains this request instead of
        // tree-killing it mid-stream. The two steps are not atomic — see this method's remarks for what each lease outcome means.
        LlamaServerEndpoint endpoint;
        ILlamaServerInferenceLease? acquired;
        var profilingReEnsures = 0;
        while (true)
        {
            try
            {
                endpoint = await _runtime.EnsureRunningAsync(model, role, ct);
            }
            catch (LlamaRuntimeException ex)
            {
                // Spawn failed, the loaded-model cap was reached, or restart-backoff was exceeded. The message is already
                // sanitized by the supervisor. A busy/at-capacity node is a retryable condition, not a permanent failure.
                _logger.LogWarning(ex, "Model proxy could not provision model {Model} for {Role}.", model, role);
                await WriteBusyAsync(context, format, "The local runtime could not load the requested model right now (it may be at capacity). Try again shortly.", ct);
                return;
            }

            var acquisition = _runtime.TryAcquireInferenceLease(model, role);
            if (acquisition.ProcessEvicting)
            {
                await WriteBusyAsync(context, format, "The requested model is being ejected by the operator. Try again shortly.", ct);
                return;
            }

            if (!acquisition.ProcessProfiling)
            {
                acquired = acquisition.Lease;
                break;
            }

            if (profilingReEnsures++ >= MaxProfilingReEnsures)
            {
                await WriteBusyAsync(context, format, "The requested model is being profiled by a benchmark right now. Try again shortly.", ct);
                return;
            }
        }

        using var lease = acquired;

        // A multi-slot chat process keeps its second slot for the node's background work (LlamaServerChatSlots): an external caller takes the
        // foreground slot over its own id_slot, as a node chat turn does, so it never runs beside a turn or leaves its full-window prompt there.
        if (role == ModelRole.Chat && endpoint.ParallelSlots > 1)
        {
            request["id_slot"] = LlamaServerChatSlots.Foreground;
            rewritten = true;
        }

        var upstreamUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri.TrimEnd('/')}/{upstreamPath}");
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
        {
            Content = new ByteArrayContent(rewritten ? JsonSerializer.SerializeToUtf8Bytes(request) : body)
        };
        upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", JsonContentType);

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await httpClient
                .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The child exited after EnsureRunningAsync, or refused/reset before any response bytes: the same "runtime unavailable" condition as an
            // at-capacity spawn, so answer the SAME retryable 503, not a generic 500. A caller disconnect surfaces as OperationCanceledException.
            _logger.LogWarning(ex, "Model proxy could not reach the llama-server child for model {Model} ({Role}).", model, role);
            await WriteBusyAsync(context, format, "The local model runtime is temporarily unavailable. Try again shortly.", ct);
            return;
        }

        using (upstreamResponse)
        {
            // The child's Messages errors use the OpenAI envelope; an Anthropic client reads only its own. A small,
            // complete JSON body (never a stream) is the one case the proxy re-wraps instead of relaying bytes.
            if (format == WireFormat.AnthropicMessages
                && !upstreamResponse.IsSuccessStatusCode
                && upstreamResponse.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                var statusCode = (int)upstreamResponse.StatusCode;
                // Overloaded keeps a retry hint, as WriteBusyAsync gives one: the child's own when it sent one.
                if (statusCode is StatusCodes.Status503ServiceUnavailable or 529)
                {
                    context.Response.Headers.RetryAfter = upstreamResponse.Headers.RetryAfter?.ToString() ?? "5";
                }

                var upstreamError = await upstreamResponse.Content.ReadAsByteArrayAsync(ct);
                await WriteErrorAsync(context, format, statusCode, ReadUpstreamErrorMessage(upstreamError, statusCode), "invalid_request_error", ct);
                return;
            }

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            if (upstreamResponse.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }

            // Stream as it arrives: without disabling the write buffer an SSE (stream:true) response would be batched and
            // the caller would not see tokens incrementally.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
            await PumpWithIdleDeadlineAsync(upstreamStream, context, model, upstreamPath, format, ct);
        }
    }

    /// <summary>
    ///     Copies the upstream stream to the caller with a per-read idle deadline.
    /// </summary>
    /// <remarks>
    ///     A read returning no bytes within <see cref="_upstreamIdleTimeout" /> aborts the exchange, so the enclosing <c>using</c> lease is released and a
    ///     graceful eject can drain the model — the naive <see cref="Stream.CopyToAsync(Stream, CancellationToken)" /> would wait forever on a silent-but-open
    ///     child. Writes flow under the caller token, so a slow CLIENT cannot trip the upstream-idle timer; only the upstream read carries the deadline. A
    ///     child that DIES mid-stream — a forced eject is an operator action, not an error — ends the read with the "server is gone" family the provider
    ///     recognizes, which ends the exchange deliberately; see <see cref="EndAfterUpstreamGoneAsync" />.
    /// </remarks>
    private async Task PumpWithIdleDeadlineAsync(Stream upstream,
        HttpContext context,
        string model,
        string upstreamPath,
        WireFormat format,
        CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(StreamCopyBufferSize);
        try
        {
            while (true)
            {
                int read;
                using (var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idleCts.CancelAfter(_upstreamIdleTimeout);
                    try
                    {
                        read = await upstream.ReadAsync(buffer.AsMemory(0, StreamCopyBufferSize), idleCts.Token);
                    }
                    catch (OperationCanceledException) when (idleCts.IsCancellationRequested && !ct.IsCancellationRequested)
                    {
                        // Upstream went silent without closing the socket. The response has started, so its status can no longer
                        // become 503: abort, so the caller sees a broken stream rather than a hang and the inference lease is released.
                        context.Abort();
                        return;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && LlamaServerConnectionFailure.IsServerGone(ex))
                    {
                        // The child died mid-stream, typically a forced eject. Propagating it would reach the pipeline AFTER the response
                        // started, logging an unhandled ERROR and cutting the stream mid-token — see EndAfterUpstreamGoneAsync's remarks.
                        await EndAfterUpstreamGoneAsync(context, model, upstreamPath, format, ex, ct);
                        return;
                    }
                }

                if (read == 0)
                {
                    return;
                }

                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                await context.Response.Body.FlushAsync(ct);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    ///     Ends an exchange whose upstream child vanished after the response started.
    /// </summary>
    /// <remarks>
    ///     The status is already on the wire and can no longer become a 503, so the contract is per body shape: an event stream gets the surface's own
    ///     terminal error event (OpenAI adds <c>[DONE]</c>), so an SSE client sees a reason instead of a truncated stream, and anything else — a
    ///     half-written JSON document that cannot be repaired — is aborted, like the idle-deadline arm. The caller's token is checked alongside the
    ///     "server is gone" match, as both sites in <c>DeferredLlamaServerChatClient</c> do: a disconnected caller aborts the same read.
    /// </remarks>
    private async Task EndAfterUpstreamGoneAsync(HttpContext context,
        string model,
        string upstreamPath,
        WireFormat format,
        Exception ex,
        CancellationToken ct)
    {
        _logger.LogWarning(ex, "Model proxy lost the llama-server child for model {Model} while streaming {UpstreamPath}; ending the response.", model, upstreamPath);

        if (context.Response.ContentType?.StartsWith(MediaTypeNames.Text.EventStream, StringComparison.OrdinalIgnoreCase) != true)
        {
            context.Abort();
            return;
        }

        try
        {
            var frames = format switch
            {
                WireFormat.AnthropicMessages => AnthropicUpstreamGoneSseFrames,
                WireFormat.OpenAiResponses => ResponsesUpstreamGoneSseFrames,
                _ => UpstreamGoneSseFrames
            };
            await context.Response.Body.WriteAsync(frames, ct);
            await context.Response.Body.FlushAsync(ct);
        }
        catch (Exception writeFailure) when (writeFailure is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The caller went away too. There is nobody left to tell, and throwing here would re-create the very
            // unhandled post-header exception this method exists to prevent.
            _logger.LogDebug(writeFailure, "Model proxy could not deliver the terminal frame for model {Model}; the caller is gone as well.", model);
        }
    }

    /// <summary>
    ///     Whether the requested name is a model the local GGUF catalog actually holds.
    /// </summary>
    /// <remarks>
    ///     <b>llama.cpp only, and no SSRF.</b> The model must exist in the catalog or the request 404s, which both
    ///     keeps the proxy strictly on the default local runtime and refuses any cloud model name, so an external tool
    ///     can never route through the operator's cloud credentials. The upstream URI is derived solely from the
    ///     supervisor-provided loopback endpoint plus a FIXED subpath; the caller's own path never influences the
    ///     target.
    /// </remarks>
    private async Task<bool> IsInstalledLlamaModelAsync(string model, CancellationToken ct)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct);
        return installed.Any(descriptor =>
            descriptor.IsAvailable && string.Equals(descriptor.ModelName, model, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Moves every <c>role: "system"</c> entry of <c>messages</c>, in order, onto the end of the top-level <c>system</c> text blocks.
    /// </summary>
    /// <remarks>
    ///     Claude Code sends system entries after a user turn; the child hands them to the chat template where they stand, and templates that require the
    ///     system message first answer 500. Count-tokens renders the same template. A body with no system entry, or a <c>system</c>/<c>content</c> that is
    ///     neither a string nor an array, is left untouched (false), so the child gets the original bytes and answers them as before.
    /// </remarks>
    private static bool FoldSystemMessages(JsonObject request)
    {
        var messages = request["messages"]!.AsArray();
        var systemMessages = messages.Where(IsSystemMessage).ToList();
        if (systemMessages.Count == 0)
        {
            return false;
        }

        var system = new JsonArray();
        if (!TryAppendTextBlocks(system, request["system"]) || !systemMessages.TrueForAll(message => TryAppendTextBlocks(system, message!["content"])))
        {
            return false;
        }

        messages.RemoveAll(IsSystemMessage);
        request["system"] = system;
        return true;
    }

    private static bool IsSystemMessage(JsonNode? message) =>
        message is JsonObject entry
        && entry["role"]?.GetValueKind() == JsonValueKind.String
        && entry["role"]!.GetValue<string>() == "system";

    // A string becomes one text block; an array's blocks are copied as they are; absent adds nothing.
    private static bool TryAppendTextBlocks(JsonArray target, JsonNode? content)
    {
        switch (content?.GetValueKind())
        {
            case null:
                return true;
            case JsonValueKind.String:
                target.Add(new JsonObject { ["type"] = "text", ["text"] = content.GetValue<string>() });
                return true;
            case JsonValueKind.Array:
                foreach (var block in content.AsArray())
                {
                    target.Add(block?.DeepClone());
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Parses the body once into the object every later check and rewrite reads, and reads its <c>model</c>.</summary>
    /// <remarks>
    ///     A repeated property name is refused here: <see cref="JsonObject" /> throws a non-JSON exception on one, which would otherwise surface as an
    ///     unhandled 500 from the fold or the slot pin.
    /// </remarks>
    private static bool TryReadRequest(byte[] body, [NotNullWhen(true)] out JsonObject? request, [NotNullWhen(true)] out string? model)
    {
        request = null;
        model = null;
        try
        {
            if (JsonNode.Parse(body, documentOptions: new JsonDocumentOptions { AllowDuplicateProperties = false }) is JsonObject parsed
                && parsed["model"]?.GetValueKind() == JsonValueKind.String
                && parsed["model"]!.GetValue<string>() is { } value
                && !string.IsNullOrWhiteSpace(value))
            {
                request = parsed;
                model = value;
                return true;
            }
        }
        catch (JsonException)
        {
            // Empty, malformed, too deep or a repeated property name — fall through to the invalid-request path.
        }

        return false;
    }

    /// <summary>
    ///     The message of the child's OpenAI-shaped error body, worded for an Anthropic client.
    /// </summary>
    /// <remarks>
    ///     A context overflow gets the Messages API's own "prompt is too long" wording, which coding clients match on to compact a conversation.
    /// </remarks>
    private static string ReadUpstreamErrorMessage(byte[] body, int statusCode)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }

                if (error.ValueKind == JsonValueKind.Object)
                {
                    var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString()
                        : null;
                    if (error.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.ValueEquals("exceed_context_size_error"))
                    {
                        return error.TryGetProperty("n_prompt_tokens", out var prompt) && error.TryGetProperty("n_ctx", out var window)
                            ? $"prompt is too long: {prompt.GetRawText()} tokens > {window.GetRawText()} maximum"
                            : $"prompt is too long: {message}";
                    }

                    if (message is not null)
                    {
                        return message;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON after all: fall through to the status-only message.
        }

        return $"The local model runtime answered HTTP {statusCode}.";
    }

    // The Messages API's error type for a status (its documented table); 503 is how the proxy says "busy, retry".
    private static string AnthropicErrorType(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "invalid_request_error",
        StatusCodes.Status401Unauthorized => "authentication_error",
        StatusCodes.Status403Forbidden => "permission_error",
        StatusCodes.Status404NotFound => "not_found_error",
        StatusCodes.Status413PayloadTooLarge => "request_too_large",
        StatusCodes.Status429TooManyRequests => "rate_limit_error",
        StatusCodes.Status503ServiceUnavailable or 529 => "overloaded_error",
        StatusCodes.Status504GatewayTimeout => "timeout_error",
        >= StatusCodes.Status500InternalServerError => "api_error",
        _ => "invalid_request_error"
    };

    private static Task WriteBusyAsync(HttpContext context, WireFormat format, string message, CancellationToken ct)
    {
        context.Response.Headers.RetryAfter = "5";
        return WriteErrorAsync(context, format, StatusCodes.Status503ServiceUnavailable, message, "server_error", ct);
    }

    // The type argument is the OpenAI one; the Anthropic envelope derives its own from the status.
    private static async Task WriteErrorAsync(HttpContext context, WireFormat format, int statusCode, string message, string type, CancellationToken ct)
    {
        context.Response.StatusCode = statusCode;
        if (format == WireFormat.AnthropicMessages)
        {
            await context.Response.WriteAsJsonAsync(new
            {
                type = "error",
                error = new
                {
                    type = AnthropicErrorType(statusCode),
                    message
                }
            }, ct);
            return;
        }

        // OpenAI error envelope, so an OpenAI-compatible client surfaces the message instead of an empty failure.
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                message,
                type,
                code = (string?)null
            }
        }, ct);
    }
}
