namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Central benchmark exception → HTTP mapper. The global handler and the batch endpoint route handled exceptions
///     through this type so the status code, machine-readable <c>code</c>, and operator-safe message stay in one place.
///     Bodies are RFC 7807 <c>application/problem+json</c>: the message is the ProblemDetails
///     <c>detail</c> and the <see cref="BenchmarkErrorCode" /> name is carried in the <c>code</c> extension member.
/// </summary>
internal static class BenchmarkEndpointSupport
{
    public static bool IsHandled(Exception exception) =>
        exception is BenchmarkNotFoundException or BenchmarkValidationException or BenchmarkConflictException or BenchmarkEligibilityException
            or BenchmarkUnsupportedKvCacheTypeException or BenchmarkJudgePolicyChangedException;

    /// <summary>
    ///     The run's current judge verdict, decrypted, or null when it has no attempt or no stored result. EVERY
    ///     endpoint that returns the run detail shape reads it here: a mutation response that skipped it would render
    ///     as "not judged" for a run whose GET shows a full verdict.
    /// </summary>
    public static async Task<BenchmarkJudgeResultV2?> ReadVerdictAsync(IBenchmarkStore store, BenchmarkRunRecord run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(run);
        if (run.Judge?.AttemptId is not { } attemptId)
        {
            return null;
        }

        var attempt = await store.GetJudgeAttemptAsync(attemptId, ct).ConfigureAwait(false);
        return BenchmarkJudgeSerialization.DeserializeResult(attempt?.ResultJson);
    }

    public static IResult Error(Exception exception)
    {
        var (statusCode, code, message) = Classify(exception);
        return Problem(statusCode, code, message);
    }

    /// <summary>
    ///     The same mapping <see cref="Error" /> applies, as data rather than as a response — the batch endpoint reports
    ///     a refused matrix cell inside a 200 body, and reading the code off a built <see cref="IResult" /> is not a
    ///     thing you can do. One switch, so a per-item rejection and a single-run failure can never disagree.
    /// </summary>
    public static (int StatusCode, BenchmarkErrorCode Code, string Message) Classify(Exception exception) =>
        exception switch
        {
            BenchmarkNotFoundException or KeyNotFoundException => (StatusCodes.Status404NotFound, BenchmarkErrorCode.NotFound,
                "The requested benchmark resource was not found."),
            BenchmarkValidationException => (StatusCodes.Status400BadRequest, BenchmarkErrorCode.InvalidRequest, exception.Message),
            BenchmarkEligibilityException => (StatusCodes.Status422UnprocessableEntity, ClassifyEligibility(exception.Message), exception.Message),
            BenchmarkUnsupportedKvCacheTypeException => (StatusCodes.Status422UnprocessableEntity, BenchmarkErrorCode.UnsupportedKvCacheType,
                exception.Message),
            BenchmarkJudgePolicyChangedException => (StatusCodes.Status409Conflict, BenchmarkErrorCode.JudgePolicyChanged,
                "The project's judge policy changed. Refresh and retry."),
            BenchmarkConflictException conflict => (StatusCodes.Status409Conflict, ClassifyConflict(conflict.Code),
                SafeConflictMessage(conflict.Code)),
            _ => throw exception
        };

    /// <summary>
    ///     Builds the problem body. <paramref name="code" /> is emitted as its enum name (matching the string the
    ///     global <c>JsonStringEnumConverter</c> produced when this was a bespoke error DTO) so clients keep switching
    ///     on the same values.
    /// </summary>
    public static IResult Problem(int statusCode, BenchmarkErrorCode code, string message) =>
        Results.Problem(statusCode: statusCode,
            detail: message,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = code.ToString()
            });

    private static BenchmarkErrorCode ClassifyEligibility(string message) =>
        message.Contains("model", StringComparison.OrdinalIgnoreCase)
            ? BenchmarkErrorCode.IneligibleModel
            : BenchmarkErrorCode.IneligibleAgent;

    private static BenchmarkErrorCode ClassifyConflict(string code) =>
        code switch
        {
            "VersionConflict" => BenchmarkErrorCode.VersionConflict,
            "ProjectFrozen" => BenchmarkErrorCode.ProjectFrozen,
            "ActiveRun" => BenchmarkErrorCode.ActiveRun,
            "FreezeDependencyChanged" => BenchmarkErrorCode.FreezeDependencyChanged,
            "FingerprintChanged" => BenchmarkErrorCode.FingerprintChanged,
            "RejudgeRequired" => BenchmarkErrorCode.RejudgeRequired,
            "JudgeAttemptsActive" => BenchmarkErrorCode.JudgeAttemptsActive,
            "JudgeAttemptActive" => BenchmarkErrorCode.JudgeAttemptActive,
            "JudgePolicyAlreadyApplied" => BenchmarkErrorCode.JudgePolicyAlreadyApplied,
            "JudgeDisabled" => BenchmarkErrorCode.JudgeDisabled,
            "PrimaryNotSucceeded" => BenchmarkErrorCode.PrimaryNotSucceeded,
            _ => BenchmarkErrorCode.InvalidLifecycleTransition
        };

    private static string SafeConflictMessage(string code) =>
        code switch
        {
            "VersionConflict" => "The resource version changed. Refresh and retry.",
            "ProjectFrozen" => "The benchmark project has runs and is frozen.",
            "ActiveRun" => "The benchmark run is active and cannot be deleted.",
            "FreezeDependencyChanged" => "A benchmark dependency changed while the run was being frozen.",
            "FingerprintChanged" => "The installed model content changed.",
            "RejudgeRequired" => "Changing the judge re-scores every run of this project. Confirm the re-judge to continue.",
            "JudgeAttemptsActive" => "A judging of this project is still running. Wait for it or cancel it first.",
            "JudgeAttemptActive" => "A judging of this run is still running.",
            "JudgePolicyAlreadyApplied" => "This run is already judged under the current policy and judge runtime.",
            "JudgeDisabled" => "This project has no judge policy to judge under.",
            "PrimaryNotSucceeded" => "The benchmark run has no stored output to judge.",
            _ => "The benchmark lifecycle transition is not allowed."
        };
}
