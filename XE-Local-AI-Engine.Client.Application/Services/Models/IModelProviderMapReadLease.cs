namespace XE_Local_AI_Engine.Client.Services.Models;

public interface IModelProviderMapReadLease : IAsyncDisposable
{
    IReadOnlyList<string> ModelKeys { get; }
    IReadOnlyList<string> MapKeys { get; }
    bool IsDisposed { get; }
    bool IsMutation { get; }
    bool ContainsModel(string modelName);
}
