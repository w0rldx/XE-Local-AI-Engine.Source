namespace XE_Local_AI_Engine.Client.Services.Auth;

using XE_Local_AI_Engine.Client.Persistence.Entities;

public interface INodeTokenService
{
    AccessTokenResult CreateAccessToken(NodeUser user, IEnumerable<string> roles);

    string CreateRefreshTokenRaw();

    string HashRefreshToken(string raw);
}
