namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;

/// <summary>
///     Reports the current Codex session and login state. Operator-gated.
/// </summary>
/// <remarks>
///     The UI polls this after starting a login until <see cref="CodexStatusResponse.SignedIn" /> flips true, or the
///     pending login resolves. It returns no token material — only presence, the non-secret account id, the
///     access-token expiry, and whether a browser login is in flight. <see cref="CodexStatusResponse.SignedIn" /> is
///     gated on a <b>non-expired</b> (skew-adjusted) access token, so a stale session does not report signed-in with a
///     past expiry, while the account id and expiry stay populated so the UI can show a "session expired" state.
/// </remarks>
public sealed class CodexStatusEndpoint : EndpointWithoutRequest<CodexStatusResponse>
{
    private readonly CodexSessionService _session;

    public CodexStatusEndpoint(CodexSessionService session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.CloudCodex.Status);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var status = await _session.GetStatusAsync(ct);

        await Send.OkAsync(new CodexStatusResponse
        {
            SignedIn = status.SignedIn,
            AccountId = status.AccountId,
            ExpiresAtUtc = status.ExpiresAtUtc,
            LoginPending = status.LoginPending
        }, ct);
    }
}
