namespace XE_Local_AI_Engine.Client.Services.AppUpdate;

/// <summary>Thread-safe holder for the latest <see cref="AppUpdateSnapshot" />.</summary>
public interface IAppUpdateState
{
    AppUpdateSnapshot Current { get; }

    void Store(AppUpdateSnapshot snapshot);
}
