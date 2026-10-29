namespace XE_Local_AI_Engine.Client.Services.Integrations;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What a cancel decided. Each value maps to exactly one status at both the admin and the external route.</summary>
public enum IntegrationCancelOutcome
{
    /// <summary>The stop marker is durable and the run was signaled. 202.</summary>
    Requested,

    /// <summary>No row with that id. 404.</summary>
    NotFound,

    /// <summary>The run had already finished. 409.</summary>
    AlreadyTerminal
}

/// <summary>
///     Thrown when an operator command or read names an integration trigger, execution, session or API key that does
///     not exist. <c>ResourceNotFoundExceptionHandler</c> answers it with the bodyless 404.
/// </summary>
/// <remarks>
///     Operator surface only: the external integration routes keep their own masked 404 bodies through
///     <see cref="IntegrationCancelOutcome" /> and <see cref="IntegrationAcceptOutcome" />, so nothing there throws it.
/// </remarks>
public sealed class IntegrationNotFoundException : ResourceNotFoundException
{
    public IntegrationNotFoundException(string message) : base(message)
    {
    }
}
