namespace XE_Local_AI_Engine.Client.Services.PreviewWorkflows;

/// <summary>
///     Structured result of validating a <see cref="PreviewWorkflowGraph" />. <see cref="IsValid" /> is true only when
///     <see cref="Errors" /> is empty. Endpoints surface the errors as a 400; the execution service refuses to start an
///     invalid graph.
/// </summary>
public sealed record PreviewWorkflowValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public static PreviewWorkflowValidationResult Valid { get; } = new([]);

    public static PreviewWorkflowValidationResult Invalid(IReadOnlyList<string> errors)
    {
        return new PreviewWorkflowValidationResult(errors);
    }
}

/// <summary>
///     Thrown by the execution service when a graph fails validation at execute time (the endpoint layer validates
///     first and returns a 400, so this is the defense-in-depth path for direct service callers). Carries the
///     structured errors.
/// </summary>
public sealed class PreviewWorkflowValidationException(PreviewWorkflowValidationResult result)
    : Exception(BuildMessage(result))
{
    public PreviewWorkflowValidationResult Result { get; } = result ?? throw new ArgumentNullException(nameof(result));

    private static string BuildMessage(PreviewWorkflowValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors.Count == 0
            ? "The preview workflow graph is invalid."
            : $"The preview workflow graph is invalid: {string.Join("; ", result.Errors)}";
    }
}
