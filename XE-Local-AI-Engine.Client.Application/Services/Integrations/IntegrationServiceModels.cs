namespace XE_Local_AI_Engine.Client.Services.Integrations;

/// <summary>What a cancel decided. Each value maps to exactly one status at both the admin and the external route.</summary>
public enum IntegrationCancelOutcome
{
    /// <summary>The stop marker is durable and the run was signalled. 202.</summary>
    Requested,

    /// <summary>No row with that id. 404.</summary>
    NotFound,

    /// <summary>The run had already finished. 409.</summary>
    AlreadyTerminal
}
