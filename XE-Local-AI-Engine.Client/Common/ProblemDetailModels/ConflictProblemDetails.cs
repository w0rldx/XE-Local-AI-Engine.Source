namespace XE_Local_AI_Engine.Client.Common.ProblemDetailModels;

using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

/// <summary>
///     Represents conflict problem details.
/// </summary>
public class ConflictProblemDetails : ProblemDetails
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyOrder(0)]
    [JsonPropertyName("conflictType")]
    public string? ConflictType { get; set; }
}
