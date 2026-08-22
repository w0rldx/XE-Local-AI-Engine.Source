namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Resolves the active <see cref="IVectorSearch" /> implementation within the current request scope. This is the
///     backend-selection seam, keeping callers independent of the concrete implementation. Only the managed cosine
///     backend is registered today. The factory MUST
///     resolve the implementation from the scoped provider so a scoped <c>DbContext</c> is never captured by a singleton.
/// </summary>
public interface IVectorSearchFactory
{
    /// <summary>Returns the vector-search implementation to use for the current request scope.</summary>
    IVectorSearch Create();
}
