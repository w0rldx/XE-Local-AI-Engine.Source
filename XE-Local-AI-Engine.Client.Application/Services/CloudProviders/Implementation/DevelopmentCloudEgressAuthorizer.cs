namespace XE_Local_AI_Engine.Client.Services.CloudProviders.Implementation;

using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>
///     Performs content-free, exact-match authorization immediately before each selected-cloud transport round.
/// </summary>
public sealed class DevelopmentCloudEgressAuthorizer(
    IDevelopmentCloudContextCatalog contextCatalog,
    IDevelopmentCloudEgressAuditSink auditSink,
    TimeProvider timeProvider,
    int maximumBundleBytes = DevelopmentCloudContextBuilder.DefaultMaximumBytes) : ICloudEgressAuthorizer
{
    private readonly IDevelopmentCloudEgressAuditSink _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    private readonly IDevelopmentCloudContextCatalog _contextCatalog = contextCatalog ?? throw new ArgumentNullException(nameof(contextCatalog));
    private readonly int _maximumBundleBytes = ValidateMaximumBundleBytes(maximumBundleBytes);
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public void Authorize(CloudEgressAuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Reject(request.CarrierState != CloudEgressAuthorizationCarrierState.Valid, "The Development cloud authorization carrier is invalid.");
        var envelope = request.Envelope ?? throw new CloudEgressAuthorizationException("The Development cloud authorization envelope is missing.");
        Reject(envelope.Version != DevelopmentCloudAuthorizationEnvelope.CurrentVersion,
            "The Development cloud authorization version is unsupported.");
        Reject(envelope.Policy != DevelopmentExecutionPolicy.CloudScoped,
            "The Development execution policy does not permit cloud egress.");
        Reject(string.IsNullOrWhiteSpace(envelope.ProjectId)
               || string.IsNullOrWhiteSpace(envelope.TaskId)
               || string.IsNullOrWhiteSpace(envelope.AttemptId)
               || string.IsNullOrWhiteSpace(envelope.AuthorizedBundleId)
               || string.IsNullOrWhiteSpace(envelope.AuthorizedBundleHash)
               || string.IsNullOrWhiteSpace(envelope.Nonce),
            "The Development cloud authorization envelope is incomplete.");
        Reject(string.IsNullOrWhiteSpace(request.ProviderName) || string.IsNullOrWhiteSpace(request.ModelId),
            "The selected cloud provider and model must be explicit.");

        var now = _timeProvider.GetUtcNow();
        Reject(envelope.ExpiresAt <= now, "The Development cloud authorization envelope has expired.");
        Reject(!_contextCatalog.TryGet(envelope.AuthorizedBundleId!, out var bundle) || bundle is null,
            "The approved Development cloud context bundle is unavailable.");
        Reject(!bundle!.SecretScanPassed || bundle.ByteCount <= 0 || bundle.ByteCount > _maximumBundleBytes,
            "The approved Development cloud context bundle is invalid or exceeds the authorized byte limit.");
        Reject(bundle.ExpiresAt <= now || bundle.ExpiresAt != envelope.ExpiresAt,
            "The approved Development cloud context expiry does not match the authorization envelope.");
        Reject(!string.Equals(bundle.ProjectId, envelope.ProjectId, StringComparison.Ordinal)
               || !string.Equals(bundle.TaskId, envelope.TaskId, StringComparison.Ordinal)
               || !string.Equals(bundle.AttemptId, envelope.AttemptId, StringComparison.Ordinal),
            "The approved Development cloud context ownership does not match the authorization envelope.");
        Reject(!string.Equals(bundle.ContentHash, envelope.AuthorizedBundleHash, StringComparison.Ordinal),
            "The approved Development cloud context hash does not match the authorization envelope.");
        Reject(!string.Equals(bundle.Nonce, envelope.Nonce, StringComparison.Ordinal),
            "The approved Development cloud context nonce does not match the authorization envelope.");
        Reject(!string.Equals(bundle.ProviderName, request.ProviderName, StringComparison.Ordinal)
               || !string.Equals(bundle.ModelId, request.ModelId, StringComparison.Ordinal),
            "The selected cloud provider or model does not match the approved Development cloud context.");

        _auditSink.Record(new DevelopmentCloudEgressAudit(envelope.ProjectId,
            envelope.TaskId,
            envelope.AttemptId,
            request.ProviderName,
            request.ModelId!,
            envelope.AuthorizedBundleId!,
            envelope.AuthorizedBundleHash!,
            now));
    }

    private static void Reject(bool condition, string reason)
    {
        if (condition)
        {
            throw new CloudEgressAuthorizationException(reason);
        }
    }

    private static int ValidateMaximumBundleBytes(int maximumBundleBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBundleBytes);
        return maximumBundleBytes;
    }
}
