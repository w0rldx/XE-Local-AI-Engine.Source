namespace XE_Local_AI_Engine.Client.Hosting.Vault;

using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

/// <summary>
///     The one-time ticket a password unlock leaves in the <c>node_ut</c> cookie (ADR 0018): the real host's
///     <c>auth/refresh</c> trades it once for a session, so an unlock does not end on the sign-in page.
/// </summary>
/// <remarks>
///     Only the SHA-256 of the value is held. The first consume attempt spends it, matching or not, and it never
///     consumes after <see cref="ExpiresAt" />. The password it stands for is the admin password (ADR 0018 decision 4).
/// </remarks>
internal sealed class VaultUnlockTicket
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private byte[]? _hash;

    internal VaultUnlockTicket(string value, DateTimeOffset expiresAt)
    {
        _hash = Hash(value);
        ExpiresAt = expiresAt;
    }

    internal DateTimeOffset ExpiresAt { get; }

    /// <summary>32 random bytes, base64url, safe as a cookie value.</summary>
    internal static string NewValue() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    internal bool TryConsume(string presented, DateTimeOffset now)
    {
        var expected = Interlocked.Exchange(ref _hash, null);
        return expected is not null && now < ExpiresAt && CryptographicOperations.FixedTimeEquals(expected, Hash(presented));
    }

    private static byte[] Hash(string value) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
