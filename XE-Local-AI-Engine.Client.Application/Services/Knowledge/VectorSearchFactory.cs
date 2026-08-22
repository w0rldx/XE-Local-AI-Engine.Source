namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IVectorSearchFactory" />. Scoped: it resolves the concrete <see cref="IVectorSearch" /> from the
///     scoped <see cref="IServiceProvider" /> so the returned implementation shares the request scope's
///     <c>DbContext</c>/connection without creating a captive dependency. Only the managed cosine backend is
///     registered.
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
        // The managed cosine backend is the only registered implementation.
        return _serviceProvider.GetRequiredService<IVectorSearch>();
    }
}
