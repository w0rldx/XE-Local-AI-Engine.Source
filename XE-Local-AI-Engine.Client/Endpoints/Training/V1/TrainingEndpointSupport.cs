namespace XE_Local_AI_Engine.Client.Endpoints.Training.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;

/// <summary>
///     Centralized Training store-exception → HTTP mapping used by <c>TrainingExceptionHandler</c>. The persisted-JSON
///     reader stays here because the Training endpoint mappers share its corrupt-row recovery contract.
/// </summary>
internal static class TrainingEndpointSupport
{
    public static IResult Error(Exception exception) =>
        exception switch
        {
            TrainingNotFoundException =>
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
            "TrainingBusy" => "Training, an evaluation or an export holds the GPU; dataset generation cannot start until it finishes.",
            "RunActive" => "The training run is still queued or running.",
            "RunEvaluated" => "An evaluation run was created from this training run and it cannot be deleted.",
            "EvaluationBound" => "The evaluation run is part of a comparison report.",
            "EvaluationActive" => "The evaluation run is still queued or running.",
            "EvaluationComplete" => "The evaluation run has already scored every hold-out sample.",
            "EvaluationTerminal" => "The evaluation run has already finished.",
            _ => "The training lifecycle transition is not allowed."
        };
}
