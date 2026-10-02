namespace XE_Local_AI_Engine.Client.Services.Vault;

using System.Security.Cryptography;
using System.Text;

/// <summary>
///     Key-encryption-key derivations and the recovery-code format for the v2 <c>node.key</c>.
/// </summary>
/// <remarks>
///     The password KEK is PBKDF2-SHA512 because a password is low-entropy and needs stretching. The recovery KEK is a
///     single HKDF-SHA256 step: the code is CSPRNG output, so there is no guess space for stretching to slow down.
/// </remarks>
public static class VaultKdf
{
    public const string Algorithm = "PBKDF2-SHA512";

    /// <summary>The iteration count new wraps use. Unwrap always honours the count stored in the file.</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>The lowest stored count an unwrap accepts; a file below it is treated as tampered.</summary>
    public const int MinimumIterations = 1_000;

    /// <summary>
    ///     The highest stored count an unwrap accepts, so a tampered file cannot pin a CPU on the unlock path.
    /// </summary>
    public const int MaximumIterations = 10_000_000;

    public const int KeyLength = 32;
    public const int SaltLength = 16;

    /// <summary>
    ///     Recovery-code entropy: 25 bytes is 200 bits, exactly 40 base32 characters, shown as 8 groups of 5.
    /// </summary>
    public const int RecoveryCodeLength = 25;

    private const int RecoveryGroupLength = 5;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static readonly byte[] RecoveryInfo = Encoding.ASCII.GetBytes("xe-vault-recovery|v2");

    public static byte[] DerivePasswordKek(string password, ReadOnlySpan<byte> salt, int iterations)
    {
        ArgumentException.ThrowIfNullOrEmpty(password);
        return Rfc2898DeriveBytes.Pbkdf2(password.AsSpan(), salt, iterations, HashAlgorithmName.SHA512, KeyLength);
    }

    public static byte[] DeriveRecoveryKek(ReadOnlySpan<byte> recoveryCode, ReadOnlySpan<byte> salt)
    {
        var kek = new byte[KeyLength];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryCode, kek, salt, RecoveryInfo);
        return kek;
    }

    /// <summary>Formats raw recovery-code bytes as upper-case base32 in dash-separated groups of five.</summary>
    public static string FormatRecoveryCode(ReadOnlySpan<byte> code)
    {
        if (code.Length != RecoveryCodeLength)
        {
            throw new ArgumentException($"A recovery code is exactly {RecoveryCodeLength} bytes.", nameof(code));
        }

        var text = new StringBuilder(capacity: 48);
        var buffer = 0;
        var bits = 0;
        foreach (var value in code)
        {
            buffer = ((buffer << 8) | value) & 0xFFF;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                AppendGrouped(text, Base32Alphabet[(buffer >> bits) & 31]);
            }
        }

        return text.ToString();
    }

    /// <summary>
    ///     Parses a recovery code typed by the operator: case-insensitive, dashes and whitespace ignored. The caller
    ///     zeroes <paramref name="code" /> after use.
    /// </summary>
    public static bool TryParseRecoveryCode(string? text, out byte[] code)
    {
        code = [];
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var decoded = new byte[RecoveryCodeLength];
        var written = 0;
        var buffer = 0;
        var bits = 0;
        foreach (var character in text)
        {
            if (character == '-' || char.IsWhiteSpace(character))
            {
                continue;
            }

            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(character), StringComparison.Ordinal);
            if (index < 0 || written == RecoveryCodeLength)
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            buffer = ((buffer << 5) | index) & 0xFFF;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                decoded[written++] = (byte)(buffer >> bits);
            }
        }

        if (written != RecoveryCodeLength || bits != 0)
        {
            CryptographicOperations.ZeroMemory(decoded);
            return false;
        }

        code = decoded;
        return true;
    }

    private static void AppendGrouped(StringBuilder text, char character)
    {
        var position = text.Length - (text.Length / (RecoveryGroupLength + 1));
        if (position > 0 && position % RecoveryGroupLength == 0)
        {
            text.Append('-');
        }

        text.Append(character);
    }
}
