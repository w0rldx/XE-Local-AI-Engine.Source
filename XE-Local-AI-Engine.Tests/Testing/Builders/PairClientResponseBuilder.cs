namespace XE_Local_AI_Engine.Tests.Testing.Builders;

using XE_Local_AI_Engine.Models;

public sealed class PairClientResponseBuilder
{
    private Guid _clientNodeId = Guid.NewGuid();
    private string _accessToken = "eyJhbGciOiJub25lIn0.eyJleHAiOjQxMDI0NDQ4MDB9.";
    private string _refreshToken = "test-refresh-token";
    private DateTimeOffset _expiresAt = DateTimeOffset.UtcNow.AddDays(30);

    private PairClientResponseBuilder()
    {
    }

    public static PairClientResponseBuilder Valid() => new();

    public PairClientResponseBuilder WithClientNodeId(Guid clientNodeId)
    {
        _clientNodeId = clientNodeId;
        return this;
    }

    public PairClientResponseBuilder WithToken(string accessToken)
    {
        ArgumentNullException.ThrowIfNull(accessToken);
        _accessToken = accessToken;
        return this;
    }

    public PairClientResponseBuilder WithRefreshToken(string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(refreshToken);
        _refreshToken = refreshToken;
        return this;
    }

    public PairClientResponseBuilder WithExpiresAt(DateTimeOffset expiresAt)
    {
        _expiresAt = expiresAt;
        return this;
    }

    public PairClientResponse Build()
    {
        return new PairClientResponse
        {
            ClientNodeId = _clientNodeId,
            AccessToken = _accessToken,
            RefreshToken = _refreshToken,
            ExpiresAt = _expiresAt,
        };
    }
}
