namespace XE_Local_AI_Engine.Client.Endpoints.Cloud.Codex.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.CodexOAuth.Auth;

/// <summary>
/// <c>POST cloud/codex/login</c> (Operator): starts the loopback PKCE login and returns the authorize URL so the
/// UI can render a copyable/clickable link (plan §8). Best-effort auto-opens the system browser; idempotent in
/// that a second call supersedes any stale pending login. The token exchange completes in the background — the UI
/// polls <c>cloud/codex/status</c> for completion. Never returns token material.
/// </summary>
public sealed class CodexLoginEndpoint(ICodexLoginCoordinator loginCoordinator)
    : EndpointWithoutRequest<CodexLoginResponse>
{
    private readonly ICodexLoginCoordinator _loginCoordinator =
        loginCoordinator ?? throw new ArgumentNullException(nameof(loginCoordinator));

    public override void Configure()
    {
        Post(LocalApiRoutes.CloudCodex.Login);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var authorizeUrl = _loginCoordinator.Start();
        await Send.OkAsync(new CodexLoginResponse { AuthorizeUrl = authorizeUrl.ToString() }, ct).ConfigureAwait(false);
    }
}
