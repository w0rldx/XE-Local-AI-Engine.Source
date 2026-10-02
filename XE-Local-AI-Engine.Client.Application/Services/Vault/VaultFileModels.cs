namespace XE_Local_AI_Engine.Client.Services.Vault;

using System.Text.Json.Serialization;

/// <summary>
///     The v2 <c>node.key</c>: the 32-byte operator secret wrapped twice, once under a KEK derived from the admin
///     password and once under a KEK derived from the one-time recovery code. The file never holds the raw secret.
/// </summary>
/// <remarks>
///     Every field the unwrap needs (algorithm, iteration count, salts, nonces) travels in the file, so a later
///     <see cref="VaultKdf.DefaultIterations" /> bump never strands an existing vault. Each <c>ct</c> is the AES-GCM
///     ciphertext followed by its 16-byte tag.
/// </remarks>
public sealed record VaultFile
{
    public required string Magic { get; init; }

    [JsonPropertyName("v")]
    public required int Version { get; init; }

    public required VaultKdfParameters Kdf { get; init; }

    public required VaultPasswordWrap Password { get; init; }

    public required VaultRecoveryWrap Recovery { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public DateTimeOffset? RewrappedUtc { get; init; }
}

public sealed record VaultKdfParameters
{
    public required string Alg { get; init; }

    public required int Iterations { get; init; }

    public required ReadOnlyMemory<byte> Salt { get; init; }
}

public sealed record VaultPasswordWrap
{
    public required ReadOnlyMemory<byte> Nonce { get; init; }

    public required ReadOnlyMemory<byte> Ct { get; init; }
}

public sealed record VaultRecoveryWrap
{
    public required ReadOnlyMemory<byte> Salt { get; init; }

    public required ReadOnlyMemory<byte> Nonce { get; init; }

    public required ReadOnlyMemory<byte> Ct { get; init; }
}
