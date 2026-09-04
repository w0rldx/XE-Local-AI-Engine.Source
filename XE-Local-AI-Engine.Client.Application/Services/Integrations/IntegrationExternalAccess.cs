namespace XE_Local_AI_Engine.Client.Services.Integrations;

using System.Security.Claims;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

/// <summary>
///     The authenticated integrator behind one external request. <see cref="PrincipalId" /> is the identity every
///     ownership question keys on; <see cref="KeyPrefix" /> names WHICH of that integrator's credentials made the call,
///     which is what lets the allowlist on that one credential still bind after the invocation.
/// </summary>
public sealed record IntegrationCallerIdentity(Guid PrincipalId, string KeyPrefix)
{
    /// <summary>
    ///     Reads the identity off an authenticated principal, failing CLOSED on a missing or duplicated claim rather
    ///     than taking the first of several. A duplicated claim is not a shape this node's own handler can produce, so
    ///     seeing one means something else built the identity.
    /// </summary>
    public static IntegrationCallerIdentity? FromPrincipal(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var principals = principal.FindAll(NodeAuthorizationPolicies.IntegrationPrincipalClaimType).Select(static claim => claim.Value).ToArray();
        var prefixes = principal.FindAll(NodeAuthorizationPolicies.IntegrationKeyPrefixClaimType).Select(static claim => claim.Value).ToArray();
        if (principals.Length != 1 || prefixes.Length != 1 || !Guid.TryParse(principals[0], out var principalId))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(prefixes[0]) ? null : new IntegrationCallerIdentity(principalId, prefixes[0]);
    }
}

/// <summary>
///     What an access check decided. <see cref="Masked" /> is ALWAYS the same 404 at the route — there is no third
///     value, because every distinguishable outcome would confirm the existence of a row the caller may not see.
/// </summary>
public enum IntegrationAccessOutcome
{
    Allowed,
    Masked
}

/// <summary>The resolved row, populated only for <see cref="IntegrationAccessOutcome.Allowed" />.</summary>
public sealed record IntegrationAccessResult(IntegrationAccessOutcome Outcome,
    IntegrationExecutionSnapshot? Execution,
    IntegrationSessionSnapshot? Session);

/// <summary>
///     The ONE authorisation rule for every external route that addresses an execution or a session, written once so
///     no slice re-derives half of it.
///     <para>
///         The rule: the row exists, AND its <c>PrincipalId</c> is the caller's, AND the caller's CURRENT key
///         allowlists the row's <c>TriggerId</c>. A principal match alone is not sufficient — two credentials can share
///         an integrator and carry different allowlists, and under principal-only masking a key deliberately scoped to
///         one trigger could read and cancel its principal's executions under every other trigger. That is an
///         authorisation bypass by the very mechanism an operator used to scope the key.
///     </para>
///     <para>
///         The key row is re-read PER REQUEST, deliberately: the allowlist is not a claim, so narrowing or revoking a
///         key takes effect on the next call rather than at the next key mint. It is one indexed read on a
///         loopback-only, rate-limited surface.
///     </para>
/// </summary>
public sealed class IntegrationExternalAccess
{
    private readonly IIntegrationApiKeyStore _keys;
    private readonly IIntegrationExecutionStore _executions;
    private readonly IIntegrationSessionStore _sessions;

    public IntegrationExternalAccess(IIntegrationExecutionStore executions, IIntegrationSessionStore sessions, IIntegrationApiKeyStore keys)
    {
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
    }

    public async Task<IntegrationAccessResult> ResolveExecutionAsync(Guid executionId,
        IntegrationCallerIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        var execution = await _executions.GetByIdAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null || execution.PrincipalId != caller.PrincipalId)
        {
            return Masked;
        }

        return await AllowsAsync(caller, execution.TriggerId, cancellationToken).ConfigureAwait(false)
            ? new IntegrationAccessResult(IntegrationAccessOutcome.Allowed, execution, Session: null)
            : Masked;
    }

    public async Task<IntegrationAccessResult> ResolveSessionAsync(Guid sessionId,
        IntegrationCallerIdentity caller,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);

        // The store's two-column predicate IS the ownership limb — a missing row and a foreign one come back as the
        // same non-result, so there is no loaded row here for a later edit to start reading. The allowlist is the
        // second limb and stays here, because it is an authorisation rule rather than a persistence one.
        var session = await _sessions.GetForPrincipalAsync(sessionId, caller.PrincipalId, cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return Masked;
        }

        return await AllowsAsync(caller, session.TriggerId, cancellationToken).ConfigureAwait(false)
            ? new IntegrationAccessResult(IntegrationAccessOutcome.Allowed, Execution: null, session)
            : Masked;
    }

    /// <summary>Unknown, foreign, revoked and out-of-allowlist are indistinguishable, on purpose.</summary>
    private static IntegrationAccessResult Masked =>
        new(IntegrationAccessOutcome.Masked, Execution: null, Session: null);

    private async Task<bool> AllowsAsync(IntegrationCallerIdentity caller, Guid triggerId, CancellationToken cancellationToken)
    {
        var key = await _keys.GetByPrefixAsync(caller.KeyPrefix, cancellationToken).ConfigureAwait(false);
        if (key is null || key.RevokedAtUtc is not null || key.PrincipalId != caller.PrincipalId)
        {
            return false;
        }

        var allowed = IntegrationApiKeyService.DeserializeAllowList(key.AllowedTriggerIdsJson);
        return allowed is null || allowed.Contains(triggerId);
    }
}
