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
            Policy(snapshot.WorkflowPolicyText),
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
