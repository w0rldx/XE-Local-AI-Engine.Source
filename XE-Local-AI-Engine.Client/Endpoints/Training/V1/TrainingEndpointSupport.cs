namespace XE_Local_AI_Engine.Client.Endpoints.Training.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Centralized store-exception → HTTP mapping for every training endpoint, mirroring
///     <c>BenchmarkEndpointSupport</c>. An exception outside the handled set is rethrown so a genuine fault still reaches
///     the global handler rather than being flattened into a 4xx.
/// </summary>
internal static class TrainingEndpointSupport
{
    public static bool IsHandled(Exception exception) =>
        exception is TrainingStoreException or KeyNotFoundException;

    public static IResult Error(Exception exception) =>
        exception switch
        {
            TrainingNotFoundException or KeyNotFoundException =>
                TypedResults.NotFound(Response(TrainingErrorCode.NotFound, "The requested training resource was not found.")),
            TrainingValidationException => TypedResults.BadRequest(Response(TrainingErrorCode.InvalidRequest, exception.Message)),
            TrainingConflictException conflict => TypedResults.Conflict(Response(Classify(conflict.Code), SafeMessage(conflict.Code))),
            _ => throw exception
        };

    /// <summary>Reads a persisted JSON payload column, returning <see langword="null" /> rather than throwing on a legacy or corrupt row.</summary>
    public static T? Read<T>(ReadOnlyMemory<byte>? payload)
        where T : class
    {
        if (payload is not { } bytes || bytes.IsEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(bytes.Span, TrainingJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static TrainingErrorResponse Response(TrainingErrorCode code, string message) =>
        new()
        {
            Code = code,
            Message = message
        };

    private static TrainingErrorCode Classify(string code) =>
        code switch
        {
            "VersionConflict" or "DuplicateWork" => TrainingErrorCode.VersionConflict,
            "GenerationActive" => TrainingErrorCode.GenerationActive,
            "DefinitionReferenced" => TrainingErrorCode.DefinitionReferenced,
            "DatasetReferenced" => TrainingErrorCode.DatasetReferenced,
            "TrainingBusy" => TrainingErrorCode.TrainingBusy,
            _ => TrainingErrorCode.InvalidLifecycleTransition
        };

    private static string SafeMessage(string code) =>
        code switch
        {
            "VersionConflict" => "The resource version changed. Refresh and retry.",
            "DuplicateWork" => "A generation work item already exists for this dataset.",
            "GenerationActive" => "The dataset is still generating.",
            "DefinitionReferenced" => "The definition has datasets and cannot be deleted.",
            "DatasetReferenced" => "A training run was created from this dataset and it cannot be deleted.",
            "TrainingBusy" => "A training run is active; dataset generation cannot start.",
            "RunActive" => "The training run is still queued or running.",
            "RunEvaluated" => "An evaluation run was created from this training run and it cannot be deleted.",
            "EvaluationBound" => "The evaluation run is part of a comparison report.",
            "EvaluationActive" => "The evaluation run is still queued or running.",
            "EvaluationComplete" => "The evaluation run has already scored every hold-out sample.",
            "EvaluationTerminal" => "The evaluation run has already finished.",
            _ => "The training lifecycle transition is not allowed."
        };
}
