namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Services.ModelFit;

internal static class GgufImportEndpointSupport
{
    public static GgufAcquisitionStatusResponse Map(GgufAcquisitionStatus status) => new()
    {
        OperationId = status.OperationId,
        OperationKind = status.OperationKind.ToString(),
        ModelName = status.ModelName,
        Phase = status.Phase.ToString(),
        CompletedBytes = status.CompletedBytes,
        TotalBytes = status.TotalBytes,
        StartedAtUtc = status.StartedAtUtc,
        UpdatedAtUtc = status.UpdatedAtUtc,
        ErrorCode = status.ErrorCode,
        SanitizedMessage = status.SanitizedError
    };

    public static IResult Error(GgufImportApplicationException exception)
    {
        var statusCode = exception.ErrorCode switch
        {
            "ModelConflict" or "DestinationConflict" or "AcquisitionAlreadyActive" => StatusCodes.Status409Conflict,
            "OperationNotFound" => StatusCodes.Status404NotFound,
            "InsufficientStorage" => StatusCodes.Status507InsufficientStorage,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new GgufImportErrorResponse
        {
            ErrorCode = exception.ErrorCode,
            Message = exception.Message
        }, statusCode: statusCode);
    }
}
