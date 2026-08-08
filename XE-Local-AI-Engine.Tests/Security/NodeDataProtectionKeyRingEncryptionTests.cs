namespace XE_Local_AI_Engine.Tests.Security;

using System.Security.Cryptography;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Security.DataProtection;
using XE_Local_AI_Engine.Client.Services.Auth.Implementation;
using XE_Local_AI_Engine.Client.Services.Persistence.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     BE-02 coverage: the non-Windows Data Protection key-ring encryptor/decryptor and its KEK derivation. Proves the
///     round-trip, fail-closed-on-wrong-KEK, an end-to-end Protect/Unprotect over an on-disk ring that is genuinely
///     encrypted at rest, and backward-compatibility with a legacy plaintext key-ring.
/// </summary>
public sealed class NodeDataProtectionKeyRingEncryptionTests
{
    private static readonly byte[] SampleKek = Enumerable.Range(start: 7, count: 32).Select(static value => (byte)value).ToArray();

    [Test]
    public async Task KeyDerivation_WhenInputsMatch_IsStableAndDistinctFromSqliteAndJwtKeys()
    {
        await Task.CompletedTask;

        var operatorSecret = Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray();
        const string nodeName = "worker-node-alpha";
        var configuration = new ConfigurationBuilder()
                            .AddInMemoryCollection(new Dictionary<string, string?>
                            {
                                [NodeOperatorSecretProvider.EnvVarName] = Convert.ToBase64String(operatorSecret)
                            })
                            .Build();

        var options = Options.Create(new WorkerNodeOptions
        {
            NodeName = nodeName
        });
        using var first = new NodeDataProtectionKeyProvider(options, new NodeOperatorSecretProvider(configuration));
        using var second = new NodeDataProtectionKeyProvider(options, new NodeOperatorSecretProvider(configuration));
        using var sqlite = new NodeSqliteKeyHolder(options, new NodeOperatorSecretProvider(configuration));
        using var jwt = new NodeJwtKeyProvider(options, new NodeOperatorSecretProvider(configuration));

        AssertEx.True(first.Key.Span.SequenceEqual(second.Key.Span), "KEK derivation must be stable for the same operator secret and node name.");
        AssertEx.Equal(expected: 32, first.Key.Length);
        AssertEx.False(first.Key.Span.SequenceEqual(sqlite.Key.Span), "The DP KEK must use a distinct HKDF info string from the SQLite key.");
        AssertEx.False(first.Key.Span.SequenceEqual(jwt.SigningKey.Span), "The DP KEK must use a distinct HKDF info string from the JWT signing key.");
    }

    [Test]
    public async Task KeyProvider_WhenDisposed_ThrowsOnSubsequentAccess()
    {
        await Task.CompletedTask;

        var provider = new NodeDataProtectionKeyProvider(Options.Create(new WorkerNodeOptions
            {
                NodeName = "worker-node-alpha"
            }),
            new NodeOperatorSecretProvider(BuildOperatorSecretConfiguration()));

        _ = provider.Key.Span[0];
        provider.Dispose();

        _ = await AssertEx.ThrowsAsync<ObjectDisposedException>(() =>
        {
            _ = provider.Key.Span[0];
            return Task.CompletedTask;
        }).ConfigureAwait(false);
    }

    [Test]
    public async Task EncryptThenDecrypt_WithSameKek_RecoversByteIdenticalXml()
    {
        await Task.CompletedTask;

        var plaintext = new XElement("descriptor",
            new XElement("secret", new XAttribute("size", "256"), "AAECAwQFBgcICQoLDA0ODw=="),
            new XComment(" representative nested key material "));
        var expected = plaintext.ToString(SaveOptions.DisableFormatting);

        var cipher = new AesGcmNodeAeadCipher();
        using var keyProvider = new FixedKeyProvider(SampleKek);
        var encryptor = new NodeDataProtectionKeyRingEncryptor(keyProvider, cipher);
        using var decryptorServices = BuildServiceProvider(SampleKek);
        var decryptor = new NodeDataProtectionKeyRingDecryptor(decryptorServices);

        var encrypted = encryptor.Encrypt(plaintext);
        var roundTripped = decryptor.Decrypt(encrypted.EncryptedElement);

        AssertEx.Equal(expected, roundTripped.ToString(SaveOptions.DisableFormatting));
        AssertEx.Equal(typeof(NodeDataProtectionKeyRingDecryptor), encrypted.DecryptorType);
        // The wrapped element must not carry the plaintext secret verbatim.
        AssertEx.False(encrypted.EncryptedElement.ToString(SaveOptions.DisableFormatting).Contains("AAECAwQFBgcICQoLDA0ODw==", StringComparison.Ordinal),
            "The encrypted element must not leak the plaintext secret.");
    }

    [Test]
    public async Task Decrypt_WithWrongKek_FailsClosed()
    {
        await Task.CompletedTask;

        var plaintext = new XElement("descriptor", new XElement("secret", "AAECAwQFBgcICQoLDA0ODw=="));
        var cipher = new AesGcmNodeAeadCipher();
        using var keyProvider = new FixedKeyProvider(SampleKek);
        var encryptor = new NodeDataProtectionKeyRingEncryptor(keyProvider, cipher);
        var encrypted = encryptor.Encrypt(plaintext);

        var wrongKek = Enumerable.Range(start: 100, count: 32).Select(static value => (byte)value).ToArray();
        using var decryptorServices = BuildServiceProvider(wrongKek);
        var decryptor = new NodeDataProtectionKeyRingDecryptor(decryptorServices);

        // A wrong KEK must throw, never silently return garbage. It surfaces as the distinctive typed failure (the
        // signal the fail-closed key resolver keys off) wrapping the underlying authentication-tag mismatch.
        var thrown = await AssertEx.ThrowsAsync<NodeDataProtectionKeyRingDecryptionException>(() =>
        {
            _ = decryptor.Decrypt(encrypted.EncryptedElement);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        AssertEx.True(thrown.InnerException is AuthenticationTagMismatchException,
            "The distinctive key-ring decryption failure must wrap the underlying authentication-tag mismatch.");
    }

    [Test]
    public async Task DataProtectionProvider_WithWrongKek_HardFailsInsteadOfSilentlyRegeneratingTheRing()
    {
        await Task.CompletedTask;

        var ringDirectory = CreateTempDirectory();
        try
        {
            const string payload = "orphanable-oauth-token";
            string protectedPayload;

            // Write a genuinely encrypted-at-rest ring under the correct KEK.
            using (var writeServices = BuildDataProtectionServices(ringDirectory, SampleKek))
            {
                var protector = writeServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests");
                protectedPayload = protector.Protect(payload);
            }

            var keysBefore = Directory.GetFiles(ringDirectory, "key-*.xml").Length;
            AssertEx.Equal(expected: 1, keysBefore);

            var wrongKek = Enumerable.Range(start: 100, count: 32).Select(static value => (byte)value).ToArray();

            // Bring the ring up under the WRONG KEK and force key-ring resolution. The default resolver would treat the
            // undecryptable key as ineligible and regenerate; the fail-closed decorator must throw instead.
            var thrown = await AssertEx.ThrowsAsync<Exception>(() =>
            {
                using var wrongServices = BuildDataProtectionServices(ringDirectory, wrongKek);
                _ = wrongServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests").Protect("forces-resolution");
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            AssertEx.True(ChainContains(thrown, static ex => ex is InvalidOperationException && ex.Message.Contains("Refusing to regenerate", StringComparison.Ordinal)),
                "The fail-closed resolver must be the cause of the failure (refusing to regenerate the ring).");
            AssertEx.True(ChainContains(thrown, static ex => ex is NodeDataProtectionKeyRingDecryptionException),
                "The failure must originate from the distinctive key-ring decryption exception, proving it was an encrypted-key decrypt failure.");

            // Critically, no new key was generated — the existing (still-valid-under-the-right-KEK) ring is intact.
            AssertEx.Equal(keysBefore, Directory.GetFiles(ringDirectory, "key-*.xml").Length);

            // Restoring the correct KEK must unprotect the original payload — proving nothing was orphaned.
            using var recoveredServices = BuildDataProtectionServices(ringDirectory, SampleKek);
            var recovered = recoveredServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests");
            AssertEx.Equal(payload, recovered.Unprotect(protectedPayload));
        }
        finally
        {
            DeleteTempDirectory(ringDirectory);
        }
    }

    [Test]
    public async Task DataProtectionProvider_WithEncryptor_ProtectsAtRestAndRoundTripsOverFreshProvider()
    {
        await Task.CompletedTask;

        var ringDirectory = CreateTempDirectory();
        try
        {
            const string payload = "cloud-credential-secret";
            string protectedPayload;

            using (var writeServices = BuildDataProtectionServices(ringDirectory, SampleKek))
            {
                var protector = writeServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests");
                protectedPayload = protector.Protect(payload);
            }

            // The persisted key XML must be encrypted at rest: the paired decryptor is named and the plaintext
            // master-key element is gone.
            var keyFiles = Directory.GetFiles(ringDirectory, "key-*.xml");
            AssertEx.False(keyFiles.Length == 0, "Data Protection should have persisted at least one key file.");
            var keyXml = await File.ReadAllTextAsync(keyFiles[0]).ConfigureAwait(false);
            AssertEx.True(keyXml.Contains(nameof(NodeDataProtectionKeyRingDecryptor), StringComparison.Ordinal),
                "The persisted key must record the BE-02 decryptor, proving it was wrapped at rest.");
            AssertEx.False(keyXml.Contains("<masterKey", StringComparison.Ordinal),
                "The plaintext masterKey element must not be present once the key-ring is encrypted at rest.");

            // A completely fresh provider over the same on-disk ring must unwrap the key (via the activated decryptor)
            // and unprotect the payload.
            using var readServices = BuildDataProtectionServices(ringDirectory, SampleKek);
            var reader = readServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests");
            AssertEx.Equal(payload, reader.Unprotect(protectedPayload));
        }
        finally
        {
            DeleteTempDirectory(ringDirectory);
        }
    }

    [Test]
    public async Task DataProtectionProvider_WithEncryptor_StillReadsLegacyPlaintextKeyRing()
    {
        await Task.CompletedTask;

        var ringDirectory = CreateTempDirectory();
        try
        {
            const string payload = "legacy-token";
            string protectedPayload;

            // Simulate an EXISTING install: a key-ring written WITHOUT any encryptor (plaintext keys on disk).
            using (var legacyServices = BuildDataProtectionServices(ringDirectory, kek: null))
            {
                var protector = legacyServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests");
                protectedPayload = protector.Protect(payload);
            }

            var legacyKeyXml = await File.ReadAllTextAsync(Directory.GetFiles(ringDirectory, "key-*.xml")[0]).ConfigureAwait(false);
            AssertEx.True(legacyKeyXml.Contains("<masterKey", StringComparison.Ordinal),
                "The legacy ring must genuinely contain a plaintext masterKey element for this test to be meaningful.");

            // Now bring up a provider WITH the BE-02 encryptor over the same ring. The pre-existing plaintext key must
            // still be read directly (the encryptor is write-side only) and the legacy payload must still unprotect.
            using var upgradedServices = BuildDataProtectionServices(ringDirectory, SampleKek);
            var reader = upgradedServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("be02-tests");
            AssertEx.Equal(payload, reader.Unprotect(protectedPayload));
        }
        finally
        {
            DeleteTempDirectory(ringDirectory);
        }
    }

    private static ServiceProvider BuildServiceProvider(byte[] kek)
    {
        var services = new ServiceCollection();
        services.AddSingleton<INodeAeadCipher, AesGcmNodeAeadCipher>();
        services.AddSingleton<INodeDataProtectionKeyProvider>(_ => new FixedKeyProvider(kek));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BuildDataProtectionServices(string ringDirectory, byte[]? kek)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INodeAeadCipher, AesGcmNodeAeadCipher>();

        var builder = services.AddDataProtection()
                              .SetApplicationName("be02-tests")
                              .PersistKeysToFileSystem(new DirectoryInfo(ringDirectory));

        if (kek is not null)
        {
            services.AddSingleton<INodeDataProtectionKeyProvider>(_ => new FixedKeyProvider(kek));
            services.AddSingleton<NodeDataProtectionKeyRingEncryptor>();
            builder.Services.AddOptions<KeyManagementOptions>()
                   .Configure<NodeDataProtectionKeyRingEncryptor>((options, encryptor) => options.XmlEncryptor = encryptor);

            // Mirror the production wiring: decorate the default key resolver so an undecryptable encrypted key
            // hard-fails instead of silently regenerating the ring.
            var defaultKeyResolver = builder.Services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IDefaultKeyResolver));
            if (defaultKeyResolver?.ImplementationType is { } innerResolverType)
            {
                builder.Services.Remove(defaultKeyResolver);
                builder.Services.AddSingleton<IDefaultKeyResolver>(serviceProvider =>
                    new NodeDataProtectionKeyRingFailClosedKeyResolver((IDefaultKeyResolver)ActivatorUtilities.CreateInstance(serviceProvider, innerResolverType)));
            }
        }

        return services.BuildServiceProvider();
    }

    private static IConfiguration BuildOperatorSecretConfiguration()
    {
        var operatorSecret = Enumerable.Range(start: 1, count: 32).Select(static value => (byte)value).ToArray();
        return new ConfigurationBuilder()
               .AddInMemoryCollection(new Dictionary<string, string?>
               {
                   [NodeOperatorSecretProvider.EnvVarName] = Convert.ToBase64String(operatorSecret)
               })
               .Build();
    }

    private static bool ChainContains(Exception exception, Func<Exception, bool> predicate)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (predicate(current))
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"be02-dpring-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already gone — nothing to clean up.
        }
    }

    private sealed class FixedKeyProvider : INodeDataProtectionKeyProvider
    {
        private readonly byte[] _key;

        public FixedKeyProvider(byte[] key)
        {
            _key = key;
        }

        public ReadOnlyMemory<byte> Key => _key;

        public void Dispose()
        {
            // Test stub over a caller-owned buffer; nothing to release.
        }
    }
}
