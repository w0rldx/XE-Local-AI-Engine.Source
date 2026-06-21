namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

using System.Text.Json;
using System.Text.Json.Serialization;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Ollama;

/// <summary>
///     Streams live pull-download progress as NDJSON (<c>application/x-ndjson</c>).  Each line is a sanitized JSON object
///     carrying only <c>{ status, completedBytes, totalBytes }</c> — no paths, tokens, or raw Ollama payloads.
/// </summary>
/// <remarks>
///     Hand-wired on the React client; intentionally excluded from the generated OpenAPI typed client (mirrors the chat
///     SSE pattern).  Uses the same <see cref="ModelNameValidator" /> and <c>Operator</c> policy as the blocking pull
///     endpoint.  The streaming response is scoped to the requesting caller — no broadcast.
/// </remarks>
public sealed class PullStreamLocalModelEndpoint(
    IOllamaModelService modelService,
    IModelProviderMapStore modelProviderMapStore,
    ModelNameValidator modelNameValidator,
    ILogger<PullStreamLocalModelEndpoint> logger) : Endpoint<PullLocalModelRequest>
{
    /// <summary>
    ///     Minimum gap between progress lines while the status is unchanged. Ollama emits many "downloading" updates
    ///     per second; coalescing same-status updates to one per second keeps the wire (and the React client) from
    ///     being flooded. A status change always flushes immediately, and the final update is always flushed.
    /// </summary>
    private const long ProgressThrottleMs = 1000;

    // Omit null properties so the optional `error` field is written only on the terminal failure line — the
    // success/progress lines stay the exact `{status, completedBytes, totalBytes}` shape (sanitization invariant).
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ILogger<PullStreamLocalModelEndpoint> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ModelNameValidator _modelNameValidator = modelNameValidator ?? throw new ArgumentNullException(nameof(modelNameValidator));

    private readonly IModelProviderMapStore _modelProviderMapStore = modelProviderMapStore ?? throw new ArgumentNullException(nameof(modelProviderMapStore));
    private readonly IOllamaModelService _modelService = modelService ?? throw new ArgumentNullException(nameof(modelService));

    public override void Configure()
    {
        Post(LocalApiRoutes.LocalModels.PullStream);
        Policies(NodeAuthorizationPolicies.Operator);
        // Hand-wired on the React client — intentionally excluded from the generated OpenAPI/NSwag document so a
        // regen cycle does not emit a typed client entry for this streaming transport.
        Options(x => x.ExcludeFromDescription());
    }

    public override async Task HandleAsync(PullLocalModelRequest req, CancellationToken ct)
    {
        var validationError = _modelNameValidator.GetValidationError(req.ModelName);
        if (validationError is not null)
        {
            AddError(validationError);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        var modelName = req.ModelName!.Trim();

        HttpContext.Response.ContentType = "application/x-ndjson";
        HttpContext.Response.StatusCode = 200;

        string? lastStatus = null;
        var lastWriteMs = 0L;
        PullStreamProgressEvent? pending = null;

        try
        {
            await foreach (var progress in _modelService.PullModelAsync(modelName, ct).ConfigureAwait(false))
            {
                // Sanitize: emit only the three safe fields — never raw Ollama payloads, paths, or tokens.
                var sanitized = new PullStreamProgressEvent
                {
                    Status = string.IsNullOrWhiteSpace(progress.Status) ? string.Empty : progress.Status,
                    CompletedBytes = progress.Completed,
                    TotalBytes = progress.Total
                };

                // Coalesce same-status updates to at most one per ProgressThrottleMs; a status (phase) change always
                // flushes immediately. Anything skipped is held as `pending` so the most recent update is never lost.
                var statusChanged = !string.Equals(sanitized.Status, lastStatus, StringComparison.Ordinal);
                var now = Environment.TickCount64;
                if (statusChanged || now - lastWriteMs >= ProgressThrottleMs)
                {
                    await WriteEventAsync(sanitized, ct).ConfigureAwait(false);
                    lastStatus = sanitized.Status;
                    lastWriteMs = now;
                    pending = null;
                }
                else
                {
                    pending = sanitized;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // OllamaSharp can throw mid-enumeration (e.g. a non-existent model -> "pull model manifest: file does not
            // exist"). By this point the 200 + content-type are already committed, so rethrowing would tear the stream
            // (Kestrel: "response has already started") and leave the client's reader hanging with no terminal line.
            // Instead emit ONE sanitized terminal error line and return — the client surfaces it as a failure toast.
            var terminal = new PullStreamProgressEvent
            {
                Status = "error",
                Error = SanitizePullError(ex)
            };
            await WriteEventAsync(terminal, ct).ConfigureAwait(false);
            return;
        }

        // Always flush the final update (e.g. the terminal "success" line / final 100% byte count) even if it fell
        // inside the throttle window, so the client's progress bar and completion handling see the true last state.
        if (pending is not null)
        {
            await WriteEventAsync(pending, ct).ConfigureAwait(false);
        }

        // Reaching here means the pull enumeration completed without throwing (the catch returns early on failure):
        // explicitly route this Ollama model to the Ollama runtime. The unmapped-routing default is now "llamacpp", so a
        // node-pulled Ollama model must persist a "ollama" map row or a later send would dial llama.cpp by default.
        // Symmetric to the GGUF download coordinator's llamacpp map-write (and its best-effort swallow-and-log).
        await RouteToOllamaAsync(modelName).ConfigureAwait(false);
    }

    // Persists the "ollama" routing row for a just-completed pull. The 200 + NDJSON stream are already committed by this
    // point, so a throw here would tear the connection ("response has already started") AND leave the model unmapped.
    // Mirror the GGUF coordinator's best-effort pattern: swallow-and-log so a successful download is never reported as a
    // failure because the routing row could not be persisted. Use CancellationToken.None so a client disconnect at the
    // very end of the stream (which cancels `ct`) does not drop the mapping for an otherwise-successful pull.
    private async Task RouteToOllamaAsync(string modelName)
    {
        try
        {
            _ = await _modelProviderMapStore.UpsertAsync(modelName, OllamaLocalModelProvider.OllamaProviderName, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not persist the ollama provider mapping for {ModelName}; the default-provider routing still applies.", modelName);
        }
    }

    /// <summary>
    ///     Maps an exception thrown during the pull enumeration to a short, stable, sanitized reason. Never forwards the
    ///     raw exception message (it can carry filesystem paths, tokens, or other Ollama internals): a manifest /
    ///     not-found / file-does-not-exist failure becomes <c>"Model not found"</c>, everything else <c>"Pull failed"</c>.
    /// </summary>
    private static string SanitizePullError(Exception ex)
    {
        var message = ex.Message ?? string.Empty;
        var notFound = message.Contains("file does not exist", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("not found", StringComparison.OrdinalIgnoreCase)
                       || message.Contains("manifest", StringComparison.OrdinalIgnoreCase);
        return notFound ? "Model not found" : "Pull failed";
    }

    private async Task WriteEventAsync(PullStreamProgressEvent sanitized, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(sanitized, SerializerOptions);
        await HttpContext.Response.WriteAsync(line, ct).ConfigureAwait(false);
        await HttpContext.Response.WriteAsync("\n", ct).ConfigureAwait(false);
        await HttpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
