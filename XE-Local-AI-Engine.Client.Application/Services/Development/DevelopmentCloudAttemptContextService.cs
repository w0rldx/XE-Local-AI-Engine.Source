namespace XE_Local_AI_Engine.Client.Services.Development;

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class DevelopmentCloudAttemptContext
{
    public required DevelopmentCloudRoleRoute Route { get; init; }

    public required Guid ArtifactId { get; init; }
}

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
internal sealed class DevelopmentCloudAttemptContextService : IDevelopmentCloudAttemptContextService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IDevelopmentArtifactBlobStore _blobStore;
    private readonly IDevelopmentCloudContextBuilder _contextBuilder;
    private readonly DevelopmentOptions _options;
    private readonly DevelopmentCloudRoleRouteFactory _routeFactory;
    private readonly IDevelopmentStore _store;
    private readonly TimeProvider _timeProvider;

    public DevelopmentCloudAttemptContextService(
        IDevelopmentCloudContextBuilder contextBuilder,
        DevelopmentCloudRoleRouteFactory routeFactory,
        IDevelopmentArtifactBlobStore blobStore,
        IDevelopmentStore store,
        IOptions<DevelopmentOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(blobStore);
        _blobStore = blobStore;
        ArgumentNullException.ThrowIfNull(contextBuilder);
        _contextBuilder = contextBuilder;
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        ArgumentNullException.ThrowIfNull(routeFactory);
        _routeFactory = routeFactory;
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

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
        var bundle = _contextBuilder.Build(new DevelopmentCloudContextBuildRequest
        {
            BundleId = $"development-{snapshot.AttemptId:N}-{Guid.NewGuid():N}",
            ProjectId = snapshot.ProjectId.ToString("D"),
            TaskId = snapshot.TaskId.ToString("D"),
            AttemptId = snapshot.AttemptId.ToString("D"),
            ProviderName = snapshot.Provider,
            ModelId = snapshot.ModelId,
            Requirements = snapshot.Requirements,
            AcceptanceCriteria = snapshot.AcceptanceCriteriaJson,
            PolicyText = Policy(snapshot.WorkflowPolicyText),
            Excerpts = excerpts,
            ExpiresAt = _timeProvider.GetUtcNow().AddSeconds(durationSeconds + 60L),
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
        });

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
        var written = await _blobStore.WriteAsync(snapshot.ProjectId, artifactId, content, cancellationToken);
        _ = await _store.AttachArtifactAsync(new DevelopmentAttachArtifactCommand
        {
            ArtifactId = artifactId,
            ProjectId = snapshot.ProjectId,
            TaskId = snapshot.TaskId,
            AttemptId = snapshot.AttemptId,
            OperationId = Guid.NewGuid(),
            Kind = DevelopmentArtifactKind.CloudContextBundle,
            SchemaVersion = 1,
            ContentHash = written.ContentHash,
            ByteCount = written.ByteCount,
            ManagedReference = written.OpaqueReference,
            InputArtifactIdsJson = inputArtifactIds is null
                                    ? null
                                    : JsonSerializer.SerializeToUtf8Bytes(inputArtifactIds, JsonOptions)
        },
                            cancellationToken);

        return new DevelopmentCloudAttemptContext { Route = _routeFactory.Create(bundle), ArtifactId = artifactId };
    }

    /// <summary>
    ///     What the bundle's <c>policy</c> resource says: the CloudScoped authorization sentence, plus the rule-set
    ///     text a Development workflow snapshotted onto this task when there is one.
    ///     <para>
    ///         Load-bearing for BOTH roles. A cloud-routed coder or reviewer is sent only this bundle — its prompt's
    ///         local <c>Policy</c> section never reaches the provider — so a workflow policy left out here is a policy
    ///         the model never sees while the task's <c>WorkflowPolicyApplied</c> event and <c>appliedRuleSets</c>
    ///         claim the attempt was governed by it.
    ///     </para>
    ///     <para>
    ///         Composed BEFORE the builder, so the sanitizer, the byte and token caps, and the content hash the egress
    ///         authorizer binds all cover it. That is also what makes it fail CLOSED: a policy the sanitizer refuses or
    ///         one that overruns the bundle's caps throws here and terminalizes the attempt, rather than being dropped
    ///         from a payload that still claims to carry it.
    ///     </para>
    /// </summary>
    private static string Policy(string? workflowPolicy) =>
        string.IsNullOrWhiteSpace(workflowPolicy)
            ? AuthorizationPolicy
            : string.Concat(AuthorizationPolicy, "\n\nRule sets applied by the workflow:\n", workflowPolicy);

    /// <summary>What CloudScoped execution itself authorizes, on every bundle whether a workflow drives it or not.</summary>
    private const string AuthorizationPolicy =
        "The operator selected CloudScoped Development execution. Use only this immutable bundle and the typed role submission tool; no general repository, chat-history, saved-agent, or shell capability is authorized.";
}
