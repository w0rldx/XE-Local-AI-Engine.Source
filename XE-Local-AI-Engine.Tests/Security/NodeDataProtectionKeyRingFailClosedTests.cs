namespace XE_Local_AI_Engine.Tests.Security;

using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption;
using Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;
using Microsoft.Extensions.DependencyInjection;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Client.Persistence.Cryptography;
using XE_Local_AI_Engine.Client.Security.DataProtection;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The fail-closed key-ring resolver, on BOTH at-rest schemes.
///     <para>
///         The defect: the decorator was registered on the non-Windows branch only, so on Windows an unreadable DPAPI
///         ring silently minted a new key and orphaned every <c>*.enc</c> credential — the HF token, GitHub auth, cloud
///         credentials — with no hard failure and no log line. The Linux backstop existed precisely to stop that, and
///         Windows did not have it.
///     </para>
///     <para>
///         Everything here runs on this Linux host because the OS decision is now a PARAMETER
///         (<see cref="NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor" />) rather than a branch only a Windows
///         machine can reach. Which is the whole point: the branch that was wrong is the branch that had no coverage.
///     </para>
///     <para>
///         This is a fail-CLOSED change on a startup path, so the tests that matter most are the ones proving it stays
///         quiet: an expiring-but-readable ring, an unrelated failure, a revoked key, and a resolved default all have to
///         behave exactly as before.
///     </para>
/// </summary>
public sealed class NodeDataProtectionKeyRingFailClosedTests
{
    [Test]
    public void DpapiRing_WhenAKeyCannotBeUnwrapped_RefusesToRegenerateInsteadOfOrphaningEveryCredential()
    {
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: true)(new FakeInnerResolver(shouldGenerateNewKey: true));

        // What ProtectedData.Unprotect raises when the blob is not readable for the current Windows user, wrapped the
        // way the framework wraps a decryptor failure before it reaches the resolver.
        var failure = new InvalidOperationException("could not materialise key",
            new CryptographicException("The data is invalid."));

        var thrown = Throws<InvalidOperationException>(() => resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow, [new FakeKey(failure)]));

        AssertEx.Contains(thrown.Message, "Refusing to regenerate the key-ring");

        // The remediation genuinely differs from the operator-secret one: a DPAPI CurrentUser blob is bound to the
        // Windows account, so there is no secret to restore.
        AssertEx.Contains(thrown.Message, "DPAPI-protected for the Windows user");
        AssertEx.Contains(thrown.Message, "dp-keys");
    }

    [Test]
    public void NonWindowsRing_StaysNarrowAndDoesNotClaimAPlainCryptographicFailure()
    {
        // The BE-02 classifier recognises only this node's own decryption exception, so an unrelated cryptographic
        // failure is still left to the framework's own handling rather than reported as a KEK problem.
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: false)(new FakeInnerResolver(shouldGenerateNewKey: true));

        var resolution = resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow,
            [new FakeKey(new CryptographicException("The data is invalid."))]);

        AssertEx.True(resolution.ShouldGenerateNewKey);
    }

    [Test]
    public void NonWindowsRing_StillFailsClosedOnItsOwnDecryptionException()
    {
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: false)(new FakeInnerResolver(shouldGenerateNewKey: true));

        var failure = new InvalidOperationException("could not materialise key",
            new NodeDataProtectionKeyRingDecryptionException("wrong KEK"));

        var thrown = Throws<InvalidOperationException>(() => resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow, [new FakeKey(failure)]));

        AssertEx.Contains(thrown.Message, "Restore the correct operator secret");
    }

    /// <summary>
    ///     The ordinary reason a resolver asks for a new key: the ring is perfectly readable, its current key is simply
    ///     due for rotation. Turning THAT into a startup failure would brick every correct install on its rotation day.
    /// </summary>
    [Test]
    public void DpapiRing_WhenTheRingIsReadableButDueForRotation_StillRegenerates()
    {
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: true)(new FakeInnerResolver(shouldGenerateNewKey: true));

        var resolution = resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow, [new FakeKey(failure: null)]);

        AssertEx.True(resolution.ShouldGenerateNewKey);
    }

    [Test]
    public void DpapiRing_WhenTheRingIsEmpty_RegeneratesAsAFirstRunMust()
    {
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: true)(new FakeInnerResolver(shouldGenerateNewKey: true));

        var resolution = resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow, []);

        AssertEx.True(resolution.ShouldGenerateNewKey);
    }

    [Test]
    public void DpapiRing_IgnoresARevokedKeyBecauseItsIneligibilityWasDeliberate()
    {
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: true)(new FakeInnerResolver(shouldGenerateNewKey: true));

        var resolution = resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow,
            [new FakeKey(new CryptographicException("The data is invalid."), isRevoked: true)]);

        AssertEx.True(resolution.ShouldGenerateNewKey);
    }

    [Test]
    public void DpapiRing_WhenTheInnerResolverResolvedADefault_NeverProbesAKeyAtAll()
    {
        var resolver = NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: true)(new FakeInnerResolver(shouldGenerateNewKey: false));
        var key = new FakeKey(new CryptographicException("The data is invalid."));

        var resolution = resolver.ResolveDefaultKeyPolicy(DateTimeOffset.UtcNow, [key]);

        AssertEx.False(resolution.ShouldGenerateNewKey);
        AssertEx.Equal(expected: 0, key.EncryptorAttempts, "the resolved-default hot path must stay untouched");
    }

    [Test]
    public void Decorate_ReplacesTheFrameworkResolverOnEitherScheme()
    {
        foreach (var isWindows in new[] { true, false })
        {
            var services = new ServiceCollection();
            services.AddSingleton<IDefaultKeyResolver, PlaceholderResolver>();

            var decorated = NodeDataProtectionKeyRingFailClosed.Decorate(services, NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows));

            AssertEx.True(decorated, "the decoration must be applied regardless of the at-rest scheme");
            using var provider = services.BuildServiceProvider();
            _ = AssertEx.NotNull(provider.GetRequiredService<IDefaultKeyResolver>() as NodeDataProtectionKeyRingFailClosedKeyResolver);
        }
    }

    /// <summary>
    ///     The guard that keeps a fail-closed startup change from ever bricking a correct install: if Data Protection
    ///     stops registering its resolver by implementation type there is no inner instance to construct, so the
    ///     framework's pre-existing behaviour stands rather than the host failing to start.
    /// </summary>
    [Test]
    public void Decorate_WhenTheFrameworkStopsRegisteringByImplementationType_LeavesTheRegistrationAlone()
    {
        var services = new ServiceCollection();
        var original = new PlaceholderResolver();
        services.AddSingleton<IDefaultKeyResolver>(original);

        var decorated = NodeDataProtectionKeyRingFailClosed.Decorate(services, NodeDataProtectionKeyRingFailClosed.ResolverFactoryFor(isWindows: true));

        AssertEx.False(decorated);
        using var provider = services.BuildServiceProvider();
        AssertEx.Equal<object>(original, provider.GetRequiredService<IDefaultKeyResolver>());
    }

    private static TException Throws<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }

        throw new AssertionException($"Expected {typeof(TException).Name} but nothing was thrown.");
    }

    private sealed class FakeInnerResolver : IDefaultKeyResolver
    {
        private readonly bool _shouldGenerateNewKey;

        public FakeInnerResolver(bool shouldGenerateNewKey)
        {
            _shouldGenerateNewKey = shouldGenerateNewKey;
        }

        public DefaultKeyResolution ResolveDefaultKeyPolicy(DateTimeOffset now, IEnumerable<IKey> allKeys)
        {
            return new DefaultKeyResolution
            {
                ShouldGenerateNewKey = _shouldGenerateNewKey
            };
        }
    }

    private sealed class PlaceholderResolver : IDefaultKeyResolver
    {
        public DefaultKeyResolution ResolveDefaultKeyPolicy(DateTimeOffset now, IEnumerable<IKey> allKeys)
        {
            return default;
        }
    }

    /// <summary>A ring key whose encryptor materialisation either succeeds or raises the supplied failure.</summary>
    private sealed class FakeKey : IKey
    {
        private readonly Exception? _failure;

        public FakeKey(Exception? failure, bool isRevoked = false)
        {
            _failure = failure;
            IsRevoked = isRevoked;
        }

        public int EncryptorAttempts { get; private set; }

        public Guid KeyId { get; } = Guid.NewGuid();

        public DateTimeOffset CreationDate { get; } = DateTimeOffset.UtcNow.AddDays(-30);

        public DateTimeOffset ActivationDate { get; } = DateTimeOffset.UtcNow.AddDays(-30);

        public DateTimeOffset ExpirationDate { get; } = DateTimeOffset.UtcNow.AddDays(60);

        public bool IsRevoked { get; }

        public IAuthenticatedEncryptorDescriptor Descriptor => throw new NotSupportedException();

        public IAuthenticatedEncryptor CreateEncryptor()
        {
            EncryptorAttempts++;
            return _failure is null ? new NoopEncryptor() : throw _failure;
        }
    }

    private sealed class NoopEncryptor : IAuthenticatedEncryptor
    {
        public byte[] Decrypt(ArraySegment<byte> ciphertext, ArraySegment<byte> additionalAuthenticatedData) => [];

        public byte[] Encrypt(ArraySegment<byte> plaintext, ArraySegment<byte> additionalAuthenticatedData) => [];
    }
}
