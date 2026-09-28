namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The throwaway smoke and evaluation spawns keep a receipt exactly as long as their child lives: present while the body runs,
///     gone once the finally has torn the child down, whether the body returned or threw.
/// </summary>
/// <remarks>
///     Linux only, as the store is. The fake handle carries THIS test process's pid and the fake binary is the test host's own
///     executable, so the receipt identity resolves. No reaper runs here.
/// </remarks>
[Category(TestCategories.Unit)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class TransientLlamaServerSpawnReceiptTests : IDisposable
{
    private readonly string _modelPath = Path.Combine(Path.GetTempPath(), $"xe-transient-receipt-{Guid.NewGuid():N}.gguf");
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"xe-transient-receipts-{Guid.NewGuid():N}");

    private string ReceiptsPath => Path.Combine(_root, "runtime", "llama-server");

    private string OwnReceipt => Path.Combine(ReceiptsPath, $"{Environment.ProcessId}.json");

    public void Dispose()
    {
        File.Delete(_modelPath);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task SmokeRun_ReceiptLivesForTheBody_AndIsRemovedAfter()
    {
        await File.WriteAllTextAsync(_modelPath, "smoke");
        var launcher = OwnPidLauncher();

        var presentDuringBody = await CreateLauncher(launcher).RunAsync(SmokeRequest(), (_, _) => Task.FromResult(File.Exists(OwnReceipt)), CancellationToken.None);

        AssertEx.True(presentDuringBody, "The child must be receipted while the body runs.");
        AssertEx.True(launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    [Test]
    public async Task SmokeRun_BodyThrows_StillRemovesTheReceipt()
    {
        await File.WriteAllTextAsync(_modelPath, "smoke");
        var launcher = OwnPidLauncher();

        await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
            CreateLauncher(launcher).RunAsync<bool>(SmokeRequest(), (_, _) => throw new InvalidOperationException("body failed"), CancellationToken.None));

        AssertEx.True(launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    [Test]
    public async Task Evaluation_ReceiptLivesForTheBody_AndAThrowingBodyStillRemovesIt()
    {
        await File.WriteAllTextAsync(_modelPath, "evaluation");
        var launcher = OwnPidLauncher();
        var variantSelector = new FakeVariantSelector(GpuVariant.Cpu);
        var binaryManager = new FakeBinaryManager(serverExecutablePath: Environment.ProcessPath!);
        var manifestProbe = new FakeLlamaServerCapabilityManifestProbe();
        var launchPolicy = new LlamaServerLaunchPolicy(new LlamaServerLaunchPolicyOptions(), new FakeLaunchFallbackStore());
        await using var supervisor = SupervisorFactory.Create(launcher,
            variantSelector: variantSelector,
            launchPolicy: launchPolicy,
            capabilityManifestProbe: manifestProbe);
        var harness = new TransientLlamaServerEvaluationHarness(supervisor,
            binaryManager,
            variantSelector,
            manifestProbe,
            launchPolicy,
            CreateLauncher(launcher, binaryManager),
            new NoOpGpuModelLoadAdmission());
        var presentDuringBody = false;

        await AssertEx.ThrowsAsync<InvalidOperationException>(() => harness.RunAsync<bool>(new TransientLlamaServerEvaluationRequest
            {
                ModelFilePath = _modelPath,
                AdapterFilePath = null,
                ContextTokens = 4096,
                ReadinessTimeout = TimeSpan.FromMinutes(1),
                LaunchPolicy = LlamaServerBenchmarkLaunchPolicy.DeterministicV1
            },
            static (_, _) => Task.CompletedTask,
            (_, _) =>
            {
                presentDuringBody = File.Exists(OwnReceipt);
                throw new InvalidOperationException("body failed");
            }, CancellationToken.None));

        AssertEx.True(presentDuringBody, "The evaluation child must be receipted while the body runs.");
        AssertEx.True(launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    private static FakeProcessLauncher OwnPidLauncher() =>
        new(_ => new FakeProcessHandle(Environment.ProcessId));

    private TransientLlamaServerLauncher CreateLauncher(FakeProcessLauncher launcher, FakeBinaryManager? binaryManager = null) =>
        new(binaryManager ?? new FakeBinaryManager(serverExecutablePath: Environment.ProcessPath!),
            new FakeVariantSelector(GpuVariant.Cpu),
            launcher,
            new FakeHealthProbe(),
            NullLogger<TransientLlamaServerLauncher>.Instance,
            new ProcessSpawnReceiptStore(_root, "llama-server", NullLogger.Instance));

    private TransientLlamaServerRequest SmokeRequest() =>
        new()
        {
            ModelFilePath = _modelPath,
            AdapterFilePath = null,
            ContextTokens = 2048,
            ReadinessTimeout = TimeSpan.FromSeconds(5)
        };

    private void AssertNoReceipts() =>
        AssertEx.Equal(expected: 0, Directory.Exists(ReceiptsPath) ? Directory.EnumerateFiles(ReceiptsPath, "*.json").Count() : 0,
            "Every teardown path must remove the receipt.");
}
