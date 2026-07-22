namespace XE_Local_AI_Engine.Client.Services.Development;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class DevelopmentTrustPolicy
{
    public const int CurrentVersion = 1;

    public static void EnsureCurrent(DevelopmentExecutionSnapshot snapshot, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!snapshot.TrustedRepositoryAcknowledged
            || snapshot.TrustedRepositoryPolicyVersion != CurrentVersion
            || snapshot.TrustedRepositoryAcknowledgedAtUtc is not { } acknowledgedAt
            || acknowledgedAt <= 0
            || acknowledgedAt > timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            throw new DevelopmentWorkspaceSecurityException("Process execution requires a current persisted trusted-repository acknowledgement.");
        }
    }

    public static void EnsureCurrent(DevelopmentProjectSnapshot snapshot, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!snapshot.TrustedRepositoryAcknowledged
            || snapshot.TrustedRepositoryPolicyVersion != CurrentVersion
            || snapshot.TrustedRepositoryAcknowledgedAtUtc is not { } acknowledgedAt
            || acknowledgedAt <= 0
            || acknowledgedAt > timeProvider.GetUtcNow().ToUnixTimeMilliseconds())
        {
            throw new DevelopmentWorkspaceSecurityException("Process execution requires a current persisted trusted-repository acknowledgement.");
        }
    }
}
