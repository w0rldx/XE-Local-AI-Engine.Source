namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     <c>GET cloud/codex/status</c> (Operator): reports the current Codex session and login state. The UI
///     polls this after starting a login until <see cref="CodexStatusResponse.SignedIn" /> flips true (or the pending
///     login resolves). Returns no token material — only presence, the non-secret account id, the access-token
///     expiry, and whether a browser login is in flight.
///     <para>
///         <see cref="CodexStatusResponse.SignedIn" /> is gated on a <b>non-expired</b> (skew-adjusted) access token, so a
///         stale session does not report signed-in with a past <see cref="CodexStatusResponse.ExpiresAtUtc" />. The
///         account id + expiry stay populated when a session exists so the UI can show a "session expired — re-authenticate"
///         state.
///     </para>
/// </summary>
public sealed class CodexStatusEndpoint(CodexSessionService session)
    : EndpointWithoutRequest<CodexStatusResponse>
{
    private readonly CodexSessionService _session = session ?? throw new ArgumentNullException(nameof(session));

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudCodex.Status);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await _session.GetStatusAsync(ct).ConfigureAwait(false);

        await Send.OkAsync(new CodexStatusResponse
        {
            SignedIn = status.SignedIn,
            AccountId = status.AccountId,
            ExpiresAtUtc = status.ExpiresAtUtc,
            LoginPending = status.LoginPending
        }, ct).ConfigureAwait(false);
    }
}
