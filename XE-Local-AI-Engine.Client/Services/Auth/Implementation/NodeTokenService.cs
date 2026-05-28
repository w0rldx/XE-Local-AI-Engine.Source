namespace XE_Local_AI_Engine.Client.Services.Auth.Implementation;

using XE_Local_AI_Engine.Client.Services.Auth;

using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeTokenService : INodeTokenService
{
    private const int RefreshTokenBytes = 64;
    private static readonly JsonWebTokenHandler TokenHandler = new();

    private readonly INodeJwtKeyProvider _jwtKeyProvider;
    private readonly IOptions<NodeAuthOptions> _options;
    private readonly TimeProvider _timeProvider;

    public NodeTokenService(INodeJwtKeyProvider jwtKeyProvider, IOptions<NodeAuthOptions> options, TimeProvider timeProvider)
    {
        _jwtKeyProvider = jwtKeyProvider ?? throw new ArgumentNullException(nameof(jwtKeyProvider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public (string AccessToken, DateTime ExpiresAtUtc) CreateAccessToken(NodeUser user, IEnumerable<string> roles)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roles);

        if (string.IsNullOrWhiteSpace(user.Id))
        {
            throw new InvalidOperationException("Node user id is required to create an access token.");
        }

        var issuedAt = _timeProvider.GetUtcNow();
        var expiresAt = issuedAt.AddMinutes(_options.Value.Jwt.AccessTokenMinutes);
        var claims = CreateClaims(user, roles, issuedAt);
        var signingKey = _jwtKeyProvider.SigningKey.ToArray();

        try
        {
            var descriptor = new SecurityTokenDescriptor
            {
                Audience = _options.Value.Jwt.Audience,
                Expires = expiresAt.UtcDateTime,
                IssuedAt = issuedAt.UtcDateTime,
                Issuer = _options.Value.Jwt.Issuer,
                NotBefore = issuedAt.UtcDateTime,
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(signingKey), SecurityAlgorithms.HmacSha256),
                Subject = new ClaimsIdentity(claims)
            };

            return (TokenHandler.CreateToken(descriptor), expiresAt.UtcDateTime);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signingKey);
        }
    }

    public string CreateRefreshTokenRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(RefreshTokenBytes);
        try
        {
            return Convert.ToBase64String(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public string HashRefreshToken(string raw)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raw);

        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static Claim[] CreateClaims(NodeUser user, IEnumerable<string> roles, DateTimeOffset issuedAt)
    {
        var userName = string.IsNullOrWhiteSpace(user.UserName) ? user.Email ?? user.Id : user.UserName;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(JwtRegisteredClaimNames.Iat, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new(JwtRegisteredClaimNames.Name, userName)
        };

        claims.AddRange(roles.Where(static role => !string.IsNullOrWhiteSpace(role))
                             .Distinct(StringComparer.Ordinal)
                              .Select(static role => new Claim(NodeAuthorizationPolicies.RoleClaimType, role)));

        return [.. claims];
    }
}
