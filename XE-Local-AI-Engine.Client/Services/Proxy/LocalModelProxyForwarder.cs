namespace XE_Local_AI_Engine.Client.Services.Proxy;

using System.Buffers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>
///     The inbound OpenAI-compatible model proxy. Provisions the requested local model through the llama-server
///     supervisor, then reverse-proxies the caller's request VERBATIM to that child's own OpenAI endpoint and streams
///     the response back byte-for-byte.
///     <para>
///         <b>Why byte-for-byte.</b> A llama-server child already speaks the OpenAI wire protocol
///         (<c>/v1/chat/completions</c>, <c>/v1/embeddings</c>). Forwarding the raw bytes is both the smallest possible
///         implementation and the strongest guarantee the caller asked for: because nothing in the node's chat
///         orchestration (persona, tools, memory, RAG, skills, sampling defaults, context clamps) sits on this path,
///         none of it can leak into the request. The external tool sees the raw model and only the raw model.
///     </para>
///     <para>
///         <b>llama.cpp only, and no SSRF.</b> The model must exist in the local GGUF catalog or the request 404s — this
///         both keeps the proxy strictly on the default local runtime and refuses any cloud model name, so an external
///         tool can never route through the operator's cloud credentials. The upstream URI is derived solely from the
///         supervisor-provided loopback endpoint plus a FIXED subpath; the caller's own path never influences the
///         target.
///     </para>
/// </summary>
internal sealed class LocalModelProxyForwarder
{
    /// <summary>Named <see cref="System.Net.Http.HttpClient" /> for forwarding. Infinite timeout: a long generation must not be cut off by a client timeout; caller disconnect and the inter-read idle watchdog below are the cancellation signals instead.</summary>
    public const string HttpClientName = "LocalModelProxyForwarder";

    private const string JsonContentType = "application/json";

    private const int StreamCopyBufferSize = 16 * 1024;

    /// <summary>
    ///     How many times a forwarded request re-ensures around a profiling spawn before answering 503. Profiling holds
    ///     the per-key single-flight gate through its own teardown, so one re-ensure normally suffices.
    /// </summary>
    private const int MaxProfilingReEnsures = 3;

    /// <summary>
    ///     Default maximum gap between two reads from the upstream child while streaming a response. A child that stops
    ///     producing bytes WITHOUT closing the socket would otherwise wedge the forward forever — the forwarding client
    ///     has an infinite timeout, and only caller disconnect would cancel it — leaving the inference lease held so a
    ///     graceful eject can never drain the model. Mirrors the invocation path's <c>StreamIdleTimeoutSeconds</c> (60s)
    ///     default so both streaming surfaces bound a silent runtime the same way.
    /// </summary>
    private static readonly TimeSpan DefaultUpstreamIdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Terminal frames for an event-stream response whose upstream child vanished mid-stream (the expected shape of
    ///     an operator force-eject). Mirrors the OpenAI error envelope <see cref="WriteErrorAsync" /> writes before the
    ///     headers are sent — same <c>message</c>/<c>type</c>/<c>code</c> fields — so a client sees ONE error shape from
    ///     this proxy whether the failure landed before or after the status. Deliberately NOT a
    ///     <c>finish_reason: "stop"</c> chunk: the generation did not complete, and saying it did would be a lie the
    ///     caller cannot detect.
    ///     <para>
    ///         Leads with a blank-line terminator because the child can die MID-LINE: appended straight onto a partial
    ///         <c>data: {"cho</c> the error frame would be swallowed into that broken event and never reach the client.
    ///         On an event boundary the extra blank lines cost nothing — an event carrying no data is not dispatched.
    ///     </para>
    /// </summary>
    private static readonly byte[] UpstreamGoneSseFrames = Encoding.UTF8.GetBytes(
        "\n\ndata: {\"error\":{\"message\":\"The local model runtime stopped while streaming this response (it may have been ejected). Try again shortly.\",\"type\":\"server_error\",\"code\":null}}\n\ndata: [DONE]\n\n");

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

    /// <summary>Handles <c>POST proxy/v1/chat/completions</c>.</summary>
    public Task ForwardChatCompletionsAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Chat, "chat/completions");

    /// <summary>Handles <c>POST proxy/v1/embeddings</c>.</summary>
    public Task ForwardEmbeddingsAsync(HttpContext context) =>
        ForwardAsync(context, ModelRole.Embedding, "embeddings");

    private async Task ForwardAsync(HttpContext context, ModelRole role, string upstreamPath)
    {
        var ct = context.RequestAborted;

        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, ct);
        var body = buffer.ToArray();

        if (!TryReadModel(body, out var model))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Request body must be JSON with a non-empty \"model\" field.", "invalid_request_error", ct);
            return;
        }

        // Existence check against the local GGUF catalog is what makes this llama.cpp-only and refuses cloud model
        // names — an unknown or cloud model is a 404 here rather than a route to any other provider.
        if (!await IsInstalledLlamaModelAsync(model, ct))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"The model '{model}' does not exist as a local model on this node.", "invalid_request_error", ct);
            return;
        }

        // Provision, then take an inference lease for the request's lifetime so an operator eject drains this request
        // instead of tree-killing it mid-stream. The two steps are not atomic, so the lease result also says whether
        // the endpoint just handed out is still ours to use:
        //   NotRunning      => proceed leaseless (EnsureRunning just returned an endpoint; the upstream call
        //                      self-evidences liveness).
        //   Evicting        => the operator is draining this process; refuse.
        //   ProfilingOwned  => a measurement spawn took the key in between, and the port allocator commonly hands it
        //                      the port the replaced process just freed — so this endpoint may BE the measurement.
        //                      Re-ensure (which parks on the per-key gate profiling holds through teardown) rather
        //                      than forward into it, bounded so back-to-back measurements answer retryably.
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
                await WriteBusyAsync(context, "The local runtime could not load the requested model right now (it may be at capacity). Try again shortly.", ct);
                return;
            }

            var acquisition = _runtime.TryAcquireInferenceLease(model, role);
            if (acquisition.ProcessEvicting)
            {
                await WriteBusyAsync(context, "The requested model is being ejected by the operator. Try again shortly.", ct);
                return;
            }

            if (!acquisition.ProcessProfiling)
            {
                acquired = acquisition.Lease;
                break;
            }

            if (profilingReEnsures++ >= MaxProfilingReEnsures)
            {
                await WriteBusyAsync(context, "The requested model is being profiled by a benchmark right now. Try again shortly.", ct);
                return;
            }
        }

        using var lease = acquired;

        var upstreamUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri.TrimEnd('/')}/{upstreamPath}");
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
        {
            Content = new ByteArrayContent(body)
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
            // The child exited after EnsureRunningAsync, or refused/reset the connection before any response bytes. This
            // is the same "runtime unavailable" condition as an at-capacity spawn, so answer with the SAME retryable
            // OpenAI-shaped 503 rather than letting it fall through to the global handler's generic 500. Nothing has been
            // written to the response yet, so the status is still ours to set. (A caller disconnect surfaces as
            // OperationCanceledException, not these, so it is not mistaken for a runtime failure.)
            _logger.LogWarning(ex, "Model proxy could not reach the llama-server child for model {Model} ({Role}).", model, role);
            await WriteBusyAsync(context, "The local model runtime is temporarily unavailable. Try again shortly.", ct);
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            if (upstreamResponse.Content.Headers.ContentType is { } contentType)
            {
                context.Response.ContentType = contentType.ToString();
            }

            // Stream as it arrives: without disabling the write buffer an SSE (stream:true) response would be batched and
            // the caller would not see tokens incrementally.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
            await PumpWithIdleDeadlineAsync(upstreamStream, context, model, upstreamPath, ct);
        }
    }

    /// <summary>
    ///     Copies the upstream stream to the caller with a per-read idle deadline. A read that returns no bytes within
    ///     <see cref="_upstreamIdleTimeout" /> aborts the exchange so the enclosing <c>using</c> lease is released and a
    ///     graceful eject can drain the model — the naive <see cref="Stream.CopyToAsync(Stream, CancellationToken)" />
    ///     would instead wait forever on a silent-but-open child. Writes flow under the caller token (a slow CLIENT must
    ///     not trip the upstream-idle timer); only the upstream read carries the idle deadline.
    ///     <para>
    ///         A child that DIES mid-stream (a forced eject is an expected operator action, not an error) ends the read
    ///         with the same "server is gone" exception family the provider already recognizes. That is not an
    ///         unhandled failure: it ends the exchange deliberately — see <see cref="EndAfterUpstreamGoneAsync" />.
    ///     </para>
    /// </summary>
    private async Task PumpWithIdleDeadlineAsync(Stream upstream,
        HttpContext context,
        string model,
        string upstreamPath,
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
                        // Upstream went silent without closing the socket. The response has already started, so its
                        // status can no longer be changed to 503 — abort the connection so the caller sees a broken
                        // stream rather than a hang, and (crucially) so returning here releases the inference lease.
                        context.Abort();
                        return;
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested && DeferredLlamaServerChatClient.IsServerGone(ex))
                    {
                        // The child died mid-stream — typically a forced eject, which is an operator action and not an
                        // error. Left to propagate this reaches the pipeline AFTER the response started, which logs an
                        // unhandled ERROR and cuts the caller's stream mid-token with no terminal frame at all.
                        // The token check pairs with the match exactly as both sites in DeferredLlamaServerChatClient
                        // do: a caller that disconnected aborts the same read, and reporting that as the child dying
                        // would write an error frame to nobody and log a runtime failure that never happened.
                        await EndAfterUpstreamGoneAsync(context, model, upstreamPath, ex, ct);
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
    ///     Ends an exchange whose upstream child vanished after the response started. The status is already on the wire
    ///     and can no longer become a 503, so the contract is per body shape: an event stream gets one error frame plus
    ///     <c>[DONE]</c> (a client that speaks SSE then sees a reason instead of a truncated stream), and anything else
    ///     — a half-written JSON document that cannot be repaired — is aborted, exactly like the idle-deadline arm.
    /// </summary>
    private async Task EndAfterUpstreamGoneAsync(HttpContext context,
        string model,
        string upstreamPath,
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
            await context.Response.Body.WriteAsync(UpstreamGoneSseFrames, ct);
            await context.Response.Body.FlushAsync(ct);
        }
        catch (Exception writeFailure) when (writeFailure is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The caller went away too. There is nobody left to tell, and throwing here would re-create the very
            // unhandled post-header exception this method exists to prevent.
            _logger.LogDebug(writeFailure, "Model proxy could not deliver the terminal frame for model {Model}; the caller is gone as well.", model);
        }
    }

    private async Task<bool> IsInstalledLlamaModelAsync(string model, CancellationToken ct)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct);
        return installed.Any(descriptor =>
            descriptor.IsAvailable && string.Equals(descriptor.ModelName, model, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryReadModel(byte[] body, out string model)
    {
        model = string.Empty;
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out var modelElement)
                && modelElement.ValueKind == JsonValueKind.String)
            {
                var value = modelElement.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    model = value;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed JSON — fall through to the invalid-request path.
        }

        return false;
    }

    private static Task WriteBusyAsync(HttpContext context, string message, CancellationToken ct)
    {
        context.Response.Headers.RetryAfter = "5";
        return WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, message, "server_error", ct);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type, CancellationToken ct)
    {
        context.Response.StatusCode = statusCode;
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
