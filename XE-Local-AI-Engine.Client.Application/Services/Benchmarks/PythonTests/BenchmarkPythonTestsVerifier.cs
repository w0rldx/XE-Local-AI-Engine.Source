namespace XE_Local_AI_Engine.Client.Services.Benchmarks.PythonTests;

using System.Text;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;

/// <summary>
///     Decides one <c>pythonTests</c> criterion by running the answer's code against the operator's tests in the
///     compute sandbox. Public because <see cref="BenchmarkJudgeExecutor" /> is, and the gateway it wraps is internal.
/// </summary>
public interface IBenchmarkPythonTestsVerifier
{
    /// <summary>
    ///     Runs the criterion. Throws <see cref="BenchmarkExecutionException" /> — prefixed with
    ///     <see cref="BenchmarkRunJudgeStates.VerifierUnavailablePrefix" /> — when the SANDBOX could not be trusted to
    ///     run it, which fails the judging rather than scoring 0. A candidate that misbehaves inside a working sandbox
    ///     is a 0, and comes back as a normal failed result.
    /// </summary>
    Task<BenchmarkJudgeVerifierResultV1> VerifyAsync(BenchmarkJudgeRubricCriterionV1 criterion,
        string answer,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc />
internal sealed class BenchmarkPythonTestsVerifier : IBenchmarkPythonTestsVerifier
{
    private const int EvidenceChars = 2048;

    private readonly IComputeToolGateway _gateway;
    private readonly ILogger<BenchmarkPythonTestsVerifier> _logger;
    private readonly ComputeOptions _options;

    public BenchmarkPythonTestsVerifier(IComputeToolGateway gateway,
        IOptions<ComputeOptions> options,
        ILogger<BenchmarkPythonTestsVerifier> logger)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<BenchmarkJudgeVerifierResultV1> VerifyAsync(BenchmarkJudgeRubricCriterionV1 criterion,
        string answer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criterion);
        ArgumentNullException.ThrowIfNull(answer);

        BenchmarkPythonTestsConfigV1 config;
        try
        {
            config = BenchmarkJudgeVerifierConfig.Parse(BenchmarkJudgeCriterionKinds.PythonTests, criterion.Config)?.PythonTests
                     ?? throw new BenchmarkExecutionException($"Rubric criterion '{criterion.Id}' carries no pythonTests configuration.");
        }
        catch (BenchmarkJudgePolicyValidationException exception)
        {
            throw new BenchmarkExecutionException($"Rubric criterion '{criterion.Id}' cannot be verified: {exception.Message}");
        }

        var candidate = BenchmarkPythonCodeExtraction.Extract(answer, config.Extract);
        if (candidate.Length == 0)
        {
            return Fail(criterion, "The answer carried no code to run.", candidate);
        }

        // The config's timeout can only TIGHTEN: the node's own ceiling is what bounds the sandbox call, and a
        // criterion is not allowed to buy itself more wall clock than the operator granted the compute tool.
        var timeout = Math.Min(config.TimeoutSeconds ?? BenchmarkPythonTestsHarness.DefaultTimeoutSeconds, _options.TimeoutSeconds);
        var composed = BenchmarkPythonTestsHarness.Compose(candidate, config.TestCode ?? string.Empty, config.Exports ?? [], timeout);
        if (composed.Program is null)
        {
            return Fail(criterion, composed.Refusal ?? "The execution harness could not be composed.", candidate);
        }

        // requireResourceLimits: TRUE. This is operator-authored test code executed unattended, possibly on a Quartz
        // schedule, so a host that cannot bound CPU, memory and process count is refused rather than run — strictly
        // tighter than run_python, which a human approves call by call and which deliberately still runs there.
        var outcome = await _gateway.ExecuteDetailedAsync(new ComputeRunToolRequest
        {
            Code = composed.Program
        }, requireResourceLimits: true, cancellationToken).ConfigureAwait(false);
        if (outcome.Result is not { } result)
        {
            throw Unavailable(criterion, outcome.RefusalCode ?? "unknown", outcome.RefusalMessage ?? "The compute sandbox refused the execution.");
        }

        var verdict = BenchmarkPythonTestsHarness.ReadVerdict(result.StandardOutput, composed.Nonce);
        if (verdict.IsUnavailable)
        {
            // The parent refuses to run un-hardened: without PR_SET_DUMPABLE a same-uid child could ptrace it or read
            // /proc/<pid>/mem for the nonce, and a verdict that can be rewritten is not a verdict.
            throw Unavailable(criterion, verdict.UnavailableReason ?? "unknown",
                $"The pythonTests harness could not harden itself ({verdict.UnavailableReason}).");
        }

        if (verdict.MarkerCount > 1)
        {
            _logger.LogWarning("A pythonTests verification saw {MarkerCount} verdict markers for criterion '{CriterionId}'; scoring it 0 and treating the extra markers as forged.",
                verdict.MarkerCount,
                criterion.Id);
        }

        return new BenchmarkJudgeVerifierResultV1(criterion.Id,
            BenchmarkJudgeCriterionKinds.PythonTests,
            verdict.Scored,
            Detail(verdict, result, candidate));
    }

    private static BenchmarkJudgeVerifierResultV1 Fail(BenchmarkJudgeRubricCriterionV1 criterion, string reason, string candidate) =>
        new(criterion.Id,
            BenchmarkJudgeCriterionKinds.PythonTests,
            Passed: false,
            $"{reason} Extracted code ({candidate.Length} chars): {Bound(candidate, 512)}");

    private BenchmarkExecutionException Unavailable(BenchmarkJudgeRubricCriterionV1 criterion, string code, string message)
    {
        _logger.LogWarning("A pythonTests verification for criterion '{CriterionId}' could not run: {RefusalCode}. The run is unranked rather than scored zero.",
            criterion.Id,
            code);

        // The prefix is what BenchmarkStore turns into the `verifier-unavailable` exclusion reason, so an operator
        // reading the ranking is told to enable Compute or fix the sandbox rather than that the model scored nothing.
        return new BenchmarkExecutionException(BenchmarkRunJudgeStates.VerifierUnavailablePrefix + message);
    }

    /// <summary>
    ///     The evidence: the counts (best-effort by design — a bare test script has no runner to enumerate, so it is
    ///     one implicit case), the parent's own record of what failed, and both captured streams. The child's stderr is
    ///     where a candidate's traceback lands and is labelled candidate-controlled; the parent's raw STDOUT is never
    ///     included, because that is the channel the verdict is read from.
    /// </summary>
    private static string Detail(BenchmarkPythonTestsVerdict verdict, SandboxCommandResult result, string candidate)
    {
        var builder = new StringBuilder();
        if (verdict.MarkerCount == 0)
        {
            builder.Append(result.Completed
                ? "The harness printed no verdict, so the candidate denied one (it killed the harness, or the jail was terminated). "
                : $"The execution did not finish within {result.Duration.TotalSeconds:F0}s and its process tree was terminated. ");
        }
        else if (verdict.MarkerCount > 1)
        {
            builder.Append(ForgedMarkers(verdict.MarkerCount));
        }
        else
        {
            builder.Append($"{verdict.PassedCount} of {verdict.Collected} collected cases passed ({verdict.Failed} failed, phase '{verdict.Phase}'). ");
            if (verdict.Error is { Length: > 0 } error)
            {
                builder.Append($"First failure: {Bound(error, 512)} ");
            }
        }

        builder.Append($"Extracted code ({candidate.Length} chars): {Bound(candidate, 512)}");
        if (result.StandardError is { Length: > 0 } stderr)
        {
            builder.Append($" | harness and candidate stderr (candidate-controlled): {Bound(stderr, EvidenceChars)}");
        }

        return builder.ToString();
    }

    private static string ForgedMarkers(int markerCount) =>
        $"{markerCount} verdict markers were printed where exactly one is possible; the result is refused as forged. ";

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : string.Concat(value.AsSpan(0, maximum), "…");
}
