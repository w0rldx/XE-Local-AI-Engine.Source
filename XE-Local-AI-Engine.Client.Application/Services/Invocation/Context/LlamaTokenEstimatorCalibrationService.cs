namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;

/// <summary>
///     Opportunistically calibrates token estimates for llama.cpp models that have already served a real request.
///     Scheduling is non-blocking; all <c>/tokenize</c> I/O runs here, outside the streaming path. Provider failures leave
///     the prior calibration (or chars/4 fallback) intact and never affect inference.
/// </summary>
internal sealed class LlamaTokenEstimatorCalibrationService : BackgroundService, ITokenEstimatorCalibrationScheduler
{
    internal const string CalibrationText =
        "Token estimation calibration sample. The quick brown fox jumps over the lazy dog. " +
        "Structured data: {\"alpha\":123,\"enabled\":true,\"items\":[\"one\",\"two\",\"three\"]}. " +
        "Code: public static int Sum(int left, int right) => left + right; " +
        "Paths: /var/lib/models/example.gguf C:\\models\\example.gguf. " +
        "Repeatable prose keeps this sample independent from prompts, tools, users, and request content.";

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly ITokenEstimatorCalibrationStore _store;
    private readonly HttpClient _httpClient;
    private readonly ILogger<LlamaTokenEstimatorCalibrationService> _logger;
    private readonly TimeSpan _interval;
    private readonly ConcurrentDictionary<string, CalibrationTarget> _targets = new(StringComparer.Ordinal);
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false
    });

    public LlamaTokenEstimatorCalibrationService(HttpClient httpClient,
        ITokenEstimatorCalibrationStore store,
        ILogger<LlamaTokenEstimatorCalibrationService> logger)
        : this(httpClient, store, logger, DefaultInterval)
    {
    }

    internal LlamaTokenEstimatorCalibrationService(HttpClient httpClient,
        ITokenEstimatorCalibrationStore store,
        ILogger<LlamaTokenEstimatorCalibrationService> logger,
        TimeSpan interval)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));
    }

    public void Schedule(string modelName, Uri llamaServerBaseAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentNullException.ThrowIfNull(llamaServerBaseAddress);

        if (!IsLoopbackHttp(llamaServerBaseAddress))
        {
            return;
        }

        _targets.AddOrUpdate(modelName,
            _ => new CalibrationTarget(llamaServerBaseAddress, DateTimeOffset.MinValue),
            (_, current) => current with
            {
                BaseAddress = llamaServerBaseAddress
            });
        _wake.Writer.TryWrite(0);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wake = _wake.Reader.ReadAsync(stoppingToken).AsTask();
        var tick = Task.Delay(_interval, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAny(wake, tick).ConfigureAwait(false);

            if (wake.IsCompleted)
            {
                _ = await wake.ConfigureAwait(false);
                wake = _wake.Reader.ReadAsync(stoppingToken).AsTask();
            }

            if (tick.IsCompleted)
            {
                tick = Task.Delay(_interval, stoppingToken);
            }

            var now = DateTimeOffset.UtcNow;
            foreach (var (modelName, target) in _targets)
            {
                if (target.NextDueUtc > now)
                {
                    continue;
                }

                _targets.TryUpdate(modelName, target with
                {
                    NextDueUtc = now + _interval
                }, target);
                await TryCalibrateAsync(modelName, target.BaseAddress, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task<bool> TryCalibrateAsync(string modelName, Uri llamaServerBaseAddress, CancellationToken cancellationToken)
    {
        if (!IsLoopbackHttp(llamaServerBaseAddress))
        {
            return false;
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
            response.EnsureSuccessStatusCode();

            await using var contentStream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: timeout.Token).ConfigureAwait(false);
            if (!document.RootElement.TryGetProperty("tokens", out var tokens)
                || tokens.ValueKind != JsonValueKind.Array
                || tokens.GetArrayLength() <= 0)
            {
                return false;
            }

            var divisor = CalculateDivisor(CalibrationText.Length, tokens.GetArrayLength());
            _store.SetDivisor(modelName, divisor);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException)
        {
            _ = exception;
            _logger.LogDebug("llama.cpp token-estimator calibration was unavailable; retaining the prior bounded divisor.");
            return false;
        }
    }

    internal static int CalculateDivisor(int characterCount, int tokenCount)
    {
        if (characterCount <= 0 || tokenCount <= 0)
        {
            return TokenEstimatorCalibrationStore.DefaultCharsPerToken;
        }

        return Math.Clamp(characterCount / tokenCount,
            TokenEstimatorCalibrationStore.MinimumCharsPerToken,
            TokenEstimatorCalibrationStore.MaximumCharsPerToken);
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

    private sealed record CalibrationTarget(Uri BaseAddress, DateTimeOffset NextDueUtc);
}
