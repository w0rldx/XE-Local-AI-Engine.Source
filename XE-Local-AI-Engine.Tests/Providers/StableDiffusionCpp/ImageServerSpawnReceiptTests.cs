namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The image supervisor's spawn receipts: written once the handle exists and gone after every teardown path, so a receipt left
///     on disk can only mean the host died without tearing its sd-server down. Mirrors <see cref="SupervisorSpawnReceiptTests" />.
/// </summary>
/// <remarks>
///     Linux only, as the store is. The fake handle carries THIS test process's pid and the fake binary is the test host's own
///     executable, so the receipt records a real <c>/proc</c> start time and realpath. No reaper runs here.
/// </remarks>
[Category(TestCategories.Unit)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class ImageServerSpawnReceiptTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"xe-sd-receipts-{Guid.NewGuid():N}");

    private string ReceiptsPath => Path.Combine(_root, "runtime", "sd-server");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Spawn_WritesAUserOnlyReceiptWithTheChildsIdentity_AndEvictRemovesIt()
    {
        var launcher = new FakeImageProcessLauncher(processId: Environment.ProcessId);
        await using var supervisor = Create(launcher);

        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);

        var file = Path.Combine(ReceiptsPath, $"{Environment.ProcessId}.json");
        AssertEx.True(File.Exists(file), "The spawn must leave a receipt while the child runs.");
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var receipt = AssertEx.NotNull(JsonSerializer.Deserialize<ProcessSpawnReceipt>(await File.ReadAllTextAsync(file), WebJson));
        AssertEx.Equal(Environment.ProcessId, receipt.Pid);
        AssertEx.Equal(SupervisorSpawnReceiptTests.ReadOwnStartTicks(), receipt.StartTicks);
        AssertEx.Equal(File.ResolveLinkTarget("/proc/self/exe", returnFinalTarget: true)!.FullName, receipt.ExecutablePath);
        AssertEx.Contains(receipt.Label, "sd15 port ");

        await supervisor.EvictAsync("sd15", CancellationToken.None);

        AssertEx.True(launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    [Test]
    public async Task Shutdown_RemovesTheReceipt()
    {
        var supervisor = Create(new FakeImageProcessLauncher(processId: Environment.ProcessId));
        await supervisor.EnsureRunningAsync("sd15", CancellationToken.None);
        AssertEx.Equal(expected: 1, Directory.EnumerateFiles(ReceiptsPath, "*.json").Count());

        await supervisor.DisposeAsync();

        AssertNoReceipts();
    }

    [Test]
    public async Task ReadinessFailure_RemovesTheReceipt()
    {
        var launcher = new FakeImageProcessLauncher(processId: Environment.ProcessId);
        await using var supervisor = Create(launcher, new FakeImageReadinessProbe(ready: false));

        await AssertEx.ThrowsAsync<Exception>(() => supervisor.EnsureRunningAsync("sd15", CancellationToken.None));

        AssertEx.Equal(expected: 1, launcher.LaunchCount, "The failure path under test is after the launch.");
        AssertEx.True(launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    private ImageServerProcessSupervisor Create(FakeImageProcessLauncher launcher, FakeImageReadinessProbe? probe = null) =>
        ImageSupervisorFactory.Create(launcher,
            probe,
            binaryManager: new FakeSdBinaryManager(serverExecutablePath: Environment.ProcessPath!),
            spawnReceipts: new ProcessSpawnReceiptStore(_root, "sd-server", NullLogger.Instance));

    private void AssertNoReceipts() =>
        AssertEx.Equal(expected: 0, Directory.Exists(ReceiptsPath) ? Directory.EnumerateFiles(ReceiptsPath, "*.json").Count() : 0,
            "Every teardown path must remove the receipt.");
}
