namespace XE_Local_AI_Engine.Client.Services.ExternalProviders.Implementation;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using XE_Local_AI_Engine.Providers.Abstractions.External;
using XE_Local_AI_Engine.Providers.OpenAICompatible.Core;

/// <summary>
///     Performs the connect-time <c>GET {normalized-base}/models</c> probe against a stored connection or an unsaved
///     draft.
/// </summary>
/// <remarks>
///     <para>
///         Three properties are load-bearing and each has a test. The address is normalized by
///         <see cref="OpenAICompatibleBaseAddress" /> — the SAME normalizer the save path and the outbound chat guard
///         use, so a probe can never validate an address the transport would then spell differently. Redirects are
///         refused rather than followed, because a <c>302</c> would move the probe to a host the operator never
///         reviewed and report IT as reachable. And the API key never leaves this class: it goes into one
///         <c>Authorization</c> header and appears in no result, message, or log.
///     </para>
///     <para>
///         The verdict is deliberately generous. "Reachable" here means the endpoint ANSWERED — a 404 from a gateway
///         with no model listing, a 401 from one that wants a different key, and a clean 200 are all answers, and only
///         the first two carry an explanatory error. A connection whose server implements nothing but
///         <c>POST /v1/chat/completions</c> is fully usable, so the probe must never be the thing that stops the
///         operator saving it.
///     </para>
/// </remarks>
internal sealed class ExternalProviderProbeService : IExternalProviderProbeService
{
    /// <summary>
    ///     Caps the whole probe. Short by design: this backs a "Test connection" button an operator is watching, and an
    ///     endpoint that has not answered a model listing in ten seconds is not one a chat turn would survive either.
    ///     Deliberately independent of the connection's own generation timeout, which bounds a long completion.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    ///     Upper bound on the listing body read into memory. A model listing is kilobytes; anything past this is a
    ///     misconfigured endpoint (or a hostile one), and reading it would be an unbounded allocation driven from
    ///     outside the node.
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly ILogger<ExternalProviderProbeService> _logger;
    private readonly IExternalProviderStore _store;
    private readonly Func<HttpMessageHandler>? _transportHandlerFactory;

    /// <param name="store">The encrypted connection store, for resolving a stored connection's address and key.</param>
    /// <param name="logger">Diagnostics. Never receives the key or the response body.</param>
    /// <param name="transportHandlerFactory">
    ///     Test seam supplying the HTTP handler so the probe can be driven without live network I/O.
    ///     <see langword="null" /> in production.
    /// </param>
    public ExternalProviderProbeService(IExternalProviderStore store,
        ILogger<ExternalProviderProbeService> logger,
        Func<HttpMessageHandler>? transportHandlerFactory = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _transportHandlerFactory = transportHandlerFactory;
    }

    public async Task<ExternalProviderProbeResult> ProbeAsync(ExternalProviderProbeQuery query, CancellationToken cancellationToken = default)
    {
        var stored = await ResolveStoredConnectionAsync(query.ConnectionId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(query.ConnectionId) && stored is null)
        {
            return new ExternalProviderProbeResult
            {
                Outcome = ExternalProviderProbeOutcome.UnknownConnection,
                Error = "No external connection is stored under that id."
            };
        }

        // A typed draft address wins over the stored one: the operator is testing what they just entered. Absent that,
        // the stored value is re-normalized rather than trusted blindly — it is a fixed point of the normalizer, so
        // this is free for a well-formed store and catches a hand-edited file.
        var candidateBaseUrl = !string.IsNullOrWhiteSpace(query.BaseUrl) ? query.BaseUrl : stored?.BaseUrl;
        if (!OpenAICompatibleBaseAddress.TryNormalize(candidateBaseUrl, out var baseAddress))
        {
            return new ExternalProviderProbeResult
            {
                Outcome = ExternalProviderProbeOutcome.InvalidBaseUrl,
                Error = "The endpoint must be an absolute http(s) address without credentials, query, or fragment."
            };
        }

        // Blank means "use what is stored": the masked editor sends no key back, so requiring one here would make
        // testing an existing connection impossible without re-typing the secret.
        var apiKey = !string.IsNullOrWhiteSpace(query.ApiKey) ? query.ApiKey : stored?.ApiKey;

        return await SendProbeAsync(baseAddress, apiKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StoredExternalProviderConnection?> ResolveStoredConnectionAsync(string? connectionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return null;
        }

        var canonicalId = ExternalModelId.CanonicalizeConnectionId(connectionId);
        var config = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return config.Connections.FirstOrDefault(connection => string.Equals(connection.Id, canonicalId, StringComparison.Ordinal));
    }

    private async Task<ExternalProviderProbeResult> SendProbeAsync(Uri baseAddress, string? apiKey, CancellationToken cancellationToken)
    {
        // The probe's own deadline must not be indistinguishable from the caller cancelling: a linked source lets the
        // catch below tell "the operator navigated away" (rethrow) from "the endpoint did not answer" (a verdict).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            // Ownership transfers into the HttpClient (disposeHandler: true), which this scope disposes. CA2000 cannot
            // follow that transfer.
#pragma warning disable CA2000
            var handler = _transportHandlerFactory?.Invoke()
                          ?? new SocketsHttpHandler
                          {
                              ConnectTimeout = ProbeTimeout,

                              // Never follow a redirect: it would move the probe to a host the operator never reviewed
                              // and then report that host as the connection's reachability. A 3xx is reported below.
                              AllowAutoRedirect = false
                          };
            using var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
#pragma warning restore CA2000

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseAddress, "models"));
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return await ReadProbeResponseAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Covers the probe deadline (a cancellation that is NOT the caller's) and every transport failure. The
            // exception text is deliberately dropped from the RESULT — it embeds the address and can embed header
            // material — but kept in the node's own log, where the operator can see it.
            _logger.LogDebug(exception, "External provider probe could not reach the configured endpoint.");
            return new ExternalProviderProbeResult
            {
                Outcome = ExternalProviderProbeOutcome.Unreachable,
                Error = "The endpoint could not be reached. Check the address, the port, and that the server is running."
            };
        }
    }

    private static async Task<ExternalProviderProbeResult> ReadProbeResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
        {
            return Answered($"The endpoint answered with a redirect (HTTP {status}), which is not followed. Enter the address it redirects to.");
        }

        if (!response.IsSuccessStatusCode)
        {
            return Answered(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? $"The endpoint rejected the model listing with HTTP {status}. Check the API key."
                : $"The endpoint answered with HTTP {status} and served no model listing. Add model ids by hand.");
        }

        var payload = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return Answered("The endpoint's model listing was too large to read. Add model ids by hand.");
        }

        return TryParseModels(payload, out var models)
            ? new ExternalProviderProbeResult
            {
                Outcome = ExternalProviderProbeOutcome.Answered,
                Models = models
            }
            : Answered("The endpoint answered, but its model listing could not be understood. Add model ids by hand.");
    }

    /// <summary>Reads at most <see cref="MaxResponseBytes" />, or <see langword="null" /> when the body exceeds it.</summary>
    private static async Task<byte[]?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                return null;
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    /// <summary>
    ///     Extracts <c>data[].id</c> and, when present, a declared window. Tolerant by design: the payload comes from a
    ///     server the node does not control, so a shape that is not the documented one is "no listing", never a fault.
    /// </summary>
    private static bool TryParseModels(byte[] payload, out IReadOnlyList<ExternalProviderProbeModel> models)
    {
        models = [];
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsed = new List<ExternalProviderProbeModel>(data.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var modelId = id.GetString();

                // Only ids this node could actually register are offered: an id that fails the wire-id grammar could
                // never be saved, so listing it would produce a pick-to-add row that always fails validation.
                if (!ExternalModelId.IsValidWireId(modelId) || !seen.Add(modelId))
                {
                    continue;
                }

                parsed.Add(new ExternalProviderProbeModel(modelId, ReadContextLength(entry)));
            }

            models = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The declared window, from vLLM's <c>max_model_len</c> or the <c>context_length</c> some gateways use. Both
    ///     are optional and neither is standard, so a missing, non-numeric or non-positive value is simply "not
    ///     declared" — the registration form then asks the operator.
    /// </summary>
    private static int? ReadContextLength(JsonElement entry)
    {
        foreach (var propertyName in (ReadOnlySpan<string>)["max_model_len", "context_length"])
        {
            if (entry.TryGetProperty(propertyName, out var value)
                && value.ValueKind == JsonValueKind.Number
                && value.TryGetInt32(out var contextLength)
                && contextLength > 0)
            {
                return contextLength;
            }
        }

        return null;
    }

    private static ExternalProviderProbeResult Answered(string error)
    {
        return new ExternalProviderProbeResult
        {
            Outcome = ExternalProviderProbeOutcome.Answered,
            Error = error
        };
    }
}
