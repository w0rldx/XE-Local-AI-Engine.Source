namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using FluentValidation.Results;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Replays a runtime validation refusal onto an endpoint's failure list. Both write routes catch the same
///     exception for the same reason — the global single-message handler could only report ONE of the complaints, and
///     an author fixing a canvas wants every one of them at once — so the replay itself lives here rather than twice.
/// </summary>
internal static class GraphWorkflowValidationErrors
{
    /// <summary>
    ///     Adds every error in <paramref name="result" /> to <paramref name="errors" />. The node or edge key becomes
    ///     the failure's property name, which is what lets the editor draw the complaint on the offending element. A
    ///     whole-document failure has no element, so it goes to FastEndpoints' own general-errors field rather than to
    ///     a key nothing on the canvas answers to.
    /// </summary>
    public static void AddTo(IValidationErrors errors, GraphWorkflowValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(result);

        foreach (var error in result.Errors)
        {
            if (error.Key is { } key)
            {
                errors.AddError(new ValidationFailure(key, error.Message));
            }
            else
            {
                errors.AddError(error.Message);
            }
        }
    }
}
