namespace XE_Local_AI_Engine.Tests.ModelLab;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
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
        inspector.KillProcessGroupAsync(Arg.Any<int>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("no"));

        await Reaper(factory, inspector).StartAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, (await ReceiptsAsync(factory)).Count, "Dropping it would strand a live job with nothing to identify it by.");
        AssertEx.Equal(ModelLabExperimentStatus.Failed, (await ReadAsync(factory, id)).Status);
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

    private static ModelLabStartupReaper Reaper(TestServerWebAppFactory factory, ITrainingProcessInspector inspector) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            inspector,
            factory.Services.GetRequiredService<INodeDataDirectory>(),
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
        var work = Path.Combine(ModelLabExperimentService.WorkRoot(dataDirectory), id.ToString("N"));
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
}
