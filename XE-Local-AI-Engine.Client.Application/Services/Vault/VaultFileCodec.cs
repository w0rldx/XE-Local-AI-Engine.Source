namespace XE_Local_AI_Engine.Client.Services.Vault;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>The on-disk shape of a <c>node.key</c>.</summary>
public enum VaultFileFormat
{
    /// <summary>The pre-vault raw-base64 or DPAPI-wrapped secret: anything that does not start with <c>{</c>.</summary>
    Legacy,

    /// <summary>The passphrase-wrapped JSON <see cref="VaultFile" />.</summary>
    V2
}

/// <summary>
///     Pure read, write and wrap operations on the v2 <c>node.key</c>. No state, no DI: the pre-host that unlocks
///     the vault runs before the service container exists.
/// </summary>
/// <remarks>
///     Every key and secret buffer this class creates is zeroed before it returns; the master key a caller passes in
///     or receives stays the caller's to zero.
/// </remarks>
public static class VaultFileCodec
{
    /// <summary>The key file name under the node data directory. Must equal <c>DesktopBootstrap.KeyFileName</c>.</summary>
    public const string KeyFileName = "node.key";

    public const string ExpectedMagic = "xe-vault";
    public const int CurrentVersion = 2;

    private static readonly byte[] PasswordAad = Encoding.ASCII.GetBytes("xe-vault|v2|password");
    private static readonly byte[] RecoveryAad = Encoding.ASCII.GetBytes("xe-vault|v2|recovery");

    // Same pattern as NodePayloadProtector: AesGcm itself is constructed only inside AesGcmNodeAeadCipher.
    private static readonly INodeAeadCipher Cipher = new AesGcmNodeAeadCipher();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static VaultFileFormat Detect(ReadOnlySpan<byte> content)
    {
        var trimmed = content.TrimStart(" \t\r\n"u8);
        return !trimmed.IsEmpty && trimmed[0] == (byte)'{' ? VaultFileFormat.V2 : VaultFileFormat.Legacy;
    }

    /// <summary>Parses and validates a v2 file. Throws <see cref="InvalidDataException" /> on anything else.</summary>
    public static VaultFile Read(ReadOnlySpan<byte> content)
    {
        VaultFile? file;
        try
        {
            file = JsonSerializer.Deserialize<VaultFile>(content, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The node vault file is not valid JSON.", exception);
        }

        if (file is null
            || !string.Equals(file.Magic, ExpectedMagic, StringComparison.Ordinal)
            || file.Version != CurrentVersion
            || !string.Equals(file.Kdf.Alg, VaultKdf.Algorithm, StringComparison.Ordinal)
            || file.Kdf.Iterations is < VaultKdf.MinimumIterations or > VaultKdf.MaximumIterations
            || file.Kdf.Salt.Length != VaultKdf.SaltLength
            || file.Recovery.Salt.Length != VaultKdf.SaltLength
            || file.Password.Nonce.Length != Cipher.NonceSize
            || file.Recovery.Nonce.Length != Cipher.NonceSize
            || file.Password.Ct.Length != VaultKdf.KeyLength + Cipher.TagSize
            || file.Recovery.Ct.Length != VaultKdf.KeyLength + Cipher.TagSize)
        {
            throw new InvalidDataException("The node vault file is not a supported xe-vault v2 file.");
        }

        return file;
    }

    public static byte[] Serialize(VaultFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return JsonSerializer.SerializeToUtf8Bytes(file, JsonOptions);
    }

    /// <summary>Replaces <paramref name="path" /> atomically with the file created 0600 (Windows: user-only ACL).</summary>
    public static Task WriteAsync(string path, VaultFile file, CancellationToken cancellationToken)
    {
        return SecureFilePermissions.WriteAllBytesAtomicAsync(path, Serialize(file), cancellationToken);
    }

    /// <summary>Wraps <paramref name="masterKey" /> under a password and a fresh recovery code.</summary>
    /// <returns>The vault file and the formatted recovery code, which is shown once and never stored.</returns>
    public static VaultCreation Create(ReadOnlySpan<byte> masterKey,
        string password,
        DateTimeOffset nowUtc,
        int iterations = VaultKdf.DefaultIterations)
    {
        ValidateMasterKey(masterKey);
        var code = RandomNumberGenerator.GetBytes(VaultKdf.RecoveryCodeLength);
        try
        {
            var recovery = WrapWithRecovery(masterKey, code);
            var (kdf, passwordWrap) = WrapWithPassword(masterKey, password, iterations);
            var file = new VaultFile
            {
                Magic = ExpectedMagic,
                Version = CurrentVersion,
                Kdf = kdf,
                Password = passwordWrap,
                Recovery = recovery,
                CreatedUtc = nowUtc,
                RewrappedUtc = null
            };
            return new VaultCreation
            {
                File = file,
                RecoveryCode = VaultKdf.FormatRecoveryCode(code)
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
        }
    }

    /// <summary>A fresh random recovery code, formatted for the operator; it is shown once and never stored.</summary>
    public static string NewRecoveryCode()
    {
        var code = RandomNumberGenerator.GetBytes(VaultKdf.RecoveryCodeLength);
        try
        {
            return VaultKdf.FormatRecoveryCode(code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
        }
    }

    /// <summary>
    ///     Re-wraps BOTH slots: the password slot under <paramref name="newPassword" /> and the recovery slot under
    ///     <paramref name="newRecoveryCode" />, so the code that proved a reset stops working. The file format is unchanged.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="newRecoveryCode" /> is not a well-formed recovery code.</exception>
    public static VaultFile RewrapWithNewRecovery(VaultFile file,
        ReadOnlySpan<byte> masterKey,
        string newPassword,
        string newRecoveryCode,
        DateTimeOffset nowUtc,
        int iterations = VaultKdf.DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateMasterKey(masterKey);
        if (!VaultKdf.TryParseRecoveryCode(newRecoveryCode, out var code))
        {
            throw new ArgumentException("The new recovery code is not well formed.", nameof(newRecoveryCode));
        }

        try
        {
            var recovery = WrapWithRecovery(masterKey, code);
            var (kdf, passwordWrap) = WrapWithPassword(masterKey, newPassword, iterations);
            return file with
            {
                Kdf = kdf,
                Password = passwordWrap,
                Recovery = recovery,
                RewrappedUtc = nowUtc
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
        }
    }

    /// <summary>Re-wraps the password slot under <paramref name="newPassword" />; the recovery wrap is kept as is.</summary>
    public static VaultFile Rewrap(VaultFile file,
        ReadOnlySpan<byte> masterKey,
        string newPassword,
        DateTimeOffset nowUtc,
        int iterations = VaultKdf.DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateMasterKey(masterKey);
        var (kdf, passwordWrap) = WrapWithPassword(masterKey, newPassword, iterations);
        return file with
        {
            Kdf = kdf,
            Password = passwordWrap,
            RewrappedUtc = nowUtc
        };
    }

    /// <summary>Returns the master key. Throws <see cref="VaultUnlockException" /> on a wrong password or tampering.</summary>
    public static byte[] UnwrapWithPassword(VaultFile file, string password)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrEmpty(password))
        {
            throw new VaultUnlockException();
        }

        var kek = VaultKdf.DerivePasswordKek(password, file.Kdf.Salt.Span, file.Kdf.Iterations);
        try
        {
            return Unwrap(kek, file.Password.Nonce.Span, file.Password.Ct.Span, PasswordAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Returns the master key. Throws <see cref="VaultUnlockException" /> on a malformed or wrong code.</summary>
    public static byte[] UnwrapWithRecovery(VaultFile file, string recoveryCode)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!VaultKdf.TryParseRecoveryCode(recoveryCode, out var code))
        {
            throw new VaultUnlockException();
        }

        byte[]? kek = null;
        try
        {
            kek = VaultKdf.DeriveRecoveryKek(code, file.Recovery.Salt.Span);
            return Unwrap(kek, file.Recovery.Nonce.Span, file.Recovery.Ct.Span, RecoveryAad);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
            if (kek is not null)
            {
                CryptographicOperations.ZeroMemory(kek);
            }
        }
    }

    private static VaultRecoveryWrap WrapWithRecovery(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> code)
    {
        var recoverySalt = RandomNumberGenerator.GetBytes(VaultKdf.SaltLength);
        var recoveryKek = VaultKdf.DeriveRecoveryKek(code, recoverySalt);
        try
        {
            var (nonce, ct) = Wrap(recoveryKek, masterKey, RecoveryAad);
            return new VaultRecoveryWrap
            {
                Salt = recoverySalt,
                Nonce = nonce,
                Ct = ct
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(recoveryKek);
        }
    }

    private static PasswordSlot WrapWithPassword(ReadOnlySpan<byte> masterKey,
        string password,
        int iterations)
    {
        if (iterations is < VaultKdf.MinimumIterations or > VaultKdf.MaximumIterations)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations));
        }

        var salt = RandomNumberGenerator.GetBytes(VaultKdf.SaltLength);
        var kek = VaultKdf.DerivePasswordKek(password, salt, iterations);
        try
        {
            var (nonce, ct) = Wrap(kek, masterKey, PasswordAad);
            return new PasswordSlot(new VaultKdfParameters
                {
                    Alg = VaultKdf.Algorithm,
                    Iterations = iterations,
                    Salt = salt
                },
                new VaultPasswordWrap
                {
                    Nonce = nonce,
                    Ct = ct
                });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    private static SealedKey Wrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(Cipher.NonceSize);
        var sealedKey = Cipher.Encrypt(kek, nonce, masterKey, aad);
        return new SealedKey(nonce, [.. sealedKey.Ciphertext, .. sealedKey.Tag]);
    }

    private readonly record struct PasswordSlot(VaultKdfParameters Kdf, VaultPasswordWrap WrappedKey);

    private readonly record struct SealedKey(byte[] Nonce, byte[] Ct);

    private static byte[] Unwrap(ReadOnlySpan<byte> kek, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ctWithTag, ReadOnlySpan<byte> aad)
    {
        var split = ctWithTag.Length - Cipher.TagSize;
        try
        {
            return Cipher.Decrypt(kek, nonce, ctWithTag[..split], ctWithTag[split..], aad);
        }
        catch (CryptographicException exception)
        {
            throw new VaultUnlockException("The password or recovery code does not unlock the node vault.", exception);
        }
    }

    private static void ValidateMasterKey(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != VaultKdf.KeyLength)
        {
            throw new ArgumentException($"The node master key is exactly {VaultKdf.KeyLength} bytes.", nameof(masterKey));
        }
    }

    /// <summary>A new vault file and its one-time recovery code; a class, so no generated ToString can print the code.</summary>
    public sealed class VaultCreation
    {
        public required VaultFile File { get; init; }

        public required string RecoveryCode { get; init; }

        public void Deconstruct(out VaultFile file, out string recoveryCode)
        {
            file = File;
            recoveryCode = RecoveryCode;
        }
    }
}
