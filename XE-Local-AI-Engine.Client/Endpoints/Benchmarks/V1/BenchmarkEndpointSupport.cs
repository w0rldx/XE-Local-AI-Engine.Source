namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     Central benchmark exception → HTTP mapper. Every benchmark endpoint routes its handled exceptions through
///     <see cref="Error" /> so the status code, the machine-readable <c>code</c> and the operator-safe message stay in
///     one place. Bodies are RFC 7807 <c>application/problem+json</c>: the message is the ProblemDetails
///     <c>detail</c> and the <see cref="BenchmarkErrorCode" /> name is carried in the <c>code</c> extension member.
/// </summary>
internal static class BenchmarkEndpointSupport
{
    public static IResult Error(Exception exception) =>
        exception switch
        {
            BenchmarkNotFoundException or KeyNotFoundException => Problem(StatusCodes.Status404NotFound, BenchmarkErrorCode.NotFound, "The requested benchmark resource was not found."),
            BenchmarkValidationException => Problem(StatusCodes.Status400BadRequest, BenchmarkErrorCode.InvalidRequest, exception.Message),
            BenchmarkEligibilityException => Problem(StatusCodes.Status422UnprocessableEntity, ClassifyEligibility(exception.Message), exception.Message),
            BenchmarkUnsupportedKvCacheTypeException => Problem(StatusCodes.Status422UnprocessableEntity, BenchmarkErrorCode.UnsupportedKvCacheType, exception.Message),
            BenchmarkConflictException conflict => Problem(StatusCodes.Status409Conflict, ClassifyConflict(conflict.Code), SafeConflictMessage(conflict.Code)),
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
            _ => "The benchmark lifecycle transition is not allowed."
        };
}
