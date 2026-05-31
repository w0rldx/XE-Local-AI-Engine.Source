namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Represents node refresh token.
/// </summary>
public sealed class NodeRefreshToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public required string UserId { get; set; }

    public required string TokenHash { get; set; }

    public required DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
}
