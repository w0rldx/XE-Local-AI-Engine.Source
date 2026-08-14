namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using Microsoft.AspNetCore.Http.HttpResults;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

internal static class BenchmarkEndpointSupport
{
    public static IResult Error(Exception exception) => exception switch
    {
        BenchmarkNotFoundException or KeyNotFoundException => TypedResults.NotFound(Response(BenchmarkErrorCode.NotFound, "The requested benchmark resource was not found.")),
        BenchmarkValidationException => TypedResults.BadRequest(Response(BenchmarkErrorCode.InvalidRequest, exception.Message)),
        BenchmarkEligibilityException => TypedResults.UnprocessableEntity(Response(ClassifyEligibility(exception.Message), exception.Message)),
        BenchmarkConflictException conflict => TypedResults.Conflict(Response(ClassifyConflict(conflict.Code), SafeConflictMessage(conflict.Code))),
        _ => throw exception
    };

    private static BenchmarkErrorResponse Response(BenchmarkErrorCode code, string message) => new() { Code = code, Message = message };

    private static BenchmarkErrorCode ClassifyEligibility(string message) =>
        message.Contains("model", StringComparison.OrdinalIgnoreCase)
            ? BenchmarkErrorCode.IneligibleModel
            : BenchmarkErrorCode.IneligibleAgent;

    private static BenchmarkErrorCode ClassifyConflict(string code) => code switch
    {
        "VersionConflict" => BenchmarkErrorCode.VersionConflict,
        "ProjectFrozen" => BenchmarkErrorCode.ProjectFrozen,
        "ActiveRun" => BenchmarkErrorCode.ActiveRun,
        "FreezeDependencyChanged" => BenchmarkErrorCode.FreezeDependencyChanged,
        "FingerprintChanged" => BenchmarkErrorCode.FingerprintChanged,
        _ => BenchmarkErrorCode.InvalidLifecycleTransition
    };

    private static string SafeConflictMessage(string code) => code switch
    {
        "VersionConflict" => "The resource version changed. Refresh and retry.",
        "ProjectFrozen" => "The benchmark project has runs and is frozen.",
        "ActiveRun" => "The benchmark run is active and cannot be deleted.",
        "FreezeDependencyChanged" => "A benchmark dependency changed while the run was being frozen.",
        "FingerprintChanged" => "The installed model content changed.",
        _ => "The benchmark lifecycle transition is not allowed."
    };
}
