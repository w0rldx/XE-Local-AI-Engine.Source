namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelLab;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Training.Runs;
using OS = TUnit.Core.Enums.OS;

/// <summary>A restart after a crash mid-trace: no process, no plaintext, and the row is failed. On the real store and database.</summary>
[Category(TestCategories.Integration)]
public sealed class ModelLabStartupReaperTests
{
    private static readonly TrainingLaunchReceiptV1 Receipt = new()
    {
        Pid = 4242,
        Pgid = 4242,
        ExecutablePath = "/venv/bin/python",
        StartTicks = 99,
        RunToken = "the-token"
    };

    private static TrainingProcessFacts Live =>
        new()
        {
            Pgid = Receipt.Pgid,
            StartTicks = Receipt.StartTicks,
            ExecutablePath = Receipt.ExecutablePath,
            RunToken = Receipt.RunToken
        };

    [Test]
    public async Task ACrashedJob_IsKilled_ItsRowFailed_AndItsPlaintextGone()
    {
        await using var factory = new TestServerWebAppFactory();
        var id = await SeedRunningAsync(factory);
        var dataDirectory = factory.Services.GetRequiredService<INodeDataDirectory>();
        var workFile = await SeedPlaintextAsync(dataDirectory, id);
        var inspector = new FakeTrainingProcessInspector(Live);

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(4242, inspector.SignalledGroups.Single(), "The whole group of the job that outlived its host is killed.");
        var record = await ReadAsync(factory, id);
        AssertEx.Equal(ModelLabExperimentStatus.Failed, record.Status);
        AssertEx.Equal("The experiment was interrupted by a host restart.", record.ErrorMessage!);
        AssertEx.False(File.Exists(workFile), "The plaintext prompt and trace do not survive the restart, whatever their age.");
        AssertEx.Equal(expected: 0, (await ReceiptsAsync(factory)).Count, "The receipt is cleared once the process is ruled out.");
    }

    [Test]
    public async Task ARecycledPid_IsNotSignalled_AndTheRowStillFails()
    {
        await using var factory = new TestServerWebAppFactory();
        var id = await SeedRunningAsync(factory);
        var inspector = new FakeTrainingProcessInspector(Live with
        {
            RunToken = "someone-else"
        });

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, inspector.SignalledGroups.Count);
        AssertEx.Equal(expected: 0, inspector.SignalledProcesses.Count);
        AssertEx.Equal(ModelLabExperimentStatus.Failed, (await ReadAsync(factory, id)).Status);
        AssertEx.Equal(expected: 0, (await ReceiptsAsync(factory)).Count);
    }

    [Test]
    public async Task AKillThatThrows_KeepsTheReceiptForTheNextStartup()
    {
        await using var factory = new TestServerWebAppFactory();
        var id = await SeedRunningAsync(factory);
        var inspector = Substitute.For<ITrainingProcessInspector>();
        inspector.Inspect(Receipt.Pid).Returns(Live);
        inspector.HostProcessGroupId.Returns(777);
        inspector.KillProcessGroupAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("no"));

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, (await ReceiptsAsync(factory)).Count, "Dropping it would strand a live job with nothing to identify it by.");
        AssertEx.Equal(ModelLabExperimentStatus.Failed, (await ReadAsync(factory, id)).Status);
    }

    [Test]
    public async Task AKillDeferredByAnOperationalFailure_KeepsTheReceiptForTheNextStartup()
    {
        await using var factory = new TestServerWebAppFactory();
        var id = await SeedRunningAsync(factory);
        var inspector = new FakeTrainingProcessInspector(Live)
        {
            PresenceAfterKill = TrainingProcessPresence.Deferred
        };

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, inspector.SignalledGroups.Count);
        AssertEx.Equal(expected: 1, (await ReceiptsAsync(factory)).Count, "A refused signal proves nothing about the job's process.");
        AssertEx.Equal(ModelLabExperimentStatus.Failed, (await ReadAsync(factory, id)).Status);
    }

    [Test]
    public async Task AWarmReceiptWhoseKillIsDeferred_IsKept()
    {
        await using var factory = new TestServerWebAppFactory();
        var dataDirectory = factory.Services.GetRequiredService<INodeDataDirectory>();
        var file = await SeedWarmReceiptAsync(dataDirectory);
        var inspector = new FakeTrainingProcessInspector(Live)
        {
            PresenceAfterKill = TrainingProcessPresence.Deferred
        };

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(4242, inspector.SignalledGroups.Single());
        AssertEx.True(File.Exists(file), "The warm receipt is deleted only once its process is proven gone.");
    }

    [Test]
    public async Task AnUnreadableReceipt_IsCleared()
    {
        await using var factory = new TestServerWebAppFactory();
        var id = await SeedRunningAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().SetLaunchReceiptAsync(id, new byte[]
            {
                1,
                2,
                3
            });
        }

        var inspector = new FakeTrainingProcessInspector(Live);
        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 0, (await ReceiptsAsync(factory)).Count);
        AssertEx.Equal(expected: 0, inspector.SignalledGroups.Count);
    }

    /// <summary>A warm worker's receipt lives beside the work root, so the sweep that deletes the work root cannot take it.</summary>
    [Test]
    public async Task AWarmWorkersReceiptFile_IsReapedBeforeTheWorkRootGoes_AndDeletedOnceItsProcessIsGone()
    {
        await using var factory = new TestServerWebAppFactory();
        var dataDirectory = factory.Services.GetRequiredService<INodeDataDirectory>();
        var file = await SeedWarmReceiptAsync(dataDirectory);
        var workFile = await SeedPlaintextAsync(dataDirectory, Guid.NewGuid());
        var inspector = new FakeTrainingProcessInspector(Live);

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(4242, inspector.SignalledGroups.Single(), "The worker that outlived its host is killed by its receipt.");
        AssertEx.False(File.Exists(file), "Its receipt goes once the kill is seen to have worked.");
        AssertEx.False(File.Exists(workFile));
    }

    [Test]
    public async Task AWarmReceiptWhoseProcessCannotBeRead_IsKept()
    {
        await using var factory = new TestServerWebAppFactory();
        var dataDirectory = factory.Services.GetRequiredService<INodeDataDirectory>();
        var file = await SeedWarmReceiptAsync(dataDirectory);
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            AbsentPresence = TrainingProcessPresence.Unknown
        };

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.True(File.Exists(file), "An unreadable process is never taken for gone: the file is all that is left to find it by.");
        AssertEx.Equal(expected: 0, inspector.SignalledGroups.Count);
    }

    [Test]
    public async Task AWarmReceiptWhoseLiveProcessHidesItsIdentity_IsKept()
    {
        await using var factory = new TestServerWebAppFactory();
        var dataDirectory = factory.Services.GetRequiredService<INodeDataDirectory>();
        var file = await SeedWarmReceiptAsync(dataDirectory);
        var inspector = new FakeTrainingProcessInspector(Live with
        {
            ExecutablePath = null,
            RunToken = null
        });

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.True(File.Exists(file), "A live pid whose exe and environment cannot be read is not proven gone.");
        AssertEx.Equal(expected: 0, inspector.SignalledGroups.Count);
    }

    /// <summary>
    ///     A worker whose leader is gone while the group it led lives may still hold the GPU: its receipt stays and is handed to the service, and the
    ///     next re-reap releases it once the group is empty. Nothing is signalled.
    /// </summary>
    [Test]
    public async Task AWarmReceiptWhoseLeaderIsGoneButWhoseGroupLives_IsKeptAndAdopted_UntilTheGroupEmpties()
    {
        var inspector = new FakeTrainingProcessInspector(facts: null)
        {
            GroupHasMembers = true
        };
        await using var factory = new TestServerWebAppFactory
        {
            ConfigureAdditionalTestServices = services =>
            {
                services.RemoveAll<ITrainingProcessInspector>();
                services.AddSingleton<ITrainingProcessInspector>(inspector);
            }
        };
        var file = await SeedWarmReceiptAsync(factory.Services.GetRequiredService<INodeDataDirectory>());
        var experiments = factory.Services.GetRequiredService<ModelLabExperimentService>();

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.True(File.Exists(file), "A missing leader is no exit while its group has members.");
        AssertEx.Contains(inspector.ProbedGroups, Receipt.Pid);
        AssertEx.True(await experiments.HasUnconfirmedWorkerAsync(CancellationToken.None), "The sweep hands the kept receipt to the service, and its re-reap keeps it.");
        AssertEx.True(File.Exists(file));

        inspector.GroupHasMembers = false;

        AssertEx.False(await experiments.HasUnconfirmedWorkerAsync(CancellationToken.None), "The re-reap after the group emptied proves the worker gone.");
        AssertEx.False(File.Exists(file), "Its receipt goes with it.");
        AssertEx.Empty(inspector.SignalledGroups);
        AssertEx.Empty(inspector.SignalledProcesses);
    }

    /// <summary>A receipts directory that exists and cannot be listed is a failed scan, not an empty one; the rows are swept and recovered regardless.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task AWarmReceiptDirectoryThatCannotBeListed_StillSweepsAndRecoversTheRows(bool viaParent)
    {
        await using var factory = new TestServerWebAppFactory();
        var id = await SeedRunningAsync(factory);
        var root = ModelLabWarmReceipts.Root(factory.Services.GetRequiredService<INodeDataDirectory>());
        var inspector = new FakeTrainingProcessInspector(Live);
        using (Unlistable(root, viaParent))
        {
            await Reaper(factory, inspector).StartAsync(CancellationToken.None);
        }

        AssertEx.Equal(ModelLabExperimentStatus.Failed, (await ReadAsync(factory, id)).Status, "Row recovery does not depend on the receipt scan.");
        AssertEx.Equal(4242, inspector.SignalledGroups.Single(), "Nor does the row receipts' sweep.");
        AssertEx.Equal(expected: 0, (await ReceiptsAsync(factory)).Count);
    }

    /// <summary>
    ///     Makes <paramref name="directory" /> unlistable until disposed: its read bit goes, or with <paramref name="viaParent" /> its parent's
    ///     execute bit, so it cannot even be seen to exist. Skips visibly where neither stops a listing.
    /// </summary>
    [UnsupportedOSPlatform("windows")]
    internal static IDisposable Unlistable(string directory, bool viaParent = false)
    {
        if (Environment.IsPrivilegedProcess)
        {
            Skip.Test("BLOCKED: a privileged process ignores the directory permissions this test removes, so the receipt scan cannot be made to fail.");
        }

        Directory.CreateDirectory(directory);
        var target = viaParent ? Path.GetDirectoryName(directory)! : directory;
        File.SetUnixFileMode(target, viaParent ? UnixFileMode.UserRead | UnixFileMode.UserWrite : UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new DirectoryModeRestore(target);
    }

    private static async Task<string> SeedWarmReceiptAsync(INodeDataDirectory dataDirectory)
    {
        var file = ModelLabWarmReceipts.PathFor(dataDirectory, Receipt.RunToken);
        await ModelLabWarmReceipts.WriteAsync(file, JsonSerializer.SerializeToUtf8Bytes(Receipt, TrainingJson.Options));
        return file;
    }

    private static ModelLabStartupReaper Reaper(TestServerWebAppFactory factory, ITrainingProcessInspector inspector) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            inspector,
            factory.Services.GetRequiredService<INodeDataDirectory>(),
            factory.Services.GetRequiredService<ModelLabExperimentService>(),
            NullLogger<ModelLabStartupReaper>.Instance);

    private static async Task<Guid> SeedRunningAsync(TestServerWebAppFactory factory)
    {
        var id = Guid.NewGuid();
        await using var scope = factory.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>();
        await store.CreateRunningAsync(new ModelLabExperimentCreate
        {
            Id = id,
            RepoId = "Qwen/Qwen3.5-0.8B",
            Revision = "rev",
            BaseArtifactId = Guid.NewGuid(),
            InstalledModelName = "org/Some-GGUF:Q4_K_M",
            Prompt = "secret",
            LicenseConfirmationJson = new byte[]
            {
                1
            }
        });
        await store.SetLaunchReceiptAsync(id, JsonSerializer.SerializeToUtf8Bytes(Receipt, TrainingJson.Options));
        return id;
    }

    private static async Task<string> SeedPlaintextAsync(INodeDataDirectory dataDirectory, Guid id)
    {
        var work = Path.Combine(ModelLabScriptRunner.WorkRoot(dataDirectory), id.ToString("N"));
        Directory.CreateDirectory(work);
        var file = Path.Combine(work, "job.json");
        await File.WriteAllTextAsync(file, "secret");
        return file;
    }

    private static async Task<ModelLabExperimentRecord> ReadAsync(TestServerWebAppFactory factory, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return AssertEx.NotNull(await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().GetAsync(id));
    }

    private static async Task<IReadOnlyList<ModelLabLaunchReceipt>> ReceiptsAsync(TestServerWebAppFactory factory)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IModelLabExperimentStore>().ListLaunchReceiptsAsync();
    }

    [UnsupportedOSPlatform("windows")]
    private sealed class DirectoryModeRestore : IDisposable
    {
        private readonly string _directory;

        public DirectoryModeRestore(string directory)
        {
            _directory = directory;
        }

        public void Dispose() =>
            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
