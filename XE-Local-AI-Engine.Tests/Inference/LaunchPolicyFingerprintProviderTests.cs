namespace XE_Local_AI_Engine.Tests.Inference;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
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
            AssertEx.Equal(expected: 2, first.Version);
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
        string? binaryVersion = null)
    {
        var store = Substitute.For<IInstalledRuntimeStore>();
        store.ReadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<InstalledRuntimeState?>(runtime));
        var binaryManager = Substitute.For<ILlamaCppBinaryManager>();
        binaryManager.EnsureBinaryAsync(Arg.Any<GpuVariant>(), Arg.Any<CancellationToken>())
                     .Returns(Task.FromResult(new LlamaBinary(binaryPath,
                         binaryVersion ?? runtime.Tag,
                         runtime.Variant,
                         IsPinnedFallback: false)));
        return new LaunchPolicyFingerprintProvider(store, binaryManager);
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
