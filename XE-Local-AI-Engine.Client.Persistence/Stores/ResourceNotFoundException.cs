namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The base of every domain not-found exception: an Application-service command or read named an id the client
///     supplied, and nothing by that id exists.
/// </summary>
/// <remarks>
///     <c>ResourceNotFoundExceptionHandler</c> answers every subtype with the surface's bodyless 404, so endpoints do
///     not branch on a null lookup themselves (docs/wiki/16-code-conventions.md, rule 4 of "Absence"). Declared
///     here, the lowest layer that both the stores and the Application services reference. Derives from <see cref="KeyNotFoundException" /> so the degradation paths that filter on "the thing is not
///     there" keep matching; the handler names this type, so a bare <see cref="KeyNotFoundException" /> still reaches
///     the default 500.
/// </remarks>
public abstract class ResourceNotFoundException : KeyNotFoundException
{
    protected ResourceNotFoundException(string message) : base(message)
    {
    }
}
