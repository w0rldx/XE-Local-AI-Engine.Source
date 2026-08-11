namespace XE_Local_AI_Engine.Client.Services.Proxy;

using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

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
    /// <summary>Named <see cref="System.Net.Http.HttpClient" /> for forwarding. Infinite timeout: a long generation must not be cut off by a client timeout; the caller's disconnect (request-abort) is the cancellation signal instead.</summary>
    public const string HttpClientName = "LocalModelProxyForwarder";

    private const string JsonContentType = "application/json";

    private readonly IGgufModelStore _ggufModelStore;
    private readonly ILlamaServerProcessSupervisor _supervisor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<LocalModelProxyForwarder> _logger;

    public LocalModelProxyForwarder(IGgufModelStore ggufModelStore,
        ILlamaServerProcessSupervisor supervisor,
        IHttpClientFactory httpClientFactory,
        TimeProvider timeProvider,
        ILogger<LocalModelProxyForwarder> logger)
    {
        _ggufModelStore = ggufModelStore ?? throw new ArgumentNullException(nameof(ggufModelStore));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Handles <c>GET proxy/v1/models</c> — synthesizes an OpenAI model list from the installed llama.cpp GGUF catalog.</summary>
    public async Task WriteModelsAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var ct = context.RequestAborted;

        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
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

        await context.Response.WriteAsJsonAsync(new { @object = "list", data }, ct).ConfigureAwait(false);
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
        await context.Request.Body.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var body = buffer.ToArray();

        if (!TryReadModel(body, out var model))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest,
                "Request body must be JSON with a non-empty \"model\" field.", "invalid_request_error", ct).ConfigureAwait(false);
            return;
        }

        // Existence check against the local GGUF catalog is what makes this llama.cpp-only and refuses cloud model
        // names — an unknown or cloud model is a 404 here rather than a route to any other provider.
        if (!await IsInstalledLlamaModelAsync(model, ct).ConfigureAwait(false))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"The model '{model}' does not exist as a local model on this node.", "invalid_request_error", ct).ConfigureAwait(false);
            return;
        }

        LlamaServerEndpoint endpoint;
        try
        {
            endpoint = await _supervisor.EnsureRunningAsync(model, role, ct).ConfigureAwait(false);
        }
        catch (LlamaRuntimeException ex)
        {
            // Spawn failed, the loaded-model cap was reached, or restart-backoff was exceeded. The message is already
            // sanitized by the supervisor. A busy/at-capacity node is a retryable condition, not a permanent failure.
            _logger.LogWarning(ex, "Model proxy could not provision model {Model} for {Role}.", model, role);
            await WriteBusyAsync(context, "The local runtime could not load the requested model right now (it may be at capacity). Try again shortly.", ct).ConfigureAwait(false);
            return;
        }

        // Hold an inference lease for the request's lifetime so an operator eject drains this request instead of
        // tree-killing it mid-stream. NotRunning => proceed leaseless (EnsureRunning just returned an endpoint; the
        // upstream call self-evidences liveness). Evicting => the operator is draining this process; refuse.
        var acquisition = _supervisor.TryAcquireInferenceLease(model, role);
        if (acquisition.ProcessEvicting)
        {
            await WriteBusyAsync(context, "The requested model is being ejected by the operator. Try again shortly.", ct).ConfigureAwait(false);
            return;
        }

        using var lease = acquisition.Lease;

        var upstreamUri = new Uri($"{endpoint.BaseAddress.AbsoluteUri.TrimEnd('/')}/{upstreamPath}");
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUri)
        {
            Content = new ByteArrayContent(body)
        };
        upstreamRequest.Content.Headers.TryAddWithoutValidation("Content-Type", JsonContentType);

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);

        using var upstreamResponse = await httpClient
                                           .SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct)
                                           .ConfigureAwait(false);

        context.Response.StatusCode = (int)upstreamResponse.StatusCode;
        if (upstreamResponse.Content.Headers.ContentType is { } contentType)
        {
            context.Response.ContentType = contentType.ToString();
        }

        // Stream as it arrives: without disabling the write buffer an SSE (stream:true) response would be batched and
        // the caller would not see tokens incrementally.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await upstreamStream.CopyToAsync(context.Response.Body, ct).ConfigureAwait(false);
    }

    private async Task<bool> IsInstalledLlamaModelAsync(string model, CancellationToken ct)
    {
        var installed = await _ggufModelStore.ListInstalledModelsAsync(ct).ConfigureAwait(false);
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
        await context.Response.WriteAsJsonAsync(new { error = new { message, type, code = (string?)null } }, ct).ConfigureAwait(false);
    }
}
