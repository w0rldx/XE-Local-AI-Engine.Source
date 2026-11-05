namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>Default lock-free in-memory update state.</summary>
public sealed class AppUpdateState : IAppUpdateState
{
    private AppUpdateSnapshot _current = AppUpdateSnapshot.Empty;

    public AppUpdateSnapshot Current => Volatile.Read(ref _current);

    public void Store(AppUpdateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
