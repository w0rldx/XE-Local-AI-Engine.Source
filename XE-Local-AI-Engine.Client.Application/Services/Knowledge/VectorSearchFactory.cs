namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IVectorSearchFactory" />. Scoped: it resolves the concrete <see cref="IVectorSearch" /> from the
///     scoped <see cref="IServiceProvider" /> so the returned implementation shares the request scope's
///     <c>DbContext</c>/connection (M3 — no captive dependency). Only the managed cosine backend is registered today; when
///     a gated ANN backend lands, the selection switch (driven by configuration) belongs here.
/// </summary>
public sealed class VectorSearchFactory : IVectorSearchFactory
{
    private readonly IServiceProvider _serviceProvider;

    public VectorSearchFactory(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
    }

    public IVectorSearch Create()
    {
        // Single backend today; the future config-driven backend switch resolves the alternate implementation here.
        return _serviceProvider.GetRequiredService<IVectorSearch>();
    }
}
