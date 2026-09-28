namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The supervisor's spawn receipts: written once the handle exists, carrying the identity the startup reaper re-checks, and
///     gone after every teardown path, so a receipt left on disk can only mean the host died without tearing its child down.
/// </summary>
/// <remarks>
///     Linux only, as the store is. The fake handle carries THIS test process's pid, so the receipt records a real <c>/proc</c>
///     start time, and the fake binary is the test host's own executable, so its realpath resolves. No reaper runs here.
/// </remarks>
[Category(TestCategories.Unit)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class SupervisorSpawnReceiptTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"xe-spawn-receipts-{Guid.NewGuid():N}");

    private string ReceiptsPath => Path.Combine(_root, "runtime", "llama-server");

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
        var launcher = OwnPidLauncher();
        await using var supervisor = Create(launcher);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);

        var file = Path.Combine(ReceiptsPath, $"{Environment.ProcessId}.json");
        AssertEx.True(File.Exists(file), "The spawn must leave a receipt while the child runs.");
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var receipt = AssertEx.NotNull(JsonSerializer.Deserialize<ProcessSpawnReceipt>(await File.ReadAllTextAsync(file), WebJson));
        AssertEx.Equal(Environment.ProcessId, receipt.Pid);
        AssertEx.Equal(ReadOwnStartTicks(), receipt.StartTicks);
        // The kernel's own canonical path for this process's binary is what the reaper will compare against.
        AssertEx.Equal(File.ResolveLinkTarget("/proc/self/exe", returnFinalTarget: true)!.FullName, receipt.ExecutablePath);
        AssertEx.Contains(receipt.Label, "model-a/Chat port ");

        await supervisor.EvictAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.True(launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    [Test]
    public async Task Shutdown_RemovesEveryReceipt()
    {
        var supervisor = Create(OwnPidLauncher());
        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        AssertEx.Equal(expected: 1, Directory.EnumerateFiles(ReceiptsPath, "*.json").Count());

        await supervisor.DisposeAsync();

        AssertNoReceipts();
    }

    [Test]
    public async Task ReadinessFailure_RemovesTheReceipt()
    {
        var launcher = OwnPidLauncher();
        await using var supervisor = Create(launcher, new FakeHealthProbe(ready: false));

        await AssertEx.ThrowsAsync<Exception>(() => supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None));

        AssertEx.True(launcher.LaunchCount > 0, "The failure path under test is after the launch.");
        AssertNoReceipts();
    }

    [Test]
    public async Task ShutdownDuringReadiness_CancelledSpawn_RemovesTheReceipt()
    {
        var probe = new GatedHealthProbe();
        var supervisor = Create(OwnPidLauncher(), probe);
        var ensure = supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await AssertEx.EventuallyAsync(() => probe.Waiting == 1, TimeSpan.FromSeconds(5), "The spawn never reached readiness.");
        AssertEx.Equal(expected: 1, Directory.EnumerateFiles(ReceiptsPath, "*.json").Count(), "The receipt must exist during the load.");

        await supervisor.DisposeAsync();

        await AssertEx.ThrowsAsync<Exception>(() => ensure);
        AssertNoReceipts();
    }

    [Test]
    public async Task WithoutADataDirectory_WritesNothing_AndKeepsTheLaunchersHandle()
    {
        var launcher = OwnPidLauncher();
        var disabled = new ProcessSpawnReceiptStore(nodeDataRoot: null, "llama-server", NullLogger.Instance);
        await using var supervisor = SupervisorFactory.Create(launcher,
            binaryManager: new FakeBinaryManager(serverExecutablePath: Environment.ProcessPath!),
            spawnReceipts: disabled);

        await supervisor.EnsureRunningAsync("model-a", ModelRole.Chat, CancellationToken.None);
        await supervisor.EvictAsync("model-a", ModelRole.Chat, CancellationToken.None);

        AssertEx.False(disabled.IsEnabled);
        AssertEx.False(Directory.Exists(_root));
        AssertEx.True(launcher.Handles.Single().WasDisposed, "The untracked handle is the one the supervisor tears down.");
    }

    private static FakeProcessLauncher OwnPidLauncher() =>
        new(_ => new FakeProcessHandle(Environment.ProcessId));

    private static long ReadOwnStartTicks()
    {
        var stat = File.ReadAllText("/proc/self/stat");
        return long.Parse(stat[(stat.LastIndexOf(')') + 2)..].Split(' ')[19], CultureInfo.InvariantCulture);
    }

    private LlamaServerProcessSupervisor Create(FakeProcessLauncher launcher, ILlamaServerHealthProbe? probe = null) =>
        SupervisorFactory.Create(launcher,
            probe,
            options: new LlamaServerSupervisorOptions
            {
                IdleTimeToLive = TimeSpan.FromHours(1),
                MaxLoadedProcesses = 3,
                MaxRestartAttempts = 1
            },
            binaryManager: new FakeBinaryManager(serverExecutablePath: Environment.ProcessPath!),
            spawnReceipts: new ProcessSpawnReceiptStore(_root, "llama-server", NullLogger.Instance));

    private void AssertNoReceipts() =>
        AssertEx.Equal(expected: 0, Directory.Exists(ReceiptsPath) ? Directory.EnumerateFiles(ReceiptsPath, "*.json").Count() : 0,
            "Every teardown path must remove the receipt.");
}
