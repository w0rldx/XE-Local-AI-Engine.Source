namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public interface INodeTokenService
{
    (string AccessToken, DateTime ExpiresAtUtc) CreateAccessToken(NodeUser user, IEnumerable<string> roles);

    string CreateRefreshTokenRaw();

    string HashRefreshToken(string raw);
}
