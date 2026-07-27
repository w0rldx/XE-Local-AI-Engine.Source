namespace XE_Local_AI_Engine.Tests.Inference;

using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LaunchPolicyFingerprintProviderTests
{
    [Test]
    public async Task CaptureAsync_SameRuntimeModelRevisionAndLaunchSemantics_IsStable()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            using var provider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var input = Input(path);

            var first = await provider.CaptureAsync(input, CancellationToken.None);
            var fullHashCountAfterFirstCapture = provider.FullFileHashComputationCount;
            var second = await provider.CaptureAsync(input, CancellationToken.None);

            AssertEx.Equal(LaunchPolicyFingerprintProvider.CurrentVersion, first.Version);
            AssertEx.Equal(expected: 4, first.Version);
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
    public async Task MatchesAsync_ColdProvider_UsesValidationStampWithoutFullFileHashes()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            LaunchPolicyFingerprint captured;
            using (var captureProvider = BuildProvider(Runtime("runtime-sha"), binaryPath))
            {
                captured = await captureProvider.CaptureAsync(Input(path), CancellationToken.None);
                AssertEx.Equal(expected: 2L, captureProvider.FullFileHashComputationCount);
            }

            using var coldValidationProvider = BuildProvider(Runtime("runtime-sha"), binaryPath);
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
            using var firstProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var first = await firstProvider.CaptureAsync(Input(path), CancellationToken.None);

            using var secondProvider = BuildProvider(Runtime("runtime-b"), binaryPath);
            var second = await secondProvider.CaptureAsync(Input(path), CancellationToken.None);
            var crossSpliced = new LaunchPolicyFingerprint(
                LaunchPolicyFingerprintProvider.CurrentVersion,
                string.Concat(first.Value.AsSpan(0, 64), ".", second.Value.AsSpan(65)));

            var matches = await secondProvider.MatchesAsync(
                Profile(Input(path), crossSpliced),
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
                                    await File.WriteAllTextAsync(
                                        path,
                                        "revision-2",
                                        call.ArgAt<CancellationToken>(1));
                                    File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddSeconds(1));
                                }

                                return null;
                            });

            using var mutatingProvider = BuildProvider(
                Runtime("runtime-sha"),
                binaryPath,
                modelRegistryOverride: mutatingRegistry);
            var captured = await mutatingProvider.CaptureAsync(Input(path), CancellationToken.None);

            using var stableProvider = BuildProvider(Runtime("runtime-sha"), binaryPath);
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
            using var provider = BuildProvider(Runtime("runtime-sha"),
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
            using var firstProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            using var runtimeChangedProvider = BuildProvider(Runtime("runtime-b"), binaryPath);
            var first = await firstProvider.CaptureAsync(input, CancellationToken.None);
            var runtimeChanged = await runtimeChangedProvider.CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(binaryPath, "binary-revision-2");
            using var binaryChangedProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
            var binaryChanged = await binaryChangedProvider.CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(path, "revision-2");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            using var modelChangedProvider = BuildProvider(Runtime("runtime-a"), binaryPath);
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
            using var provider = BuildProvider(Runtime("runtime-sha"), binaryPath);
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
            using var baselineProvider = BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: new LlamaServerSupervisorOptions
                    {
                        ChatCacheReuse = 256,
                        SpeculativeMode = "ngram-simple"
                    });
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);
            using var cacheChangedProvider = BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: new LlamaServerSupervisorOptions
                    {
                        ChatCacheReuse = 512,
                        SpeculativeMode = "ngram-simple"
                    });
            var cacheChanged = await cacheChangedProvider.CaptureAsync(input, CancellationToken.None);
            using var modeChangedProvider = BuildProvider(Runtime("runtime-a"),
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
            using var baselineProvider = BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: options,
                    resolvedDraftPath: draftPath);
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);
            var originalTimestamp = File.GetLastWriteTimeUtc(draftPath);

            await File.WriteAllTextAsync(draftPath, "revision-2");
            File.SetLastWriteTimeUtc(draftPath, originalTimestamp);
            using var draftChangedProvider = BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: options,
                    resolvedDraftPath: draftPath);
            var draftBytesChanged = await draftChangedProvider.CaptureAsync(input, CancellationToken.None);
            using var flagsChangedProvider = BuildProvider(Runtime("runtime-a"),
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
            using var baselineProvider = BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                    {
                        AssumeSimultaneousMultithreading = true,
                        CpuThreadReserve = 1,
                        CpuThreadCount = 3,
                        CpuThreadsBatchCount = 4
                    });
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);
            using var policyChangedProvider = BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                    {
                        AssumeSimultaneousMultithreading = false,
                        CpuThreadReserve = 2,
                        CpuThreadCount = 3,
                        CpuThreadsBatchCount = 4
                    });
            var policyChanged = await policyChangedProvider.CaptureAsync(input, CancellationToken.None);
            using var countsChangedProvider = BuildProvider(Runtime("runtime-a"),
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
            using var baselineProvider = BuildProvider(
                Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 2048,
                    RerankerContextTokens = 2048,
                    ContextSafetyMarginTokens = 256
                });
            var baseline = await baselineProvider.CaptureAsync(input, CancellationToken.None);

            using var embeddingChangedProvider = BuildProvider(
                Runtime("runtime-a"),
                binaryPath,
                launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                {
                    EmbeddingContextTokens = 4096,
                    RerankerContextTokens = 2048,
                    ContextSafetyMarginTokens = 256
                });
            var embeddingChanged = await embeddingChangedProvider.CaptureAsync(input, CancellationToken.None);

            using var marginChangedProvider = BuildProvider(
                Runtime("runtime-a"),
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
            using var firstProvider = BuildProvider(Runtime("runtime-a"), binaryPath, "override");
            using var dormantRuntimeProvider = BuildProvider(Runtime("runtime-b"), binaryPath, "override");
            var first = await firstProvider.CaptureAsync(input, CancellationToken.None);
            var dormantRuntimeChanged = await dormantRuntimeProvider.CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(binaryPath, "override-revision-2");
            using var executableChangedProvider = BuildProvider(Runtime("runtime-b"), binaryPath, "override");
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
            using var provider = BuildProvider(Runtime("runtime-a"), binaryPath);
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

    private static LaunchPolicyFingerprintProvider BuildProvider(
        InstalledRuntimeState runtime,
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
            launchPolicyOptions ?? new LlamaServerLaunchPolicyOptions());
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

    private static InferenceProfileRecord Profile(
        InferenceProfileFingerprintInput input,
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
