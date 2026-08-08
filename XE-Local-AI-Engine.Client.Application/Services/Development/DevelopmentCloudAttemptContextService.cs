namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed record DevelopmentCloudAttemptContext(
    DevelopmentCloudRoleRoute Route,
    Guid ArtifactId);

internal interface IDevelopmentCloudAttemptContextService
{
    Task<DevelopmentCloudAttemptContext> CreateAsync(DevelopmentExecutionSnapshot snapshot,
        IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts,
        IReadOnlyList<Guid>? inputArtifactIds = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
///     Builds, durably records, and projects the one immutable cloud context authorized for an attempt.
/// </summary>
internal sealed class DevelopmentCloudAttemptContextService(
    IDevelopmentCloudContextBuilder contextBuilder,
    DevelopmentCloudRoleRouteFactory routeFactory,
    IDevelopmentArtifactBlobStore blobStore,
    IDevelopmentStore store,
    IOptions<DevelopmentOptions> options,
    TimeProvider timeProvider) : IDevelopmentCloudAttemptContextService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentArtifactBlobStore _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    private readonly IDevelopmentCloudContextBuilder _contextBuilder = contextBuilder ?? throw new ArgumentNullException(nameof(contextBuilder));
    private readonly DevelopmentOptions _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    private readonly DevelopmentCloudRoleRouteFactory _routeFactory = routeFactory ?? throw new ArgumentNullException(nameof(routeFactory));
    private readonly IDevelopmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<DevelopmentCloudAttemptContext> CreateAsync(DevelopmentExecutionSnapshot snapshot,
        IReadOnlyList<DevelopmentCloudContextExcerpt> excerpts,
        IReadOnlyList<Guid>? inputArtifactIds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(excerpts);
        if (snapshot.EgressPolicy != DevelopmentEgressPolicy.CloudScoped
            || string.IsNullOrWhiteSpace(snapshot.Provider)
            || string.Equals(snapshot.Provider, "local", StringComparison.OrdinalIgnoreCase))
        {
            throw new DevelopmentWorkspaceSecurityException("A cloud Development context can be created only for a CloudScoped cloud attempt.");
        }

        var durationSeconds = Math.Min(snapshot.MaxDurationSeconds ?? _options.MaxAttemptDurationSeconds,
            _options.MaxAttemptDurationSeconds);
        var bundle = _contextBuilder.Build(new DevelopmentCloudContextBuildRequest($"development-{snapshot.AttemptId:N}-{Guid.NewGuid():N}",
            snapshot.ProjectId.ToString("D"),
            snapshot.TaskId.ToString("D"),
            snapshot.AttemptId.ToString("D"),
            snapshot.Provider,
            snapshot.ModelId,
            snapshot.Requirements,
            snapshot.AcceptanceCriteriaJson,
            "The operator selected CloudScoped Development execution. Use only this immutable bundle and the typed role submission tool; no general repository, chat-history, saved-agent, or shell capability is authorized.",
            excerpts,
            _timeProvider.GetUtcNow().AddSeconds(durationSeconds + 60L),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

        var content = JsonSerializer.SerializeToUtf8Bytes(new
        {
            bundle.Id,
            bundle.ProjectId,
            bundle.TaskId,
            bundle.AttemptId,
            bundle.ProviderName,
            bundle.ModelId,
            bundle.Requirements,
            bundle.AcceptanceCriteria,
            bundle.PolicyText,
            bundle.Excerpts,
            bundle.ContentHash,
            bundle.ByteCount,
            bundle.EstimatedTokenCount,
            bundle.ExpiresAt,
            bundle.Nonce,
            bundle.SecretScanPassed
        }, JsonOptions);
        var artifactId = Guid.NewGuid();
        var written = await _blobStore.WriteAsync(snapshot.ProjectId, artifactId, content, cancellationToken).ConfigureAwait(false);
        _ = await _store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand(artifactId,
                                snapshot.ProjectId,
                                snapshot.TaskId,
                                snapshot.AttemptId,
                                Guid.NewGuid(),
                                DevelopmentArtifactKind.CloudContextBundle,
                                SchemaVersion: 1,
                                written.ContentHash,
                                written.ByteCount,
                                ManagedReference: written.OpaqueReference,
                                InputArtifactIdsJson: inputArtifactIds is null
                                    ? null
                                    : JsonSerializer.SerializeToUtf8Bytes(inputArtifactIds, JsonOptions)),
                            cancellationToken)
                        .ConfigureAwait(false);

        return new DevelopmentCloudAttemptContext(_routeFactory.Create(bundle), artifactId);
    }
}
