namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>One sign-in: the rotation chain of refresh tokens a login starts, and the <c>sid</c> its access tokens carry.</summary>
public sealed class NodeAuthSession
{
    public const int ClientLabelMaxLength = 160;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public required string UserId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The last sign-in or refresh on this session.</summary>
    public DateTime LastUsedAtUtc { get; set; }

    /// <summary>A short client description taken from the signing-in request's User-Agent; null when none was sent.</summary>
    public string? ClientLabel { get; set; }

    public DateTime? RevokedAtUtc { get; set; }
}
