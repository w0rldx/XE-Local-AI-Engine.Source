namespace XE_Local_AI_Engine.Client.Common.ProblemDetailModels;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
///     The one 409 envelope <c>ConflictExceptionHandler</c> writes.
/// </summary>
/// <remarks>
///     <see cref="ConflictType" /> is the discriminator the SPA switches on; the payload members beside it are typed
///     here rather than left as untyped <see cref="ProblemDetails.Extensions" />, so the OpenAPI schema names them — a
///     schema-validating client can otherwise neither type the discriminator nor accept the extra members. Null members
///     are omitted on the wire. Declare it with <c>ProducesConflictProblemDetails()</c>, never with FastEndpoints'
///     <c>ProducesProblemDetails(409)</c>, whose schema is <c>additionalProperties: false</c> and has none of these.
/// </remarks>
public class ConflictProblemDetails : ProblemDetails
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(0)]
    [JsonPropertyName("conflictType")]
    public string? ConflictType { get; set; }

    /// <summary>
    ///     Set for <c>DevWorkflowGateAlreadyDecided</c>: the <c>DevWorkflowDecisionKind</c> name that already stands on
    ///     the node run. Without it the refused second click can only say "conflict", when the useful answer is
    ///     "someone already approved this".
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("standingDecision")]
    public string? StandingDecision { get; set; }

    /// <summary>
    ///     Set for <c>ExternalAppPermissionChangeRequiresAcknowledgement</c>: the permission names the new manifest
    ///     adds, from the fixed vocabulary <c>ExternalAppEffectivePermissions.Diff</c> produces.
    /// </summary>
    /// <remarks>
    ///     The SPA renders them in the acknowledgement dialog; the server owns the diff, so this is never a list the
    ///     client computes.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("addedPermissions")]
    public IReadOnlyList<string>? AddedPermissions { get; set; }
}
