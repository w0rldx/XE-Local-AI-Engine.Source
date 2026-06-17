namespace XE_Local_AI_Engine.Client.Persistence;

/// <summary>
///     Worker-side projection of a persisted <c>ModelProviderMap</c> row: the model name and the provider key that
///     serves it. Read by the model-routing client and the preview/embeddings resolvers to dispatch a model to the
///     right local runtime.
/// </summary>
public sealed record ModelProviderMapRecord(string ModelName, string ProviderName, long UpdatedAtUtc);
