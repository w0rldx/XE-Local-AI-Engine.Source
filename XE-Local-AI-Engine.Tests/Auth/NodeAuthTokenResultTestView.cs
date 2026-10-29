namespace XE_Local_AI_Engine.Tests.Auth;

using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>
///     Test-only reads over <see cref="NodeAuthTokenResult" />: the session tests assert on success and hand tokens on to
///     the next call, and a failed result simply has no token to hand on.
/// </summary>
internal static class NodeAuthTokenResultTestView
{
    extension(NodeAuthTokenResult result)
    {
        public bool Succeeded => result is NodeAuthTokenResult.Issued;

        public string? AccessToken => (result as NodeAuthTokenResult.Issued)?.AccessToken;

        public string? RefreshToken => (result as NodeAuthTokenResult.Issued)?.RefreshToken;
    }
}
