namespace XE_Local_AI_Engine.Tests.Training.Runs;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Run creation against the real stores and the real canonical export writer. The freeze is the point: what a run
///     trained on has to stay answerable after the dataset moves on, which is a claim only a real store can support.
/// </summary>
public sealed class TrainingRunServiceTests : IDisposable
{
    private readonly FixedNodeSqliteKeyHolder _keyHolder = new(RandomNumberGenerator.GetBytes(32));
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _keyHolder.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task RunCreate_WithoutLicenseConfirmation_IsRejectedAndQueuesNothing()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var fixture = await SeedAsync(context);
        var service = BuildService(context);

        var rejection = await AssertEx.ThrowsAsync<TrainingRunRejectedException>(
            () => service.CreateAsync(new CreateTrainingRunCommand(fixture.DatasetId, fixture.DatasetVersion, fixture.BaseArtifactId, LicenseConfirmed: false)));

        AssertEx.True(rejection.Message.Contains("licensing", StringComparison.OrdinalIgnoreCase), "The refusal has to name the licensing gate.");
        var runStore = new TrainingRunStore(context, TimeProvider.System);
        AssertEx.Equal(expected: 0, (await runStore.ListAsync(new TrainingRunQuery(Page: 1, PageSize: 50))).TotalCount,
            "A refused creation must leave no run behind.");
        AssertEx.Null(await runStore.ClaimNextAsync(), "A refused creation must queue nothing for the consumer to pick up.");
        AssertEx.False(Directory.Exists(Path.Combine(_root, "training", "datasets", fixture.DatasetId.ToString(), "frozen")),
            "A refused creation must not leave a frozen copy on disk either.");
    }

    [Test]
    public async Task RunCreate_WithAStaleDatasetVersion_IsRefusedAndLeavesNoOrphanFreeze()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var fixture = await SeedAsync(context);
        var service = BuildService(context);

        // The wizard's confirmation dialog was opened before somebody edited a sample.
        _ = await AssertEx.ThrowsAsync<TrainingConflictException>(
            () => service.CreateAsync(new CreateTrainingRunCommand(fixture.DatasetId, fixture.DatasetVersion + 1, fixture.BaseArtifactId, LicenseConfirmed: true)));

        var frozenDirectory = Path.Combine(_root, "training", "datasets", fixture.DatasetId.ToString(), "frozen");
        AssertEx.True(!Directory.Exists(frozenDirectory) || Directory.GetFiles(frozenDirectory).Length == 0,
            "The freeze is only meaningful behind a run; an orphan copy is dead plaintext at rest.");
    }

    [Test]
    public async Task DatasetFreeze_EditAfterEnqueue_DoesNotAffectRun()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var fixture = await SeedAsync(context);
        var service = BuildService(context);
        var workspace = BuildWorkspace();

        var run = await service.CreateAsync(new CreateTrainingRunCommand(fixture.DatasetId, fixture.DatasetVersion, fixture.BaseArtifactId, LicenseConfirmed: true));
        var freeze = ReadFreeze(run);
        var frozenBytesBefore = await File.ReadAllBytesAsync(workspace.FrozenDatasetPath(fixture.DatasetId, freeze.FreezeId));

        // Reject a sample: an accepted review bumps the dataset revision and recomputes its content fingerprint.
        var datasets = new TrainingDatasetStore(context, TimeProvider.System);
        var sample = (await datasets.ListAllSamplesAsync(fixture.DatasetId))[0];
        _ = await datasets.ReviewSampleAsync(new TrainingSampleReviewCommand(sample.Id, TrainingSampleReviewVerb.Reject, Label: null));
        var moved = AssertEx.NotNull(await datasets.GetDatasetAsync(fixture.DatasetId), "The dataset still exists.");

        AssertEx.NotEqual(fixture.DatasetContentFingerprint, moved.ContentFingerprint!);
        var reread = AssertEx.NotNull(await new TrainingRunStore(context, TimeProvider.System).GetAsync(run.Id), "The run still exists.");
        AssertEx.Equal(fixture.DatasetContentFingerprint, reread.DatasetContentFingerprint,
            "The run's frozen fingerprint is a copy taken at creation, not a live read.");
        AssertEx.Equal(fixture.DatasetRevision, reread.DatasetRevision);
        var frozenBytesAfter = await File.ReadAllBytesAsync(workspace.FrozenDatasetPath(fixture.DatasetId, freeze.FreezeId));
        AssertEx.True(frozenBytesBefore.SequenceEqual(frozenBytesAfter), "The frozen copy on disk is immutable once written.");
    }

    [Test]
    public async Task RunCreate_RecordsTheLicenseFactsIncludingTheirAbsence()
    {
        await using var provider = await BuildProviderAsync(Guid.NewGuid().ToString("N"));
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        var fixture = await SeedAsync(context);
        var service = BuildService(context);

        var run = await service.CreateAsync(new CreateTrainingRunCommand(fixture.DatasetId, fixture.DatasetVersion, fixture.BaseArtifactId, LicenseConfirmed: true));

        AssertEx.True(run.LicenseConfirmationJson.HasValue, "A run records its confirmation.");
        var confirmation = ReadConfirmation(run);
        // The seeded checkpoint has a NULL license column. That is a recorded fact in its own right, not a pass.
        AssertEx.False(confirmation.MetadataPresent, "No license metadata found is what this checkpoint's confirmation records.");
        AssertEx.Null(confirmation.License, "There is no license to name.");
        AssertEx.NotNullOrEmpty(confirmation.ConfirmationTextSha256, "The exact text shown to the operator is hashed onto the run.");
    }

    [Test]
    public void Split_IsStratifiedByKindAndDeterministic()
    {
        var samples = Enumerable.Range(0, 20)
                                .Select(index => Sample(index, index < 16 ? "tool-call" : "no-tool"))
                                .ToArray();

        var first = TrainingRunService.Split(samples, holdoutFraction: 0.25);
        var second = TrainingRunService.Split(samples, holdoutFraction: 0.25);

        AssertEx.Equal(first.Holdout.Count, second.Holdout.Count, "The freeze must be reproducible from the same input.");
        AssertEx.True(first.Holdout.SequenceEqual(second.Holdout), "The same input must produce the same membership.");
        AssertEx.Equal(samples.Length, first.Train.Count + first.Holdout.Count, "Every eligible sample lands on exactly one side.");
        // Stratified: the rare kind is represented on the holdout side rather than swallowed whole by training.
        var holdoutKinds = first.HoldoutSequences.Select(sequence => samples.First(sample => sample.Sequence == sequence).Kind).Distinct().ToArray();
        AssertEx.Equal(expected: 2, holdoutKinds.Length, "A random split can put an entire rare kind on one side; a stratified one cannot.");
    }

    [Test]
    public void Split_WithAFractionTooSmallToTakeASample_HoldsNothingBack()
    {
        var samples = Enumerable.Range(0, 3).Select(index => Sample(index, "tool-call")).ToArray();

        var split = TrainingRunService.Split(samples, holdoutFraction: 0.05);

        AssertEx.Equal(expected: 0, split.Holdout.Count, "Rounding down to zero holdout is correct: a partial sample cannot be held back.");
        AssertEx.Equal(expected: 3, split.Train.Count);
    }

    /// <summary>Deserializing in its own method keeps the span off an await path.</summary>
    private static TrainingRunFreezeV1 ReadFreeze(TrainingRunRecord run) =>
        AssertEx.NotNull(JsonSerializer.Deserialize<TrainingRunFreezeV1>(run.FreezeJson.Span, TrainingJson.Options), "The run carries its freeze.");

    private static TrainingLicenseConfirmationV1 ReadConfirmation(TrainingRunRecord run) =>
        AssertEx.NotNull(JsonSerializer.Deserialize<TrainingLicenseConfirmationV1>(run.LicenseConfirmationJson!.Value.Span, TrainingJson.Options),
            "The confirmation must decode.");

    private static TrainingSampleRecord Sample(int sequence, string kind) =>
        new(Guid.NewGuid(),
            Guid.Empty,
            sequence,
            kind,
            TrainingSampleLabel.Good,
            TrainingSampleReviewState.Approved,
            ReadOnlyMemory<byte>.Empty,
            ValidationJson: null,
            TrainingSampleProvenance.Generated,
            $"hash-{sequence}",
            CreatedAtUtc: 0,
            UpdatedAtUtc: 0);

    private ITrainingRunService BuildService(NodeChatDbContext context)
    {
        var runStore = new TrainingRunStore(context, TimeProvider.System);
        var datasetStore = new TrainingDatasetStore(context, TimeProvider.System);
        var artifactStore = new TrainingBaseArtifactStore(context, TimeProvider.System);

        var defaults = Substitute.For<ITrainingOptionDefaultsCalculator>();
        _ = defaults.ResolveAsync(Arg.Any<Guid>(), Arg.Any<TrainingRunOptionsV1?>(), Arg.Any<CancellationToken>())
                    .Returns(new TrainingRunDefaults(new TrainingRunOptionsV1(),
                        new TrainingFootprintEstimate(1, 1, 1, 1, Experimental: false),
                        AvailableVramBytes: 1,
                        VramKnown: true,
                        Fits: true,
                        RejectionReason: null));

        return new TrainingRunService(runStore,
            datasetStore,
            new DatasetExportService(datasetStore),
            defaults,
            new LicenseGateService(artifactStore, TimeProvider.System),
            BuildWorkspace(),
            new TrainingRunCancellationRegistry(),
            Substitute.For<ITrainingRunQueueSignal>(),
            Substitute.For<IInstalledBaseModelLinker>());
    }

    private TrainingRunWorkspace BuildWorkspace() =>
        new(new FixedNodeDataDirectory(_root), _keyHolder);

    private static async Task<RunFixture> SeedAsync(NodeChatDbContext context)
    {
        var datasets = new TrainingDatasetStore(context, TimeProvider.System);
        var definition = await datasets.CreateDefinitionAsync(new TrainingDefinitionInput("tool calling",
            TrainingDatasetKind.ToolCalling,
            Encoding.UTF8.GetBytes("""{"schemaVersion":1,"holdoutFraction":0.2}""")));
        var dataset = await datasets.CreateDatasetAndEnqueueAsync(new TrainingDatasetEnqueueCommand(definition.Id, definition.Version, "dataset"));
        _ = await datasets.ClaimNextAsync();
        for (var index = 0; index < 4; index++)
        {
            _ = await datasets.AppendSampleAsync(new TrainingSampleInput(dataset.Id,
                "tool-call",
                TrainingSampleLabel.Good,
                Encoding.UTF8.GetBytes($$"""{"schemaVersion":1,"parts":[{"kind":"user","sequence":0,"content":"q{{index}}"}]}"""),
                ValidationJson: null,
                TrainingSampleProvenance.Generated,
                new string((char)('a' + index), count: 64)));
        }

        var ready = await datasets.CompleteGenerationAsync(dataset.Id, DatasetGenerationWorkStatus.Succeeded, errorMessage: null);

        var artifacts = new TrainingBaseArtifactStore(context, TimeProvider.System);
        var downloading = await artifacts.StartDownloadAsync("org/base-model", new string('b', count: 40));
        // licenseJson stays null: a repository with no license tag still has to be confirmed, just differently.
        var baseArtifact = await artifacts.MarkReadyAsync(downloading.Id, downloading.Version, Encoding.UTF8.GetBytes("[]"), totalBytes: 42, licenseJson: null);

        return new RunFixture(ready.Id,
            ready.Version,
            AssertEx.NotNull(ready.ContentFingerprint, "A ready dataset carries a content fingerprint."),
            ready.Revision,
            baseArtifact.Id);
    }

    private async Task<ServiceProvider> BuildProviderAsync(string name)
    {
        _ = Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        // DI owns and disposes its key holder, so it gets its own rather than the workspace's — a shared one would be
        // disposed by the first scope that closes and break every test after it.
        _ = services.AddScoped<INodeSqliteKeyHolder, NullNodeSqliteKeyHolder>();
        _ = services.AddDbContext<NodeChatDbContext>(options => options.UseSqlite($"Data Source={Path.Combine(_root, $"{name}.sqlite")}"));

        var provider = services.BuildServiceProvider(validateScopes: true);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<NodeChatDbContext>();
        _ = await context.Database.EnsureDeletedAsync();
        _ = await context.Database.EnsureCreatedAsync();
        return provider;
    }

    private sealed record RunFixture(Guid DatasetId, long DatasetVersion, string DatasetContentFingerprint, int DatasetRevision, Guid BaseArtifactId);
}
