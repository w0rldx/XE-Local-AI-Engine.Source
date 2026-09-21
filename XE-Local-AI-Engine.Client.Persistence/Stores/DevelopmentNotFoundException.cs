namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Identifies a missing Development resource — project, task, attempt, artifact or template — at the persistence or
///     service boundary.
/// </summary>
/// <remarks>
///     <c>DevelopmentNotFoundExceptionHandler</c> can then answer the surface's bodyless 404 in one place instead of
///     every Development endpoint catching the bare CLR type. Mirrors <see cref="WorkSessionNotFoundException" />, and
///     derives from <see cref="KeyNotFoundException" /> so the service-layer degradation paths that already filter on
///     it — <c>DevelopmentProfileBackfillService.DetectProfileJsonAsync</c> in this area — keep treating it as the
///     same "the thing is not there" signal.
/// </remarks>
public sealed class DevelopmentNotFoundException : KeyNotFoundException
{
    public DevelopmentNotFoundException(string message) : base(message)
    {
    }
}
