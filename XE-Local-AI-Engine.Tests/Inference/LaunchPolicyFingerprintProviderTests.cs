namespace XE_Local_AI_Engine.Tests.Inference;

using NSubstitute;
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
            var provider = BuildProvider(Runtime("runtime-sha"), binaryPath);
            var input = Input(path);

            var first = await provider.CaptureAsync(input, CancellationToken.None);
            var second = await provider.CaptureAsync(input, CancellationToken.None);

            AssertEx.Equal(LaunchPolicyFingerprintProvider.CurrentVersion, first.Version);
            AssertEx.Equal(expected: 3, first.Version);
            AssertEx.Equal(first, second);
            AssertEx.Equal(64, first.Value.Length);
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
            var first = await BuildProvider(Runtime("runtime-a"), binaryPath).CaptureAsync(input, CancellationToken.None);
            var runtimeChanged = await BuildProvider(Runtime("runtime-b"), binaryPath).CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(binaryPath, "binary-revision-2");
            var binaryChanged = await BuildProvider(Runtime("runtime-a"), binaryPath).CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(path, "revision-2");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var modelChanged = await BuildProvider(Runtime("runtime-a"), binaryPath).CaptureAsync(input, CancellationToken.None);

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

            await File.WriteAllTextAsync(path, "revision-2");
            File.SetLastWriteTimeUtc(path, originalTimestamp);
            var replacementLength = new FileInfo(path).Length;
            var replacement = await provider.CaptureAsync(input, CancellationToken.None);

            AssertEx.Equal(originalLength, replacementLength);
            AssertEx.Equal(originalTimestamp, File.GetLastWriteTimeUtc(path));
            AssertEx.NotEqual(first.Value, replacement.Value);
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
            var baseline = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: new LlamaServerSupervisorOptions
                    {
                        ChatCacheReuse = 256,
                        SpeculativeMode = "ngram-simple"
                    })
                .CaptureAsync(input, CancellationToken.None);
            var cacheChanged = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: new LlamaServerSupervisorOptions
                    {
                        ChatCacheReuse = 512,
                        SpeculativeMode = "ngram-simple"
                    })
                .CaptureAsync(input, CancellationToken.None);
            var modeChanged = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: new LlamaServerSupervisorOptions
                    {
                        ChatCacheReuse = 256,
                        SpeculativeMode = "ngram-cache"
                    })
                .CaptureAsync(input, CancellationToken.None);

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
            var baseline = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: options,
                    resolvedDraftPath: draftPath)
                .CaptureAsync(input, CancellationToken.None);
            var originalTimestamp = File.GetLastWriteTimeUtc(draftPath);

            await File.WriteAllTextAsync(draftPath, "revision-2");
            File.SetLastWriteTimeUtc(draftPath, originalTimestamp);
            var draftBytesChanged = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: options,
                    resolvedDraftPath: draftPath)
                .CaptureAsync(input, CancellationToken.None);
            var flagsChanged = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    supervisorOptions: new LlamaServerSupervisorOptions
                    {
                        SpeculativeMode = "draft-simple",
                        SpeculativeDraftModelName = "draft-model",
                        SpeculativeDraftMaxTokens = 5,
                        SpeculativeDraftGpuLayers = 24
                    },
                    resolvedDraftPath: draftPath)
                .CaptureAsync(input, CancellationToken.None);

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
            var baseline = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                    {
                        AssumeSimultaneousMultithreading = true,
                        CpuThreadReserve = 1,
                        CpuThreadCount = 3,
                        CpuThreadsBatchCount = 4
                    })
                .CaptureAsync(input, CancellationToken.None);
            var policyChanged = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                    {
                        AssumeSimultaneousMultithreading = false,
                        CpuThreadReserve = 2,
                        CpuThreadCount = 3,
                        CpuThreadsBatchCount = 4
                    })
                .CaptureAsync(input, CancellationToken.None);
            var resolvedCountsChanged = await BuildProvider(Runtime("runtime-a"),
                    binaryPath,
                    launchPolicyOptions: new LlamaServerLaunchPolicyOptions
                    {
                        AssumeSimultaneousMultithreading = true,
                        CpuThreadReserve = 1,
                        CpuThreadCount = 5,
                        CpuThreadsBatchCount = 6
                    })
                .CaptureAsync(input, CancellationToken.None);

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
    public async Task CaptureAsync_OperatorOverride_IgnoresDormantInstalledRuntimeButHashesSelectedExecutable()
    {
        var path = await CreateModelFileAsync();
        var binaryPath = await CreateBinaryFileAsync();
        try
        {
            var input = Input(path);
            var first = await BuildProvider(Runtime("runtime-a"), binaryPath, "override")
                .CaptureAsync(input, CancellationToken.None);
            var dormantRuntimeChanged = await BuildProvider(Runtime("runtime-b"), binaryPath, "override")
                .CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(binaryPath, "override-revision-2");
            var executableChanged = await BuildProvider(Runtime("runtime-b"), binaryPath, "override")
                .CaptureAsync(input, CancellationToken.None);

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
            var first = await BuildProvider(Runtime("runtime-a"), binaryPath).CaptureAsync(input, CancellationToken.None);

            await File.AppendAllTextAsync(implementationPath, "implementation-revision-2");
            var implementationChanged = await BuildProvider(Runtime("runtime-a"), binaryPath)
                .CaptureAsync(input, CancellationToken.None);

            AssertEx.NotEqual(first.Value, implementationChanged.Value);
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
        string? resolvedDraftPath = null)
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
        return new LaunchPolicyFingerprintProvider(store,
            binaryManager,
            modelStore,
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
