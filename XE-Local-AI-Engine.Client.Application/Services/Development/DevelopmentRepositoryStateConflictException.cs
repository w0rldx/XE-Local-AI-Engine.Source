namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Thrown when a well-formed Development request is blocked by the persisted state of the project's repository
///     binding or trust acknowledgement, and the operator has to reconnect, re-acknowledge or re-register first.
/// </summary>
/// <remarks>
///     The bound folder is no longer usable, the stored repository identity no longer matches, or the
///     trusted-repository acknowledgement is no longer current; nothing in the request is wrong. It derives from
///     <see cref="DevelopmentWorkspaceSecurityException" /> so every service-layer catch of that keeps treating it as
///     one rejection, mirroring <see cref="XE_Local_AI_Engine.Client.Services.Workspace.SelectedFolderConflictException" />.
///     Endpoints that map the base type to 400 catch this one first and map it to 409.
/// </remarks>
public sealed class DevelopmentRepositoryStateConflictException : DevelopmentWorkspaceSecurityException
{
    public DevelopmentRepositoryStateConflictException(string message) : base(message)
    {
    }
}
