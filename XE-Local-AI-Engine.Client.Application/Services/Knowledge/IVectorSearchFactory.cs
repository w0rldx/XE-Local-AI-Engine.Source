namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Resolves the active <see cref="IVectorSearch" /> implementation within the current request scope. This is the
///     backend-selection seam (M3): today only the managed cosine backend ships, but a future ANN backend (for example a
///     <c>sqlite-vec</c> KNN impl) can be selected here from configuration without any caller change. The factory MUST
///     resolve the implementation from the scoped provider so a scoped <c>DbContext</c> is never captured by a singleton.
/// </summary>
public interface IVectorSearchFactory
{
    /// <summary>Returns the vector-search implementation to use for the current request scope.</summary>
    IVectorSearch Create();
}
