namespace XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Thrown when a well-formed Development request is blocked by the <em>persisted</em> state of the project's
///     repository binding or trust acknowledgement — the folder the project was bound to is no longer usable, the
///     stored repository identity no longer matches, or the trusted-repository acknowledgement is no longer current.
///     Nothing in the request is wrong; the operator has to reconnect, re-acknowledge or re-register first.
///     <para>
///         Derives from <see cref="DevelopmentWorkspaceSecurityException" /> so every existing service-layer
///         "workspace security rejected this" catch (availability probing in
///         <c>DevelopmentRepositoryBindingService.ListAsync</c>, the profile backfill/summary degradation paths, the
///         attempt runners' failure-reason mapping) keeps treating it as one rejection — exactly like
///         <see cref="XE_Local_AI_Engine.Client.Services.Workspace.SelectedFolderConflictException" /> derives from
///         <see cref="XE_Local_AI_Engine.Client.Services.Workspace.SelectedFolderValidationException" />. Endpoints that
///         map the base type to 400 catch this one FIRST and map it to 409.
///     </para>
/// </summary>
public sealed class DevelopmentRepositoryStateConflictException : DevelopmentWorkspaceSecurityException
{
    public DevelopmentRepositoryStateConflictException(string message) : base(message)
    {
    }
}
