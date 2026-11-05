namespace XE_Local_AI_Engine.Client.Services.Compute;

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
