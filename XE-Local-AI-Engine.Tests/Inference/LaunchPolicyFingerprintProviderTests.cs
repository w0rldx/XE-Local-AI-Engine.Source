namespace XE_Local_AI_Engine.Tests.Inference;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LaunchPolicyFingerprintProviderTests : IDisposable
{
    // One cache per provider: several tests assert an absolute full-hash count, so a provider must start cold.
    private readonly List<LaunchPolicyFileHashCache> _fileHashCaches = [];

    public void Dispose()
    {
        foreach (var cache in _fileHashCaches)
        {
            cache.Dispose();
        }
    }

    [Test]
    public async Task CaptureAsync_SameRuntimeModelRevisionAndLaunchSemantics_IsStable()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var provider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var input = Input(path);

            var first = await provider.CaptureAsync(input, CancellationToken.None);
            var fullHashCountAfterFirstCapture = provider.FullFileHashComputationCount;
            var second = await provider.CaptureAsync(input, CancellationToken.None);

            AssertEx.Equal(LaunchPolicyFingerprintProvider.CurrentVersion, first.Version);
            AssertEx.Equal(expected: 5, first.Version);
            AssertEx.Equal(first, second);
            AssertEx.Equal(129, first.Value.Length);
            AssertEx.Equal(expected: 2L, fullHashCountAfterFirstCapture);
            AssertEx.Equal(fullHashCountAfterFirstCapture, provider.FullFileHashComputationCount);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task Fingerprint_VersionBump_InvalidatesAndRefits()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var provider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var captured = await provider.CaptureAsync(Input(path), CancellationToken.None);

            // A profile frozen under the PREVIOUS version is hard-rejected on version alone, whatever its value says —
            // it cannot prove whether an adapter was applied, because adapters did not exist when it was written.
            var stale = new LaunchPolicyFingerprint(LaunchPolicyFingerprintProvider.CurrentVersion - 1, captured.Value);
            var staleMatches = await provider.MatchesAsync(Profile(Input(path), stale), path, CancellationToken.None);
            AssertEx.False(staleMatches, "A fingerprint from before the adapter input joined must not match.");

            // Re-capturing (the one-time re-fit) produces a current-version fingerprint that does match.
            var refit = await provider.CaptureAsync(Input(path), CancellationToken.None);
            AssertEx.Equal(LaunchPolicyFingerprintProvider.CurrentVersion, refit.Version);
            AssertEx.True(await provider.MatchesAsync(Profile(Input(path), refit), path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_AdapterMember_ChangesTheFingerprint()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            // The sha only selects the identity branch; both providers use the same one so the adapter member is the
            // single difference between the two captures.
            var plain = RegistryEntry(path, new string('a', 64));
            var adapter = plain with
            {
                AdapterFileName = "tuned-adapter.gguf",
                AdapterSha256 = new string('c', 64),
                AdapterSizeBytes = 4096,
                AdapterMemberFingerprint = $"sha256:{new string('c', 64)}:4096",
                BaseModelName = "bartowski/Base-GGUF:Q4_K_M"
            };

            var plainProvider = BuildProvider(Runtime("runtime-sha"), binaryPath, registryEntry: plain);
            var adapterProvider = BuildProvider(Runtime("runtime-sha"), binaryPath, registryEntry: adapter);

            var withoutAdapter = await plainProvider.CaptureAsync(Input(path), CancellationToken.None);
            var withAdapter = await adapterProvider.CaptureAsync(Input(path), CancellationToken.None);

            AssertEx.NotEqual(withoutAdapter.Value, withAdapter.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task MatchesAsync_ColdProvider_UsesValidationStampWithoutFullFileHashes()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var captureProvider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var captured = await captureProvider.CaptureAsync(Input(path), CancellationToken.None);
            AssertEx.Equal(expected: 2L, captureProvider.FullFileHashComputationCount);

            var coldValidationProvider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var matches = await coldValidationProvider.MatchesAsync(Profile(Input(path), captured),
                path,
                CancellationToken.None);

            AssertEx.True(matches);
            AssertEx.Equal(expected: 0L,
                coldValidationProvider.FullFileHashComputationCount,
                "cold-spawn validation must use metadata/guard samples and never stream the model or runtime bundle");
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task MatchesAsync_CrossSplicedStrongAndValidationHashes_IsRejected()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var firstProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var first = await firstProvider.CaptureAsync(Input(path), CancellationToken.None);

            var secondProvider = BuildProvider(Runtime("runtime-b"), binaryPath);
            var second = await secondProvider.CaptureAsync(Input(path), CancellationToken.None);
            var crossSpliced = new LaunchPolicyFingerprint(LaunchPolicyFingerprintProvider.CurrentVersion,
                string.Concat(first.Value.AsSpan(0, 64), ".", second.Value.AsSpan(65)));

            var matches = await secondProvider.MatchesAsync(Profile(Input(path), crossSpliced),
                path,
                CancellationToken.None);

            AssertEx.False(matches,
                "the cheap validation half must be cryptographically bound to the persisted strong identity");
            AssertEx.Equal(expected: 2L,
                secondProvider.FullFileHashComputationCount,
                "matching must reject a cross-spliced identity without adding another whole-file hash");
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_WhenModelChangesBetweenStrongAndValidationPasses_RetriesStableSnapshot()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var registryLookupCount = 0;
            var mutatingRegistry = Substitute.For<IGgufModelRegistry>();
            mutatingRegistry.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                            .Returns(async call =>
                            {
                                if (Interlocked.Increment(ref registryLookupCount) == 2)
                                {
                                    await File.WriteAllTextAsync(path,
                                        "revision-2",
                                        call.ArgAt<CancellationToken>(1));
                                    File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(1));
                                }

                                return null;
                            });

            var mutatingProvider = BuildProvider(Runtime("runtime-sha"),
                binaryPath,
                modelRegistryOverride: mutatingRegistry);
            var captured = await mutatingProvider.CaptureAsync(Input(path), CancellationToken.None);

            var stableProvider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var stable = await stableProvider.CaptureAsync(Input(path), CancellationToken.None);

            AssertEx.Equal(stable,
                captured,
                "capture must retry instead of persisting a strong hash from one file revision with a validation stamp from another");
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_VerifiedRegistrySha256_DoesNotReadWholeModelFile()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var provider = BuildProvider(Runtime("runtime-sha"),
                binaryPath,
                registryEntry: RegistryEntry(path, new string('a', 64)));

            _ = await provider.CaptureAsync(Input(path), CancellationToken.None);

            AssertEx.Equal(expected: 1L,
                provider.FullFileHashComputationCount,
                "only the selected runtime file should require a full hash; the verified GGUF registry identity is reused");
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_RuntimeOrModelRevisionChange_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path);
            var firstProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var runtimeChangedProvider = BuildProvider(Runtime("runtime-b"), binaryPath);
            var first = await firstProvider.CaptureAsync(input, CancellationToken.None);
            var runtimeChanged = await runtimeChangedProvider.CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(binaryPath, "binary-revision-2");
            var binaryChangedProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var binaryChanged = await binaryChangedProvider.CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(path, "revision-2");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var modelChangedProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var modelChanged = await modelChangedProvider.CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(first.Value, runtimeChanged.Value);
            AssertEx.NotEqual(first.Value, binaryChanged.Value);
            AssertEx.NotEqual(first.Value, modelChanged.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_ModelBytesChangeWithSameLengthAndTimestamp_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var provider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var input = Input(path);
            var originalTimestamp = File.GetLastWriteTimeUtc(path);
            var originalLength = new FileInfo(path).Length;
            var first = await provider.CaptureAsync(input, CancellationToken.None);
            var fullHashCountAfterFirstCapture = provider.FullFileHashComputationCount;

            await File.WriteAllTextAsync(path, "revision-2");
            File.SetLastWriteTimeUtc(path, originalTimestamp);
            var replacementLength = new FileInfo(path).Length;
            var replacement = await provider.CaptureAsync(input, CancellationToken.None);

            AssertEx.Equal(originalLength, replacementLength);
            AssertEx.Equal(originalTimestamp, File.GetLastWriteTimeUtc(path));
            AssertEx.NotEqual(first.Value, replacement.Value);
            AssertEx.Equal(fullHashCountAfterFirstCapture + 1, provider.FullFileHashComputationCount);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_ActiveChatCacheAndSpeculativeSettingsChange_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path) with
            {
                Role = (int)ModelRole.Chat
            };
            var baselineProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                supervisorOptions: new LlamaServerSupervisorOptions
                {
                    ChatCacheReuse = 256,
                    SpeculativeMode = "ngram-simple"
                });
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);
            var cacheChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                supervisorOptions: new LlamaServerSupervisorOptions
                {
                    ChatCacheReuse = 512,
                    SpeculativeMode = "ngram-simple"
                });
            var cacheChanged = await cacheChangedProvider.CaptureAsync(input, CancellationToken.None);
            var modeChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                supervisorOptions: new LlamaServerSupervisorOptions
                {
                    ChatCacheReuse = 256,
                    SpeculativeMode = "ngram-cache"
                });
            var modeChanged = await modeChangedProvider.CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(baseline.Value, cacheChanged.Value);
            AssertEx.NotEqual(baseline.Value, modeChanged.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_ResolvedDraftModelBytesOrDraftFlagsChange_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var draftPath = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path) with
            {
                Role = (int)ModelRole.Chat
            };
            var options = new LlamaServerSupervisorOptions
            {
                SpeculativeMode = "draft-simple",
                SpeculativeDraftModelName = "draft-model",
                SpeculativeDraftMaxTokens = 3,
                SpeculativeDraftGpuLayers = 12
            };
            var baselineProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                supervisorOptions: options,
                resolvedDraftPath: draftPath);
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);
            var originalTimestamp = File.GetLastWriteTimeUtc(draftPath);

            await File.WriteAllTextAsync(draftPath, "revision-2");
            File.SetLastWriteTimeUtc(draftPath, originalTimestamp);
            var draftChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                supervisorOptions: options,
                resolvedDraftPath: draftPath);
            var draftBytesChanged = await draftChangedProvider.CaptureAsync(input, CancellationToken.None);
            var flagsChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                supervisorOptions: new LlamaServerSupervisorOptions
                {
                    SpeculativeMode = "draft-simple",
                    SpeculativeDraftModelName = "draft-model",
                    SpeculativeDraftMaxTokens = 5,
                    SpeculativeDraftGpuLayers = 24
                },
                resolvedDraftPath: draftPath);
            var flagsChanged = await flagsChangedProvider.CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(baseline.Value, draftBytesChanged.Value);
            AssertEx.NotEqual(draftBytesChanged.Value, flagsChanged.Value);
        }
        finally
        {
            File.Delete(path);
            File.Delete(draftPath);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_CpuThreadPolicyOrResolvedCountsChange_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path) with
            {
                Backend = "cpu"
            };
            var baselineProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    AssumeSimultaneousMultithreading = true,
                    CpuThreadReserve = 1,
                    CpuThreadCount = 3,
                    CpuThreadsBatchCount = 4
                });
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);
            var policyChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    AssumeSimultaneousMultithreading = false,
                    CpuThreadReserve = 2,
                    CpuThreadCount = 3,
                    CpuThreadsBatchCount = 4
                });
            var policyChanged = await policyChangedProvider.CaptureAsync(input, CancellationToken.None);
            var countsChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    AssumeSimultaneousMultithreading = true,
                    CpuThreadReserve = 1,
                    CpuThreadCount = 5,
                    CpuThreadsBatchCount = 6
                });
            var resolvedCountsChanged = await countsChangedProvider.CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(baseline.Value, policyChanged.Value);
            AssertEx.NotEqual(baseline.Value, resolvedCountsChanged.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_AuxiliaryRoleContextOrSafetyMarginChange_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path);
            var baselineProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 2048,
                    RerankerContextTokens = 2048,
                    ContextSafetyMarginTokens = 256
                });
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);

            var embeddingChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 4096,
                    RerankerContextTokens = 2048,
                    ContextSafetyMarginTokens = 256
                });
            var embeddingChanged = await embeddingChangedProvider.CaptureAsync(input, CancellationToken.None);

            var marginChangedProvider = BuildProvider(Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 2048,
                    RerankerContextTokens = 2048,
                    ContextSafetyMarginTokens = 512
                });
            var marginChanged = await marginChangedProvider.CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(baseline.Value, embeddingChanged.Value);
            AssertEx.NotEqual(baseline.Value, marginChanged.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_OperatorOverride_IgnoresDormantInstalledRuntimeButHashesSelectedExecutable()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path);
            var firstProvider = BuildProvider(Runtime("runtime-a"), binaryPath, "override");
            var dormantRuntimeProvider = BuildProvider(Runtime("runtime-b"), binaryPath, "override");
            var first = await firstProvider.CaptureAsync(input, CancellationToken.None);
            var dormantRuntimeChanged = await dormantRuntimeProvider.CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(binaryPath, "override-revision-2");
            var executableChangedProvider = BuildProvider(Runtime("runtime-b"), binaryPath, "override");
            var executableChanged = await executableChangedProvider.CaptureAsync(input, CancellationToken.None);

            AssertEx.Equal(first, dormantRuntimeChanged);
            AssertEx.NotEqual(first.Value, executableChanged.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task CaptureAsync_RuntimeSiblingImplementationChange_ChangesIdentity()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        var implementationPath = Path.Combine(Path.GetDirectoryName(binaryPath)!, "libllama-server-impl.so");
        try
        {
            await File.WriteAllTextAsync(implementationPath, "implementation-revision-1");
            var input = Input(path);
            var provider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var first = await provider.CaptureAsync(input, CancellationToken.None);
            var fullHashCountAfterFirstCapture = provider.FullFileHashComputationCount;

            await File.AppendAllTextAsync(implementationPath, "implementation-revision-2");
            var implementationChanged = await provider.CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(first.Value, implementationChanged.Value);
            AssertEx.Equal(fullHashCountAfterFirstCapture + 1, provider.FullFileHashComputationCount);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    // ---- S1 / D13: the node's SELECTED KV-cache type is part of a frozen profile's identity ----

    [Test]
    public async Task Fingerprint_WithTheDefaultKvCacheType_IsByteIdenticalToTheUnseededOptions()
    {
        // Byte-identical default. The knob is FOLDED at q8_0, so a node that never touched it hashes exactly the bytes
        // it has always hashed and shipping this slice invalidates no stored profile. If the seed default and the
        // provider default ever drift apart, this fails loudly instead of silently staling every profile on every node.
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var unseeded = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var seededFromUnsetSettings = BuildProvider(Runtime("runtime-sha"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    KvCacheType = StoredNodeSettings.DefaultKvCacheType,
                    EnableGpuKvCacheQuantization = true
                });

            var withProviderDefault = await unseeded.CaptureAsync(Input(path), CancellationToken.None);
            var withSeededDefault = await seededFromUnsetSettings.CaptureAsync(Input(path), CancellationToken.None);

            AssertEx.Equal(withProviderDefault.Value, withSeededDefault.Value);
            AssertEx.Equal(expected: 5, withProviderDefault.Version);
            AssertEx.Equal(expected: 5, LaunchPolicyFingerprintProvider.CurrentVersion);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task Fingerprint_ChangesWithTheSelectedKvCacheType()
    {
        // Never inert, never sticky (D13): switching the knob in EITHER direction moves the hash, so axis (b) stales a
        // frozen profile explored under the other type and it re-explores before it can replay.
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var q8 = await CaptureWithKvCacheTypeAsync(binaryPath, path, LlamaServerKvCacheTypes.Q8_0);
            var f16 = await CaptureWithKvCacheTypeAsync(binaryPath, path, LlamaServerKvCacheTypes.F16);
            var q4 = await CaptureWithKvCacheTypeAsync(binaryPath, path, LlamaServerKvCacheTypes.Q4_0);
            var providerDefault = await BuildProvider(Runtime("runtime-sha"), binaryPath).CaptureAsync(Input(path), CancellationToken.None);

            AssertEx.Equal(providerDefault.Value, q8);
            AssertEx.False(string.Equals(q8, f16, StringComparison.Ordinal), "f16 must not hash as the q8_0 default.");
            AssertEx.False(string.Equals(q8, q4, StringComparison.Ordinal), "q4_0 must not hash as the q8_0 default.");
            AssertEx.False(string.Equals(f16, q4, StringComparison.Ordinal), "f16 and q4_0 must not hash alike.");

            // What staleness actually reads: a profile frozen under q8_0 stops matching once the operator selects
            // q4_0, and matches again the moment they switch back. The rule is symmetric, which is what "never
            // sticky" means.
            var frozenUnderQ8 = Profile(Input(path), await BuildProvider(Runtime("runtime-sha"), binaryPath).CaptureAsync(Input(path), CancellationToken.None));
            AssertEx.False(await ProviderWithKvCacheType(binaryPath, LlamaServerKvCacheTypes.Q4_0).MatchesAsync(frozenUnderQ8, path, CancellationToken.None),
                "A q8_0-frozen profile must not match once q4_0 is selected.");
            AssertEx.True(await ProviderWithKvCacheType(binaryPath, LlamaServerKvCacheTypes.Q8_0).MatchesAsync(frozenUnderQ8, path, CancellationToken.None),
                "Switching back to q8_0 must make the profile valid again.");
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    [Test]
    public async Task Fingerprint_OnACpuBackend_IgnoresTheKvCacheType()
    {
        // A CPU spawn never quantizes KV, so a CPU-backend profile must not go stale for a knob that cannot reach it.
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var cpuInput = Input(path) with { Backend = "cpu" };
            var cpuRuntime = Runtime("runtime-sha") with { Variant = GpuVariant.Cpu };
            var withDefault = await BuildProvider(cpuRuntime, binaryPath).CaptureAsync(cpuInput, CancellationToken.None);
            var withQ4 = await BuildProvider(cpuRuntime,
                    binaryPath,
                    launchPolicyOptions: new LlamaServerLaunchPolicyOptions { KvCacheType = LlamaServerKvCacheTypes.Q4_0 })
                .CaptureAsync(cpuInput, CancellationToken.None);

            AssertEx.Equal(withDefault.Value, withQ4.Value);
        }
        finally
        {
            File.Delete(path);
            DeleteBinaryDirectory(binaryPath);
        }
    }

    // Builds a provider with the options the DI seed would produce for one selected KV type: f16 collapses the
    // quantization flag, exactly as BuildSeededLlamaServerLaunchPolicyOptions does.
    private LaunchPolicyFingerprintProvider ProviderWithKvCacheType(string binaryPath, string kvCacheType)
    {
        return BuildProvider(Runtime("runtime-sha"),
            binaryPath,
            launchPolicyOptions: new LlamaServerLaunchPolicyOptions
            {
                KvCacheType = kvCacheType,
                EnableGpuKvCacheQuantization = !string.Equals(kvCacheType, LlamaServerKvCacheTypes.F16, StringComparison.Ordinal)
            });
    }

    private async Task<string> CaptureWithKvCacheTypeAsync(string binaryPath, string modelPath, string kvCacheType)
    {
        var fingerprint = await ProviderWithKvCacheType(binaryPath, kvCacheType).CaptureAsync(Input(modelPath), CancellationToken.None);
        return fingerprint.Value;
    }

    private LaunchPolicyFingerprintProvider BuildProvider(InstalledRuntimeState runtime,
        string binaryPath,
        string? binaryVersion = null,
        LlamaServerSupervisorOptions? supervisorOptions = null,
        LlamaServerLaunchPolicyOptions? launchPolicyOptions = null,
        string? resolvedDraftPath = null,
        GgufModelRegistryEntry? registryEntry = null,
        IGgufModelRegistry? modelRegistryOverride = null)
    {
        var store = Substitute.For<IInstalledRuntimeStore>();
        store.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<InstalledRuntimeState?>(runtime));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(new LlamaBinary(binaryPath,
                         binaryVersion ?? runtime.Tag,
                         runtime.Variant,
                         IsPinnedFallback: false)));
        var modelStore = Substitute.For<IGgufModelStore>();
        modelStore.ResolveModelFilePathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                  .Returns(Task.FromResult(resolvedDraftPath));
        var modelRegistry = modelRegistryOverride ?? Substitute.For<IGgufModelRegistry>();
        if (modelRegistryOverride is null)
        {
            modelRegistry.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                         .Returns(Task.FromResult(registryEntry));
        }

        return new LaunchPolicyFingerprintProvider(store,
            binaryManager,
            modelStore,
            modelRegistry,
            supervisorOptions ?? new LlamaServerSupervisorOptions(),
            launchPolicyOptions ?? new LlamaServerLaunchPolicyOptions(),
            NewFileHashCache());
    }

    private LaunchPolicyFileHashCache NewFileHashCache()
    {
        var cache = new LaunchPolicyFileHashCache();
        _fileHashCaches.Add(cache);
        return cache;
    }

    private static InstalledRuntimeState Runtime(string sha)
    {
        return new InstalledRuntimeState("b9999",
            "llama.zip",
            sha,
            GpuVariant.Cuda,
            DateTimeOffset.UnixEpoch);
    }

    private static InferenceProfileFingerprintInput Input(string path)
    {
        return new InferenceProfileFingerprintInput("bartowski/Model-GGUF:Q4_K_M",
            (int)ModelRole.Embedding,
            "cuda",
            path,
            CtxSize: 2048,
            NGpuLayers: 33,
            TensorSplit: null,
            OverrideTensor: null,
            KvTypeK: "q8_0",
            KvTypeV: "q8_0",
            FlashAttn: true);
    }

    private static GgufModelRegistryEntry RegistryEntry(string path, string sha256)
    {
        return new GgufModelRegistryEntry
        {
            ModelName = "bartowski/Model-GGUF:Q4_K_M",
            RepoId = "bartowski/Model-GGUF",
            FileName = Path.GetFileName(path),
            Quant = "Q4_K_M",
            LocalPath = path,
            SizeBytes = new FileInfo(path).Length,
            Sha256 = sha256,
            SourceRevision = "revision",
            DownloadedAtUtc = DateTimeOffset.UnixEpoch
        };
    }

    private static InferenceProfileRecord Profile(InferenceProfileFingerprintInput input,
        LaunchPolicyFingerprint fingerprint)
    {
        return new InferenceProfileRecord(Guid.NewGuid(),
            MachineKey: "machine",
            input.ModelName,
            input.Role,
            input.Backend,
            LlamacppBuild: "b9999",
            Quant: "Q4_K_M",
            input.CtxSize,
            input.NGpuLayers,
            input.TensorSplit,
            input.OverrideTensor,
            input.KvTypeK,
            input.KvTypeV,
            input.FlashAttn,
            NParams: 1,
            IsMoe: false,
            ExpertCount: null,
            GlobalFreeVramAtFreezeBytes: null,
            Status: InferenceProfileStatus.Frozen,
            BenchmarkSnapshotId: Guid.NewGuid(),
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0,
            LaunchPolicyFingerprintVersion: fingerprint.Version,
            LaunchPolicyFingerprint: fingerprint.Value);
    }

    private static async Task<string> CreateModelFileAsync()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, "revision-1");
        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch);
        return path;
    }

    private static async Task<string> CreateBinaryFileAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server");
        await File.WriteAllTextAsync(path, "binary-revision-1");
        return path;
    }

    private static void DeleteBinaryDirectory(string binaryPath)
    {
        var directory = Path.GetDirectoryName(binaryPath);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
