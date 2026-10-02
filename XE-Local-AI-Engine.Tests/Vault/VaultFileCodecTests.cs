namespace XE_Local_AI_Engine.Tests.Vault;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Hosting;
using XE_Local_AI_Engine.Client.Services.Vault;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The v2 <c>node.key</c> wrap: the master key must come back only for the right password or recovery code, and a
///     single flipped byte anywhere in a wrap must refuse rather than return different key material.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class VaultFileCodecTests
{
    private const string Password = "Str0ng!Password123";

    // Tests wrap at the floor so the suite stays fast; the default count is proven by the iterations test below.
    private const int FastIterations = VaultKdf.MinimumIterations;

    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Create_ThenUnwrapWithPassword_ReturnsTheMasterKey()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var (file, _) = VaultFileCodec.Create(masterKey, Password, Now, FastIterations);

        var roundTripped = VaultFileCodec.Read(VaultFileCodec.Serialize(file));

        SameBytes(masterKey, VaultFileCodec.UnwrapWithPassword(roundTripped, Password));
    }

    [Test]
    public void Serialize_NeverContainsTheMasterKey()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var (file, _) = VaultFileCodec.Create(masterKey, Password, Now, FastIterations);

        var json = Encoding.UTF8.GetString(VaultFileCodec.Serialize(file));

        AssertEx.False(json.Contains(Convert.ToBase64String(masterKey), StringComparison.Ordinal));
        AssertEx.Contains(json, "\"magic\": \"xe-vault\"");
        AssertEx.Contains(json, "\"v\": 2");
    }

    [Test]
    public void UnwrapWithPassword_WithAWrongPassword_ThrowsVaultUnlockException()
    {
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);

        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(file, "Wr0ng!Password123"));
    }

    [Test]
    public void UnwrapWithPassword_WhenTheCiphertextIsTampered_ThrowsVaultUnlockException()
    {
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);
        var tamperedCt = file.Password.Ct.ToArray();
        tamperedCt[0] ^= 0x01;
        var tampered = file with { Password = file.Password with { Ct = tamperedCt } };

        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(tampered, Password));
    }

    [Test]
    public void Create_ThenUnwrapWithRecovery_ReturnsTheMasterKey()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var (file, recoveryCode) = VaultFileCodec.Create(masterKey, Password, Now, FastIterations);

        SameBytes(masterKey, VaultFileCodec.UnwrapWithRecovery(file, recoveryCode));
    }

    [Test]
    public void UnwrapWithRecovery_WithAnotherVaultsCode_ThrowsVaultUnlockException()
    {
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);
        var (_, otherCode) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);

        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithRecovery(file, otherCode));
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithRecovery(file, "not-a-code"));
    }

    [Test]
    public void Rewrap_ReplacesThePasswordWrapAndKeepsTheRecoveryWrapValid()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var (file, recoveryCode) = VaultFileCodec.Create(masterKey, Password, Now, FastIterations);
        const string newPassword = "N3w!Password4567";

        var rewrapped = VaultFileCodec.Rewrap(file, masterKey, newPassword, Now.AddDays(1), FastIterations);

        SameBytes(masterKey, VaultFileCodec.UnwrapWithPassword(rewrapped, newPassword));
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(rewrapped, Password));
        SameBytes(masterKey, VaultFileCodec.UnwrapWithRecovery(rewrapped, recoveryCode));
        SameBytes(file.Recovery.Ct.ToArray(), rewrapped.Recovery.Ct.ToArray());
        AssertEx.Equal(Now.AddDays(1), rewrapped.RewrappedUtc);
    }

    [Test]
    public void Unwrap_HonoursTheIterationCountStoredInTheFile()
    {
        var masterKey = RandomNumberGenerator.GetBytes(32);
        var (file, _) = VaultFileCodec.Create(masterKey, Password, Now, iterations: 1_000);

        var read = VaultFileCodec.Read(VaultFileCodec.Serialize(file));

        AssertEx.Equal(expected: 1_000, read.Kdf.Iterations);
        SameBytes(masterKey, VaultFileCodec.UnwrapWithPassword(read, Password));
        // The same file read with the default count would derive a different KEK; prove the count is load-bearing.
        var lied = read with { Kdf = read.Kdf with { Iterations = 1_001 } };
        _ = AssertEx.Throws<VaultUnlockException>(() => VaultFileCodec.UnwrapWithPassword(lied, Password));
    }

    [Test]
    public void Create_UsesTheDefaultIterationCount()
    {
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now);

        AssertEx.Equal(VaultKdf.DefaultIterations, file.Kdf.Iterations);
        AssertEx.Equal(VaultKdf.Algorithm, file.Kdf.Alg);
    }

    [Test]
    public void Read_RejectsAnIterationCountOutsideTheBounds()
    {
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);
        var huge = file with { Kdf = file.Kdf with { Iterations = VaultKdf.MaximumIterations + 1 } };

        _ = AssertEx.Throws<InvalidDataException>(() => VaultFileCodec.Read(VaultFileCodec.Serialize(huge)));
    }

    [Test]
    public void Detect_ReadsRawBase64AndDpapiShapedBytesAsLegacy()
    {
        var rawBase64 = Encoding.ASCII.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        // DPAPI blobs start 01 00 00 00 (version); any binary that does not open a JSON object is legacy.
        byte[] dpapiShaped = [0x01, 0x00, 0x00, 0x00, .. RandomNumberGenerator.GetBytes(200)];
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);

        AssertEx.Equal(VaultFileFormat.Legacy, VaultFileCodec.Detect(rawBase64));
        AssertEx.Equal(VaultFileFormat.Legacy, VaultFileCodec.Detect(dpapiShaped));
        AssertEx.Equal(VaultFileFormat.Legacy, VaultFileCodec.Detect([]));
        AssertEx.Equal(VaultFileFormat.V2, VaultFileCodec.Detect(VaultFileCodec.Serialize(file)));
    }

    [Test]
    public void RecoveryCode_IsEightGroupsOfFiveAndParsesLowercaseWithoutSeparators()
    {
        var (file, recoveryCode) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);

        var groups = recoveryCode.Split('-');
        AssertEx.Equal(expected: 8, groups.Length);
        AssertEx.True(groups.All(static group => group.Length == 5));

        var sloppy = " " + string.Concat(recoveryCode.Where(static c => c != '-').Select(char.ToLowerInvariant)) + "\n";
        AssertEx.True(VaultKdf.TryParseRecoveryCode(sloppy, out var parsed));
        AssertEx.True(VaultKdf.TryParseRecoveryCode(recoveryCode, out var exact));
        SameBytes(exact, parsed);
        AssertEx.NotNull(VaultFileCodec.UnwrapWithRecovery(file, sloppy));

        AssertEx.False(VaultKdf.TryParseRecoveryCode(recoveryCode + "A", out _));
        AssertEx.False(VaultKdf.TryParseRecoveryCode(recoveryCode[..^1], out _));
        AssertEx.False(VaultKdf.TryParseRecoveryCode(recoveryCode.Replace(recoveryCode[0], '1'), out _));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteAsync_CreatesTheFileOwnerOnly()
    {
        using var temp = new TempDirectory("xe-vault");
        var path = temp.FilePath(VaultFileCodec.KeyFileName);
        var (file, _) = VaultFileCodec.Create(RandomNumberGenerator.GetBytes(32), Password, Now, FastIterations);

        await VaultFileCodec.WriteAsync(path, file, CancellationToken.None);

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        AssertEx.Equal(VaultFileFormat.V2, VaultFileCodec.Detect(await File.ReadAllBytesAsync(path)));
    }

    private static void SameBytes(byte[] expected, byte[] actual)
    {
        AssertEx.True(expected.AsSpan().SequenceEqual(actual), "The byte sequences differ.");
    }

    [Test]
    public void KeyFileName_MatchesTheDesktopBootstrapKeyFile()
    {
        AssertEx.Equal(DesktopBootstrap.KeyFileName, VaultFileCodec.KeyFileName);
    }
}
