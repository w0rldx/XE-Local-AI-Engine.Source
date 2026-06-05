namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using System.Globalization;
using global::Docker.DotNet;
using global::Grpc.Core;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.HostAgent.Grpc.Contracts;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;

/// <summary>
///     Serves the narrow <c>ModelFitUtilityControl</c> gRPC contract: runs a digest-pinned, approved
///     llmfit utility image for a recommend or benchmark operation. The global HMAC interceptor authenticates every
///     call (the rpc is unary by design), so this service does no auth wiring.
///     This is NOT a general Docker executor. The only selectable unit on the wire is INTENT (operation + validated
///     params + a pinned image reference). The actual <c>llmfit</c> argv is built HERE from a fixed server-side command
///     profile — never accepted from the request — and the image reference is RE-validated against the allowlist
///     (defense in depth on top of the node-side validation) before anything runs. The verified command profiles are:
///     <list type="bullet">
///         <item>
///             <c>recommend</c>: <c>(--cpu-cores n)? (--ram gG)? (--memory vG)? recommend --json --use-case &lt;uc&gt; --limit &lt;n&gt;</c> — HW overrides MUST precede the subcommand (verified
///             against the llmfit CLI).
///         </item>
///         <item><c>bench</c>: <c>bench --provider &lt;provider&gt; --url &lt;url&gt; --json &lt;model&gt;</c> — model name is REQUIRED for ollama bench.</item>
///     </list>
/// </summary>
public sealed class ModelFitUtilityControlService : ModelFitUtilityControl.ModelFitUtilityControlBase
{
    private const string RecommendSubcommand = "recommend";
    private const string BenchmarkSubcommand = "bench";
    private readonly ILogger<ModelFitUtilityControlService> _logger;
    private readonly ModelFitUtilityOptions _options;

    private readonly IDockerRuntimeClient _runtimeClient;
    private readonly TimeProvider _timeProvider;

    public ModelFitUtilityControlService(IDockerRuntimeClient runtimeClient,
        IOptions<ModelFitUtilityOptions> options,
        TimeProvider timeProvider,
        ILogger<ModelFitUtilityControlService> logger)
    {
        _runtimeClient = runtimeClient ?? throw new ArgumentNullException(nameof(runtimeClient));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<RunModelFitUtilityReply> RunModelFitUtility(RunModelFitUtilityRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);

        var startedAt = _timeProvider.GetUtcNow();

        // 1) Image reference: strict canonical parse + repository allowlist. Defense in depth — the node already
        // validated, the HostAgent re-validates and refuses to run anything else.
        DockerImageReference image;
        try
        {
            image = DockerImageReference.Parse(request.ImageReference);
        }
        catch (FormatException)
        {
            return RejectedReply(startedAt, "image reference rejected");
        }

        if (!_options.AllowedImageRepositories.Contains(image.Repository, StringComparer.Ordinal))
        {
            return RejectedReply(startedAt, "image reference rejected");
        }

        // 2) Build the argv from the operation + validated params. Validation is HostAgent-side too (defense in depth).
        List<string> arguments;
        switch (request.Operation)
        {
            case ModelFitOperationMessage.ModelFitOperationRecommend:
                arguments = BuildRecommendArguments(request);
                break;
            case ModelFitOperationMessage.ModelFitOperationBenchmark:
                if (string.IsNullOrWhiteSpace(request.ModelName))
                {
                    return RejectedReply(startedAt, "benchmark requires a model name");
                }

                arguments = BuildBenchmarkArguments(request);
                break;
            default:
                return RejectedReply(startedAt, "unsupported operation");
        }

        // 3) Network + environment. NONE → no network; RUNTIME → the managed runtime network (so http://ollama:11434
        // resolves). For bench, also set OLLAMA_HOST as a belt-and-suspenders alongside --url.
        var attachRuntime = request.Network == ModelFitNetworkModeMessage.ModelFitNetworkModeRuntime;
        var networkName = attachRuntime ? _options.RuntimeNetworkName : null;
        var environment = BuildEnvironment(request, attachRuntime);

        // 4) Timeout: an explicit positive request value wins; otherwise the operation-specific default ceiling.
        var timeout = ResolveTimeout(request);

        var spec = new UtilityContainerRunSpec
        {
            Image = image.CanonicalReference,
            Arguments = arguments,
            Environment = environment,
            NetworkName = networkName,
            RetainOnFailure = _options.RetainFailedContainersForDebug
        };

        // Distinguish timeout from caller-cancel: a per-run timeout CTS fires the timeout branch; the caller token fires
        // the cancel branch. The linked token is what RunUtilityContainerAsync observes.
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, timeoutCts.Token);

        UtilityContainerRunResult result;
        try
        {
            result = await _runtimeClient.RunUtilityContainerAsync(spec, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DockerApiException or InvalidOperationException)
        {
            // A runtime failure carries no client-safe detail beyond a generic one-liner; the underlying reason is logged
            // (type + message only) so host paths and internal detail never cross the wire.
            _logger.LogInformation("Model-fit utility run failed: {ExceptionType}: {Reason}", exception.GetType().Name, exception.Message);
            return new RunModelFitUtilityReply
            {
                Status = ModelFitTerminalStatusMessage.Failed,
                ExitCode = -1,
                Completed = false,
                DurationMs = 0,
                StartedAt = Timestamp.FromDateTimeOffset(startedAt),
                CompletedAt = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()),
                SanitizedError = "model-fit utility run failed"
            };
        }

        var completedAt = _timeProvider.GetUtcNow();
        var status = ResolveTerminalStatus(result, timeoutCts, context.CancellationToken);

        return new RunModelFitUtilityReply
        {
            Status = status,
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Completed = result.Completed,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            StartedAt = Timestamp.FromDateTimeOffset(startedAt),
            CompletedAt = Timestamp.FromDateTimeOffset(completedAt),
            SanitizedError = SanitizedErrorFor(status, result.ExitCode)
        };
    }

    private List<string> BuildRecommendArguments(RunModelFitUtilityRequest request)
    {
        // HW overrides are GLOBAL flags and MUST precede the subcommand (verified against the llmfit CLI; placing them after exits 2).
        var arguments = new List<string>();
        AppendOverride(arguments, "--cpu-cores", request.CpuCoresOverride, suffixG: false);
        AppendOverride(arguments, "--ram", request.RamOverrideGb, suffixG: true);
        AppendOverride(arguments, "--memory", request.VramOverrideGb, suffixG: true);

        arguments.Add(RecommendSubcommand);
        arguments.Add("--json");

        // use_case is appended only when non-empty; the node enforces the six-value allowlist (llmfit silently accepts
        // an unknown use-case), so this is just a non-empty guard.
        if (!string.IsNullOrWhiteSpace(request.UseCase))
        {
            arguments.Add("--use-case");
            arguments.Add(request.UseCase);
        }

        var limit = ClampLimit(request.Limit);
        arguments.Add("--limit");
        arguments.Add(limit.ToString(CultureInfo.InvariantCulture));

        return arguments;
    }

    private static List<string> BuildBenchmarkArguments(RunModelFitUtilityRequest request)
    {
        // bench --provider <provider> --url <url> --json <model> (verified against the llmfit CLI). The positional model name is
        // required for ollama bench. provider_url is benchmark-only — never passed for recommend.
        var arguments = new List<string>
        {
            BenchmarkSubcommand,
            "--provider",
            string.IsNullOrWhiteSpace(request.ProviderName) ? "ollama" : request.ProviderName
        };

        if (!string.IsNullOrWhiteSpace(request.ProviderUrl))
        {
            arguments.Add("--url");
            arguments.Add(request.ProviderUrl);
        }

        arguments.Add("--json");
        arguments.Add(request.ModelName);

        return arguments;
    }

    private static Dictionary<string, string> BuildEnvironment(RunModelFitUtilityRequest request, bool attachRuntime)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        // Belt-and-suspenders for the benchmark path: set OLLAMA_HOST alongside --url so the provider URL resolves even
        // if a future llmfit drops --url. Only when a runtime network is attached and a URL was supplied.
        if (attachRuntime
            && request.Operation == ModelFitOperationMessage.ModelFitOperationBenchmark
            && !string.IsNullOrWhiteSpace(request.ProviderUrl))
        {
            environment["OLLAMA_HOST"] = request.ProviderUrl;
        }

        return environment;
    }

    private TimeSpan ResolveTimeout(RunModelFitUtilityRequest request)
    {
        if (request.TimeoutSeconds > 0)
        {
            return TimeSpan.FromSeconds(request.TimeoutSeconds);
        }

        var seconds = request.Operation == ModelFitOperationMessage.ModelFitOperationBenchmark
            ? _options.BenchmarkMaxRuntimeSeconds
            : _options.DefaultMaxRuntimeSeconds;
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private int ClampLimit(int limit)
    {
        var max = Math.Max(1, _options.MaxRecommendLimit);
        return Math.Clamp(limit <= 0 ? 1 : limit, 1, max);
    }

    private static void AppendOverride(List<string> arguments, string flag, int value, bool suffixG)
    {
        if (value <= 0)
        {
            return;
        }

        arguments.Add(flag);
        arguments.Add(suffixG
            ? string.Create(CultureInfo.InvariantCulture, $"{value}G")
            : value.ToString(CultureInfo.InvariantCulture));
    }

    private static ModelFitTerminalStatusMessage ResolveTerminalStatus(UtilityContainerRunResult result,
        CancellationTokenSource timeoutCts,
        CancellationToken callerToken)
    {
        if (!result.Completed)
        {
            // The timeout CTS firing means the max runtime elapsed; otherwise the run was cancelled (caller token, or a
            // best-effort docker-side teardown). The timeout branch is checked first because a timeout also cancels the
            // linked token the caller sees. callerToken is read to make the cancel-vs-timeout disambiguation explicit.
            _ = callerToken;
            return timeoutCts.IsCancellationRequested
                ? ModelFitTerminalStatusMessage.TimedOut
                : ModelFitTerminalStatusMessage.Cancelled;
        }

        return result.ExitCode == 0 ? ModelFitTerminalStatusMessage.Succeeded : ModelFitTerminalStatusMessage.Failed;
    }

    private static string SanitizedErrorFor(ModelFitTerminalStatusMessage status, int exitCode)
    {
        return status switch
        {
            ModelFitTerminalStatusMessage.Succeeded => string.Empty,
            ModelFitTerminalStatusMessage.TimedOut => "model-fit utility run timed out",
            ModelFitTerminalStatusMessage.Cancelled => "model-fit utility run was cancelled",
            // Never echo raw stderr or secrets — a generic one-liner plus the exit code is the whole operator story.
            _ => string.Create(CultureInfo.InvariantCulture, $"model-fit utility run failed (exit code {exitCode})")
        };
    }

    private RunModelFitUtilityReply RejectedReply(DateTimeOffset startedAt, string sanitizedError)
    {
        // A rejected request never reaches Docker — return a FAILED reply with a generic one-liner and no output.
        var now = _timeProvider.GetUtcNow();
        return new RunModelFitUtilityReply
        {
            Status = ModelFitTerminalStatusMessage.Failed,
            ExitCode = -1,
            Completed = false,
            DurationMs = 0,
            StartedAt = Timestamp.FromDateTimeOffset(startedAt),
            CompletedAt = Timestamp.FromDateTimeOffset(now),
            SanitizedError = sanitizedError
        };
    }
}
