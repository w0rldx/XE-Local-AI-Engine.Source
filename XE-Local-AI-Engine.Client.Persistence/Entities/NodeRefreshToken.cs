namespace XE_Local_AI_Engine.Client.Persistence.Entities;

public sealed class NodeRefreshToken
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public required string UserId { get; set; }

    public required string TokenHash { get; set; }

    public required DateTime ExpiresAtUtc { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? RevokedAtUtc { get; set; }

    /// <summary>The <see cref="Id" /> of the token rotation issued in this one's place; null when logout, a password change or a reset revoked it.</summary>
    public string? ReplacedByTokenId { get; set; }
}
