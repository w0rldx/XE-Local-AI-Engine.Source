namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public interface INodeTokenService
{
    /// <summary>Mints an access token; with <paramref name="sessionId" /> it carries a <c>sid</c> that dies with that session.</summary>
    AccessTokenResult CreateAccessToken(NodeUser user, IEnumerable<string> roles, string? sessionId = null);

    string CreateRefreshTokenRaw();

    string HashRefreshToken(string raw);
}
