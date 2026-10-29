namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Identifies a missing WorkSession resource at the persistence or service boundary.
/// </summary>
public sealed class WorkSessionNotFoundException : ResourceNotFoundException
{
    public WorkSessionNotFoundException(string message) : base(message)
    {
    }
}
