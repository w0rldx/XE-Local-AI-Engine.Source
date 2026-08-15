namespace XE_Local_AI_Engine.Client.Common.ProblemDetailModels;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
///     The one 409 envelope <c>ConflictExceptionHandler</c> writes. <see cref="ConflictType" /> is the discriminator the
///     SPA switches on; the cap numbers are typed here (rather than left as untyped
///     <see cref="ProblemDetails.Extensions" />) so the OpenAPI schema names them — a schema-validating client can
///     otherwise neither type the discriminator nor accept the extra members. Null members are omitted on the wire, so
///     the body is unchanged from when they rode as extensions. Declare it on an endpoint with
///     <c>ProducesConflictProblemDetails()</c>, never with FastEndpoints' <c>ProducesProblemDetails(409)</c>, whose
///     schema is <c>additionalProperties: false</c> and has none of these members.
/// </summary>
public class ConflictProblemDetails : ProblemDetails
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(0)]
    [JsonPropertyName("conflictType")]
    public string? ConflictType { get; set; }

    /// <summary>Set for <c>PreviewWorkflowCapReached</c>: the concurrent-run cap that was hit.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("maxConcurrentRuns")]
    public int? MaxConcurrentRuns { get; set; }

    /// <summary>Set for <c>PreviewWorkflowModelCapExceeded</c>: how many distinct models the workflow needs.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("distinctModelCount")]
    public int? DistinctModelCount { get; set; }

    /// <summary>Set for <c>PreviewWorkflowModelCapExceeded</c>: the loaded-process cap those models exceed.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("maxLoadedProcesses")]
    public int? MaxLoadedProcesses { get; set; }
}
