namespace XE_Local_AI_Engine.Tests.Testing;

using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Providers.Abstractions.External;

/// <summary>
///     An <see cref="IModelTrustResolver" /> over an in-memory set of registrations, with the production fail-closed
///     answers built in: a non-external id is node-local, and an <c>ext:</c> id that was not registered here resolves
///     <see cref="ModelTrustLocality.Unresolved" />.
/// </summary>
/// <remarks>
///     Its default (empty) shape is what the many tests that predate external providers want: they drive non-external
///     ids, so every answer is <see cref="ModelTrustLocality.Local" /> and the seams under test behave exactly as they
///     did before. Tests that care about external trust register the connection they mean.
/// </remarks>
internal sealed class FakeModelTrustResolver : IModelTrustResolver
{
    private readonly Dictionary<string, ExternalProviderModelRegistration> _registrations = new(StringComparer.Ordinal);

    /// <summary>Set to make <see cref="ClassifyExternalCached" /> behave as it does before the registry is primed.</summary>
    public bool CacheIsCold { get; set; }

    public FakeModelTrustResolver Register(string connectionId,
        string wireId,
        ExternalProviderLocality locality = ExternalProviderLocality.Local,
        int? contextLength = null,
        bool supportsTools = false,
        bool supportsVision = false,
        bool supportsReasoning = false,
        bool supportsReasoningEffort = false,
        string? defaultReasoningEffort = null)
    {
        var registration = new ExternalProviderModelRegistration(new ExternalProviderConnectionDescriptor
            {
                Id = connectionId,
                DisplayName = connectionId,
                BaseUrl = new Uri("http://127.0.0.1:18099/v1/", UriKind.Absolute),
                Locality = locality
            },
            new ExternalProviderModelDescriptor
            {
                WireId = wireId,
                ContextLength = contextLength,
                SupportsTools = supportsTools,
                SupportsVision = supportsVision,
                SupportsReasoning = supportsReasoning,
                SupportsReasoningEffort = supportsReasoningEffort,
                DefaultReasoningEffort = defaultReasoningEffort
            });

        _registrations[registration.ModelId] = registration;
        return this;
    }

    public Task<ModelTrustLocality> ResolveAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ExternalModelId.HasExternalScheme(modelId) ? ClassifyRegistered(modelId) : ModelTrustLocality.Local);
    }

    public Task<ExternalProviderModelRegistration?> TryResolveExternalAsync(string? modelId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(ExternalModelId.HasExternalScheme(modelId) ? Lookup(modelId) : null);
    }

    public ModelTrustLocality? ClassifyExternalCached(string? modelId)
    {
        if (!ExternalModelId.HasExternalScheme(modelId))
        {
            return null;
        }

        return CacheIsCold ? ModelTrustLocality.Unresolved : ClassifyRegistered(modelId);
    }

    private ModelTrustLocality ClassifyRegistered(string? modelId)
    {
        if (Lookup(modelId) is not { } registration)
        {
            return ModelTrustLocality.Unresolved;
        }

        return registration.Connection.Locality == ExternalProviderLocality.Local ? ModelTrustLocality.Local : ModelTrustLocality.Cloud;
    }

    private ExternalProviderModelRegistration? Lookup(string? modelId)
    {
        return ExternalModelId.Canonicalize(modelId) is { } canonical ? _registrations.GetValueOrDefault(canonical) : null;
    }
}
