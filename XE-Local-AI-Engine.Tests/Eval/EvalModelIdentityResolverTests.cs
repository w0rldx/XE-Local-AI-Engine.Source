namespace XE_Local_AI_Engine.Tests.Eval;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Eval;
using XE_Local_AI_Engine.Client.Services.Eval.Implementation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Tests for <see cref="EvalModelIdentityResolver" /> (RAG-03): the weight-identity source folded into the eval
///     fingerprint. It prefers the llama.cpp GGUF registry (content hash, else revision+size+download-time), falls back
///     to the Ollama classification digest, and degrades to the explicit unverified sentinel — a same-name weight swap
///     always changes the verified token.
/// </summary>
public sealed class EvalModelIdentityResolverTests
{
    private const string ModelName = "publisher/Model-GGUF:Q4_K_M";

    [Test]
    public async Task ResolveAsync_WhenGgufHasContentHash_UsesTheHashAndIsVerified()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns(Entry(sha256: "abc123"));
        var resolver = CreateResolver(registry, Substitute.For<IModelClassificationStore>());

        var identity = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.True(identity.IsVerified, "an installed GGUF with a content hash is a verified identity");
        AssertEx.Equal("gguf-sha256:abc123", identity.Token);
    }

    [Test]
    public async Task ResolveAsync_WhenGgufContentHashChanges_ProducesADifferentToken()
    {
        // A same-name re-download that yields different weights (new LFS OID) must change the token.
        var registry = Substitute.For<IGgufModelRegistry>();
        var resolver = CreateResolver(registry, Substitute.For<IModelClassificationStore>());

        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns(Entry(sha256: "before"));
        var before = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns(Entry(sha256: "after"));
        var after = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.NotEqual(before.Token, after.Token);
    }

    [Test]
    public async Task ResolveAsync_WhenGgufHasNoHash_FallsBackToRevisionSizeAndDownloadTime()
    {
        // No LFS OID exposed (revision-pin only): the token is composed from revision + size + download-time, all of
        // which change on a re-download under the same name.
        var registry = Substitute.For<IGgufModelRegistry>();
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>())
                .Returns(Entry(sha256: null, sizeBytes: 4096, revision: "rev-1", downloadedAt: DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)));
        var resolver = CreateResolver(registry, Substitute.For<IModelClassificationStore>());

        var identity = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.True(identity.IsVerified);
        AssertEx.Equal("gguf-rev:rev-1:size:4096:dl:1700000000000", identity.Token);
    }

    [Test]
    public async Task ResolveAsync_WhenNotInGgufRegistry_UsesOllamaClassificationDigest()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns((GgufModelRegistryEntry?)null);
        var classification = Substitute.For<IModelClassificationStore>();
        classification.GetByNameAsync(ModelName, Arg.Any<CancellationToken>()).Returns(Classification(digest: "sha256:deadbeef"));
        var resolver = CreateResolver(registry, classification);

        var identity = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.True(identity.IsVerified, "an Ollama model with a cached content digest is a verified identity");
        AssertEx.Equal("ollama-digest:sha256:deadbeef", identity.Token);
    }

    [Test]
    public async Task ResolveAsync_WhenNoIdentitySourceResolves_ReturnsTheUnverifiedSentinel()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns((GgufModelRegistryEntry?)null);
        var classification = Substitute.For<IModelClassificationStore>();
        classification.GetByNameAsync(ModelName, Arg.Any<CancellationToken>()).Returns((ModelClassificationRecord?)null);
        var resolver = CreateResolver(registry, classification);

        var identity = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.False(identity.IsVerified, "no resolvable identity must not be treated as verified");
        AssertEx.Equal(EvalModelIdentity.UnverifiedToken, identity.Token);
    }

    [Test]
    public async Task ResolveAsync_WhenClassificationDigestIsBlank_ReturnsUnverified()
    {
        // A classification row with no digest (override-only / never probed) is NOT an identity — degrade to unverified.
        var registry = Substitute.For<IGgufModelRegistry>();
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns((GgufModelRegistryEntry?)null);
        var classification = Substitute.For<IModelClassificationStore>();
        classification.GetByNameAsync(ModelName, Arg.Any<CancellationToken>()).Returns(Classification(digest: null));
        var resolver = CreateResolver(registry, classification);

        var identity = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.False(identity.IsVerified);
        AssertEx.Equal(EvalModelIdentity.UnverifiedToken, identity.Token);
    }

    [Test]
    public async Task ResolveAsync_WhenModelNameIsBlank_ReturnsUnverifiedWithoutLookup()
    {
        var registry = Substitute.For<IGgufModelRegistry>();
        var classification = Substitute.For<IModelClassificationStore>();
        var resolver = CreateResolver(registry, classification);

        var identity = await resolver.ResolveAsync("   ").ConfigureAwait(false);

        AssertEx.False(identity.IsVerified);
        AssertEx.Equal(EvalModelIdentity.UnverifiedToken, identity.Token);
        await registry.DidNotReceive().FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await classification.DidNotReceive().GetByNameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ResolveAsync_WhenGgufLookupThrows_FallsThroughToClassificationDigest()
    {
        // A registry failure must never throw out of the resolver — it degrades to the next source.
        var registry = Substitute.For<IGgufModelRegistry>();
        registry.FindAsync(ModelName, Arg.Any<CancellationToken>()).Returns<GgufModelRegistryEntry?>(_ => throw new IOException("manifest unreadable"));
        var classification = Substitute.For<IModelClassificationStore>();
        classification.GetByNameAsync(ModelName, Arg.Any<CancellationToken>()).Returns(Classification(digest: "sha256:cafe"));
        var resolver = CreateResolver(registry, classification);

        var identity = await resolver.ResolveAsync(ModelName).ConfigureAwait(false);

        AssertEx.True(identity.IsVerified);
        AssertEx.Equal("ollama-digest:sha256:cafe", identity.Token);
    }

    private static EvalModelIdentityResolver CreateResolver(IGgufModelRegistry registry, IModelClassificationStore classificationStore)
    {
        return new EvalModelIdentityResolver(registry, classificationStore, NullLogger<EvalModelIdentityResolver>.Instance);
    }

    private static GgufModelRegistryEntry Entry(string? sha256,
        long sizeBytes = 1024,
        string revision = "rev",
        DateTimeOffset? downloadedAt = null)
    {
        return new GgufModelRegistryEntry
        {
            ModelName = ModelName,
            RepoId = "publisher/Model-GGUF",
            FileName = "model.Q4_K_M.gguf",
            Quant = "Q4_K_M",
            LocalPath = "/models/model.Q4_K_M.gguf",
            SizeBytes = sizeBytes,
            Sha256 = sha256,
            SourceRevision = revision,
            DownloadedAtUtc = downloadedAt ?? DateTimeOffset.FromUnixTimeMilliseconds(1_000)
        };
    }

    private static ModelClassificationRecord Classification(string? digest)
    {
        return new ModelClassificationRecord(ModelName,
            digest,
            ModelKind.Chat,
            DetectedCapabilitiesJson: null,
            OverrideKind: null,
            DetectedAtUtc: null,
            UpdatedAtUtc: 0);
    }
}
