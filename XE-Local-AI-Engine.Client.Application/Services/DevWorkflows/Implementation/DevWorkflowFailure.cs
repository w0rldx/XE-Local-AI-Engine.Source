namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>What a lane came back with, in the four terms the retry decision is made on.</summary>
internal sealed class DevWorkflowFailure
{
    /// <summary>The closed failure-class token that says why.</summary>
    public required string FailureClass { get; init; }

    /// <summary>What an operator is shown. Already sanitized by whoever produced it.</summary>
    public required string SanitizedReason { get; init; }

    /// <summary>The node's output document, which a routed retry hands to the node it re-runs.</summary>
    public required string OutputJson { get; init; }

    /// <summary>The event outcome, for the two cases the status alone cannot express.</summary>
    public string? Outcome { get; init; }
}
