namespace XE_Local_AI_Engine.Client.Endpoints.Python.V1;

using XE_Local_AI_Engine.Client.Services.ManagedPython;

/// <summary>
///     Builds the 409 <see cref="ManagedPythonBlockedResponse" /> both Compute environment actions return when they are refused.
/// </summary>
/// <remarks>
///     A typed value rather than the shared conflict envelope for the reason <c>TrainingRuntimeBlockedEndpointSupport</c>
///     gives: the refusal is an outcome the service returns, not an exception a handler could write (ADR 0009).
/// </remarks>
internal static class ManagedPythonBlockedEndpointSupport
{
    /// <summary>The 409 for a refused outcome, or <see langword="null" /> when the action went ahead.</summary>
    internal static IResult? BlockedOrNull(ManagedPythonActionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var reason = result.Outcome switch
        {
            ManagedPythonActionOutcome.Unsupported => "unsupported",
            ManagedPythonActionOutcome.Busy => "busy",
            ManagedPythonActionOutcome.Failed => "failed",
            _ => null
        };

        return reason is null
            ? null
            : Results.Conflict(new ManagedPythonBlockedResponse
            {
                Reason = reason,
                Message = result.Message ?? "The action was refused."
            });
    }
}
