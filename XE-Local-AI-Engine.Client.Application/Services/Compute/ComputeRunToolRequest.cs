namespace XE_Local_AI_Engine.Client.Services.Compute;

/// <summary>
///     Typed projection of the <c>run_python</c> JSON arguments. The tool bridge stays JSON-in / JSON-out, so the
///     handler deserializes into this record and validates it before anything is executed — the schema advertised to the
///     model is advisory; this validation is authoritative.
/// </summary>
internal sealed record ComputeRunToolRequest
{
    public string? Code { get; init; }
}

/// <summary>Validates a <see cref="ComputeRunToolRequest" /> against the compute tool constraints.</summary>
internal static class ComputeRunToolRequestValidator
{
    public static IReadOnlyList<string> Validate(ComputeRunToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Code))
        {
            errors.Add("'code' is required and must be a non-empty string.");
        }
        else if (request.Code.Length > ComputeToolDefinition.CodeMaxLength)
        {
            errors.Add($"'code' must be at most {ComputeToolDefinition.CodeMaxLength} characters.");
        }

        return errors;
    }
}
