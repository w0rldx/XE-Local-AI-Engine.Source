namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The transcription supervisor's spawn receipts: written once the handle exists, kept across an in-place model switch (same
///     process, same identity) and gone after every teardown path. Mirrors <see cref="SupervisorSpawnReceiptTests" />.
/// </summary>
/// <remarks>
///     Linux only, as the store is. The fake handle carries THIS test process's pid and the fake binary is the test host's own
///     executable, so the receipt records a real <c>/proc</c> start time and realpath. No reaper runs here.
/// </remarks>
[Category(TestCategories.Unit)]
[RunOn(OS.Linux)]
[SupportedOSPlatform("linux")]
public sealed class WhisperServerSpawnReceiptTests : IDisposable
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"xe-whisper-receipts-{Guid.NewGuid():N}");

    private string ReceiptsPath => Path.Combine(_root, "runtime", "whisper-server");

    private string OwnReceipt => Path.Combine(ReceiptsPath, $"{Environment.ProcessId}.json");

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
        await using var harness = Create();

        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        AssertEx.True(File.Exists(OwnReceipt), "The spawn must leave a receipt while the child runs.");
        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(OwnReceipt));
        var receipt = AssertEx.NotNull(JsonSerializer.Deserialize<ProcessSpawnReceipt>(await File.ReadAllTextAsync(OwnReceipt), WebJson));
        AssertEx.Equal(Environment.ProcessId, receipt.Pid);
        AssertEx.Equal(SupervisorSpawnReceiptTests.ReadOwnStartTicks(), receipt.StartTicks);
        AssertEx.Equal(File.ResolveLinkTarget("/proc/self/exe", returnFinalTarget: true)!.FullName, receipt.ExecutablePath);
        AssertEx.Contains(receipt.Label, "base port ");

        await harness.Supervisor.EvictAsync(CancellationToken.None);

        AssertEx.True(harness.Launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    [Test]
    public async Task InPlaceModelSwitch_KeepsTheOneReceipt_AndShutdownRemovesIt()
    {
        var harness = Create();
        await harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None);

        await harness.Supervisor.EnsureRunningAsync("small", CancellationToken.None);

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount, "The switch under test must be in place, not a respawn.");
        AssertEx.Equal(OwnReceipt, Directory.EnumerateFiles(ReceiptsPath, "*.json").Single());

        await harness.DisposeAsync();

        AssertNoReceipts();
    }

    [Test]
    public async Task ReadinessFailure_RemovesTheReceipt()
    {
        await using var harness = Create(new FakeWhisperReadinessProbe(ready: false));

        await AssertEx.ThrowsAsync<Exception>(() => harness.Supervisor.EnsureRunningAsync("base", CancellationToken.None));

        AssertEx.Equal(expected: 1, harness.Launcher.LaunchCount, "The failure path under test is after the launch.");
        AssertEx.True(harness.Launcher.Handles.Single().WasDisposed);
        AssertNoReceipts();
    }

    private WhisperSupervisorHarness Create(FakeWhisperReadinessProbe? probe = null) =>
        new(new FakeWhisperProcessLauncher(processId: Environment.ProcessId),
            probe,
            binaryManager: new FakeWhisperBinaryManager(serverExecutablePath: Environment.ProcessPath!),
            spawnReceipts: new ProcessSpawnReceiptStore(_root, "whisper-server", NullLogger.Instance));

    private void AssertNoReceipts() =>
        AssertEx.Equal(expected: 0, Directory.Exists(ReceiptsPath) ? Directory.EnumerateFiles(ReceiptsPath, "*.json").Count() : 0,
            "Every teardown path must remove the receipt.");
}
