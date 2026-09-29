namespace XE_Local_AI_Engine.Client.Services.Invocation.Context;

using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Tokenization;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Opportunistically calibrates token estimates for llama.cpp models that are serving real requests.</summary>
/// <remarks>
///     Each request performs only a bounded due check and queue write, so the <c>/tokenize</c> I/O runs here, outside
///     inference, and no timer can revisit a stale endpoint after an eject. A provider failure retains the prior
///     calibration, or the chars/4 fallback. The same round measures the model's tool-template preamble: the tokens its
///     chat template spends once per request that offers tools (see <see cref="CalculateToolTemplatePreamble" />).
/// </remarks>
internal sealed class LlamaTokenEstimatorCalibrationService : BackgroundService, ITokenEstimatorCalibrationScheduler
{
    internal const int DefaultWorkCapacity = 64;

    internal const string CalibrationText =
        "Token estimation calibration sample. The quick brown fox jumps over the lazy dog. " +
        "Structured data: {\"alpha\":123,\"enabled\":true,\"items\":[\"one\",\"two\",\"three\"]}. " +
        "Code: public static int Sum(int left, int right) => left + right; " +
        "Paths: /var/lib/models/example.gguf C:\\models\\example.gguf. " +
        "Repeatable prose keeps this sample independent from prompts, tools, users, and request content.";

    internal const string ProbeSystemPrompt = "You are a helpful assistant.";

    internal const string ProbeUserMessage = "What is the weather in Paris?";

    /// <summary>
    ///     The FIXED probe tool set, deliberately small and typical: what the template adds beyond these two definitions is the preamble,
    ///     and it must not depend on which tools a given invocation happens to offer.
    /// </summary>
    internal static readonly IReadOnlyList<AIFunctionDeclaration> ProbeTools =
    [
        CreateProbeTool("get_weather",
            "Get the current weather for a city.",
            """{"type":"object","properties":{"city":{"type":"string","description":"City name"}},"required":["city"]}"""),
        CreateProbeTool("search_documents",
            "Search the knowledge base and return the most relevant passages.",
            """{"type":"object","properties":{"query":{"type":"string","description":"Search text"},"limit":{"type":"integer"}},"required":["query"]}""")
    ];

    private static readonly HeuristicTokenEstimator ProbeEstimator = new();

    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Lock _sync = new();
    private readonly ITokenEstimatorCalibrationStore _store;
    private readonly ILlamaServerNativeClient _nativeClient;
    private readonly ILogger<LlamaTokenEstimatorCalibrationService> _logger;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, CalibrationTarget> _targets = new(StringComparer.Ordinal);
    private readonly Channel<CalibrationWork> _work;

    /// <summary>Calibration is housekeeping: its lease must not give a model loaded only for a draft the interactive idle lifetime.</summary>
    private const ModelResidencyIntent HousekeepingIntent = ModelResidencyIntent.Transient;

    private readonly ILlamaServerProcessSupervisor _supervisor;
    private long _generation;

    public LlamaTokenEstimatorCalibrationService(ILlamaServerNativeClient nativeClient,
        ITokenEstimatorCalibrationStore store,
        ILlamaServerProcessSupervisor supervisor,
        ILogger<LlamaTokenEstimatorCalibrationService> logger,
        TimeProvider timeProvider)
        : this(nativeClient, store, supervisor, logger, DefaultInterval, timeProvider, DefaultWorkCapacity)
    {
    }

    internal LlamaTokenEstimatorCalibrationService(ILlamaServerNativeClient nativeClient,
        ITokenEstimatorCalibrationStore store,
        ILlamaServerProcessSupervisor supervisor,
        ILogger<LlamaTokenEstimatorCalibrationService> logger,
        TimeSpan interval,
        TimeProvider timeProvider,
        int workCapacity)
    {
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _nativeClient = nativeClient ?? throw new ArgumentNullException(nameof(nativeClient));
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
                target = new CalibrationTarget
                {
                    BaseAddress = llamaServerBaseAddress,
                    Generation = ++_generation,
                    NextDueUtc = DateTimeOffset.MinValue,
                    InFlight = false
                };
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
        await foreach (var work in _work.Reader.ReadAllAsync(stoppingToken))
        {
            await TryCalibrateCurrentTargetAsync(work, stoppingToken);
        }
    }

    internal async Task<bool> TryCalibrateAsync(string modelName, Uri llamaServerBaseAddress, CancellationToken cancellationToken)
    {
        var result = await TryMeasureAsync(llamaServerBaseAddress, cancellationToken);
        if (result.Divisor is not { } divisor)
        {
            return false;
        }

        Store(modelName, divisor, result.ToolTemplatePreamble);
        return true;
    }

    /// <summary>
    ///     The preamble a template adds once per tool-offering request: the rendered count WITH the probe tools, minus the count
    ///     without them, minus what the budgeters already charge for the probe tools' own definitions.
    /// </summary>
    /// <remarks>
    ///     The probe charge uses the budgeters' own per-tool formula (a framed definition plus the wrapper constant) at the divisor just
    ///     measured, so the preamble is exactly the residual they miss and adding it once makes a tool round's estimate meet the template.
    ///     Negative when the per-tool constants already over-charge this template; the store clamps that to zero.
    /// </remarks>
    internal static int CalculateToolTemplatePreamble(int tokensWithTools, int tokensWithoutTools, int charsPerToken)
    {
        var probeCharge = 0;
        foreach (var tool in ProbeTools)
        {
            var definition = string.Concat(tool.Name, "\n", tool.Description, "\n", tool.JsonSchema.GetRawText());
            probeCharge += ProbeEstimator.EstimateTokensWithDivisor(new ChatMessage(ChatRole.System, definition), charsPerToken)
                           + TokenEstimatorCalibrationStore.ToolDefinitionWrapperTokens;
        }

        return tokensWithTools - tokensWithoutTools - probeCharge;
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

        // The probe is a real request against the model's process, dispatched long after the chat that scheduled it. Unleased, profiling's pre-spawn claim wins
        // and this POST lands on whatever now answers that port. A refused lease skips the round: calibration is opportunistic and the next request reschedules it.
        var acquisition = _supervisor.TryAcquireInferenceLease(work.ModelName, ModelRole.Chat, HousekeepingIntent);
        if (acquisition.ProcessEvicting || acquisition.ProcessProfiling)
        {
            lock (_sync)
            {
                if (IsCurrentTarget(work))
                {
                    _targets[work.ModelName] = _targets[work.ModelName] with
                    {
                        InFlight = false,
                        NextDueUtc = _timeProvider.GetUtcNow() + _interval
                    };
                }
            }

            return;
        }

        CalibrationResult result;
        using (acquisition.Lease)
        {
            result = await TryMeasureAsync(work.BaseAddress, cancellationToken);
        }

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
                Store(work.ModelName, divisor, result.ToolTemplatePreamble);
            }
        }
    }

    private void Store(string modelName, int divisor, int? toolTemplatePreamble)
    {
        _store.SetDivisor(modelName, divisor);
        if (toolTemplatePreamble is { } preamble)
        {
            _store.SetToolTemplatePreamble(modelName, preamble);
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

    private async Task<CalibrationResult> TryMeasureAsync(Uri llamaServerBaseAddress, CancellationToken cancellationToken)
    {
        if (!IsLoopbackHttp(llamaServerBaseAddress))
        {
            LogFailure(CalibrationFailureReason.RejectedEndpoint);
            return default;
        }

        var tokenCount = await TryReadCountAsync(ct => _nativeClient.TokenizeAsync(llamaServerBaseAddress, CalibrationText, ct), cancellationToken);
        if (tokenCount is not { } sampleTokens)
        {
            return default;
        }

        var divisor = CalculateDivisor(CalibrationText.Length, sampleTokens);

        // Both counts or neither: a preamble derived from one fresh and one missing count would be noise. A failure keeps the prior preamble.
        int? preamble = null;
        if (await TryReadCountAsync(ct => _nativeClient.CountPromptTokensAsync(llamaServerBaseAddress, ProbeSystemPrompt, ProbeUserMessage, [], ct),
                cancellationToken) is { } withoutTools
            && await TryReadCountAsync(ct => _nativeClient.CountPromptTokensAsync(llamaServerBaseAddress, ProbeSystemPrompt, ProbeUserMessage, ProbeTools, ct),
                cancellationToken) is { } withTools)
        {
            preamble = Math.Clamp(CalculateToolTemplatePreamble(withTools, withoutTools, divisor), 0, TokenEstimatorCalibrationStore.MaximumToolTemplatePreambleTokens);
        }

        LogMeasured(divisor, preamble);
        return new CalibrationResult(divisor, preamble);
    }

    /// <summary>Sends one counting request under the endpoint, redirect and status guards and reads its token count, or <see langword="null" /> on any failure.</summary>
    private async Task<int?> TryReadCountAsync(Func<CancellationToken, Task<LlamaServerTokenizeResponse>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await send(timeout.Token);

            if (IsRedirect(response.StatusCode))
            {
                LogFailure(CalibrationFailureReason.Redirect);
                return null;
            }

            if (response.FinalRequestUri is { } finalAddress && !IsLoopbackHttp(finalAddress))
            {
                LogFailure(CalibrationFailureReason.FinalEndpointRejected);
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                LogFailure(CalibrationFailureReason.HttpStatus);
                return null;
            }

            if (await response.ReadTokenCountAsync(timeout.Token) is not { } tokenCount)
            {
                LogFailure(CalibrationFailureReason.InvalidPayload);
                return null;
            }

            return tokenCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LogFailure(CalibrationFailureReason.Timeout);
            return null;
        }
        catch (HttpRequestException)
        {
            LogFailure(CalibrationFailureReason.RequestFailure);
            return null;
        }
    }

    private void LogFailure(CalibrationFailureReason reason)
    {
        // Bounded, content-free evidence only: no model, URI, port, prompt, tool, user, request, or response data.
        _logger.LogDebug("llama.cpp token-estimator calibration unavailable ({FailureReason}); retaining the prior bounded calibration.",
            reason.ToString());
    }

    private void LogMeasured(int divisor, int? toolTemplatePreamble)
    {
        // Same bounded evidence as LogFailure: the two numbers alone, never the model, endpoint or sample. A null preamble kept the prior one.
        _logger.LogDebug("llama.cpp token-estimator calibration measured {CharsPerToken} chars per token and a tool-template preamble of {ToolTemplatePreambleTokens} tokens.",
            divisor,
            toolTemplatePreamble);
    }

    private static AIFunctionDeclaration CreateProbeTool(string name, string description, string schema)
    {
        using var document = JsonDocument.Parse(schema);
        return AIFunctionFactory.CreateDeclaration(name, description, document.RootElement.Clone());
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

    private sealed record CalibrationTarget
    {
        public required Uri BaseAddress { get; init; }

        public required long Generation { get; init; }

        public required DateTimeOffset NextDueUtc { get; init; }

        public required bool InFlight { get; init; }
    }

    private readonly record struct CalibrationWork(string ModelName, Uri BaseAddress, long Generation);

    [StructLayout(LayoutKind.Auto)]
    private readonly record struct CalibrationResult(int? Divisor, int? ToolTemplatePreamble);

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
