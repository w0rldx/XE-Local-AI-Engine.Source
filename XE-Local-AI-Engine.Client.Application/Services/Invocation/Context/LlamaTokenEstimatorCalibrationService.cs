namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Opportunistically calibrates token estimates for llama.cpp models that are serving real requests. Each request
///     performs only a bounded due check and queue write; /tokenize I/O runs here, outside inference. There is no timer
///     that can revisit a stale endpoint after eject. Provider failures retain the prior calibration or chars/4 fallback.
/// </summary>
internal sealed class LlamaTokenEstimatorCalibrationService : BackgroundService, ITokenEstimatorCalibrationScheduler
{
    internal const int DefaultWorkCapacity = 64;

    internal const string CalibrationText =
        "Token estimation calibration sample. The quick brown fox jumps over the lazy dog. " +
        "Structured data: {\"alpha\":123,\"enabled\":true,\"items\":[\"one\",\"two\",\"three\"]}. " +
        "Code: public static int Sum(int left, int right) => left + right; " +
        "Paths: /var/lib/models/example.gguf C:\\models\\example.gguf. " +
        "Repeatable prose keeps this sample independent from prompts, tools, users, and request content.";

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _sync = new();
    private readonly ITokenEstimatorCalibrationStore _store;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaTokenEstimatorCalibrationService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, CalibrationTarget> _targets = new(StringComparer.Ordinal);
    private readonly Channel<CalibrationWork> _work;
    private long _generation;

    public LlamaTokenEstimatorCalibrationService(HttpClient httpClient,
        ITokenEstimatorCalibrationStore store,
        ILogger<LlamaTokenEstimatorCalibrationService> logger)
        : this(httpClient, store, logger, DefaultInterval, TimeProvider.System, DefaultWorkCapacity)
    {
    }

    internal LlamaTokenEstimatorCalibrationService(HttpClient httpClient,
        ITokenEstimatorCalibrationStore store,
        ILogger<LlamaTokenEstimatorCalibrationService> logger,
        TimeSpan interval,
        TimeProvider timeProvider,
        int workCapacity)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(workCapacity);
        _work = Channel.CreateBounded<CalibrationWork>(new BoundedChannelOptions(workCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public void Schedule(string modelName, Uri llamaServerBaseAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(llamaServerBaseAddress);

        if (!IsLoopbackHttp(llamaServerBaseAddress))
        {
            LogFailure(CalibrationFailureReason.RejectedEndpoint);
            return;
        }

        CalibrationWork? work = null;
        lock (_sync)
        {
            var now = _timeProvider.GetUtcNow();
            if (!_targets.TryGetValue(modelName, out var target)
                || target.BaseAddress != llamaServerBaseAddress)
            {
                target = new CalibrationTarget(llamaServerBaseAddress, ++_generation, DateTimeOffset.MinValue, false);
                _targets[modelName] = target;
            }

            if (!target.InFlight && target.NextDueUtc <= now)
            {
                target = target with
                {
                    InFlight = true
                };
                _targets[modelName] = target;
                work = new CalibrationWork(modelName, target.BaseAddress, target.Generation);
            }
        }

        if (work is { } due && !_work.Writer.TryWrite(due))
        {
            ReleaseRejectedWork(due);
        }
    }

    public void Invalidate(string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        lock (_sync)
        {
            _targets.Remove(modelName);
            _generation++;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            await TryCalibrateCurrentTargetAsync(work, stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task<bool> TryCalibrateAsync(string modelName, Uri llamaServerBaseAddress, CancellationToken cancellationToken)
    {
        var result = await TryReadDivisorAsync(llamaServerBaseAddress, cancellationToken).ConfigureAwait(false);
        if (result.Divisor is not { } divisor)
        {
            return false;
        }

        _store.SetDivisor(modelName, divisor);
        return true;
    }

    internal static HttpClientHandler CreateProductionHandler()
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
    }

    internal static int CalculateDivisor(int characterCount, int tokenCount)
    {
        if (characterCount <= 0 || tokenCount <= 0)
        {
            return TokenEstimatorCalibrationStore.DefaultCharsPerToken;
        }

        // Floor is deliberate: characterCount / divisor must be >= the observed token count for the calibration sample.
        return Math.Clamp(characterCount / tokenCount,
            TokenEstimatorCalibrationStore.MinimumCharsPerToken,
            TokenEstimatorCalibrationStore.MaximumCharsPerToken);
    }

    private async Task TryCalibrateCurrentTargetAsync(CalibrationWork work, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (!IsCurrentTarget(work))
            {
                return;
            }
        }

        var result = await TryReadDivisorAsync(work.BaseAddress, cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            if (!IsCurrentTarget(work))
            {
                return;
            }

            var target = _targets[work.ModelName];
            _targets[work.ModelName] = target with
            {
                InFlight = false,
                NextDueUtc = _timeProvider.GetUtcNow() + _interval
            };

            if (result.Divisor is { } divisor)
            {
                _store.SetDivisor(work.ModelName, divisor);
            }
        }
    }

    private bool IsCurrentTarget(CalibrationWork work)
    {
        return _targets.TryGetValue(work.ModelName, out var target)
               && target.Generation == work.Generation
               && target.BaseAddress == work.BaseAddress;
    }

    private void ReleaseRejectedWork(CalibrationWork work)
    {
        lock (_sync)
        {
            if (!IsCurrentTarget(work))
            {
                return;
            }

            var target = _targets[work.ModelName];
            _targets[work.ModelName] = target with
            {
                InFlight = false
            };
        }
    }

    private async Task<CalibrationResult> TryReadDivisorAsync(Uri llamaServerBaseAddress, CancellationToken cancellationToken)
    {
        if (!IsLoopbackHttp(llamaServerBaseAddress))
        {
            LogFailure(CalibrationFailureReason.RejectedEndpoint);
            return default;
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            var tokenizeUri = new Uri($"{llamaServerBaseAddress.Scheme}://{llamaServerBaseAddress.Authority}/tokenize");
            using var response = await _httpClient.PostAsJsonAsync(tokenizeUri,
                new
                {
                    content = CalibrationText,
                    add_special = false,
                    parse_special = false,
                    with_pieces = false
                },
                timeout.Token).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                LogFailure(CalibrationFailureReason.Redirect);
                return default;
            }

            if (response.RequestMessage?.RequestUri is { } finalAddress && !IsLoopbackHttp(finalAddress))
            {
                LogFailure(CalibrationFailureReason.FinalEndpointRejected);
                return default;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogFailure(CalibrationFailureReason.HttpStatus);
                return default;
            }

            await using var contentStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Array
                || tokens.GetArrayLength() <= 0)
            {
                LogFailure(CalibrationFailureReason.InvalidPayload);
                return default;
            }

            return new CalibrationResult(CalculateDivisor(CalibrationText.Length, tokens.GetArrayLength()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogFailure(CalibrationFailureReason.Timeout);
            return default;
        }
        catch (HttpRequestException)
        {
            LogFailure(CalibrationFailureReason.RequestFailure);
            return default;
        }
        catch (JsonException)
        {
            LogFailure(CalibrationFailureReason.InvalidPayload);
            return default;
        }
    }

    private void LogFailure(CalibrationFailureReason reason)
    {
        // Bounded, content-free evidence only: no model, URI, port, prompt, tool, user, request, or response data.
        _logger.LogDebug("llama.cpp token-estimator calibration unavailable ({FailureReason}); retaining the prior bounded divisor.",
            reason.ToString());
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MultipleChoices
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
    }

    private static bool IsLoopbackHttp(Uri address)
    {
        if (!address.IsAbsoluteUri || address.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        return string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)
               || (IPAddress.TryParse(address.Host, out var parsed) && IPAddress.IsLoopback(parsed));
    }

    private sealed record CalibrationTarget(Uri BaseAddress, long Generation, DateTimeOffset NextDueUtc, bool InFlight);

    private readonly record struct CalibrationWork(string ModelName, Uri BaseAddress, long Generation);

    private readonly record struct CalibrationResult(int? Divisor);

    private enum CalibrationFailureReason
    {
        RejectedEndpoint,
        Redirect,
        FinalEndpointRejected,
        HttpStatus,
        InvalidPayload,
        Timeout,
        RequestFailure
    }
}
