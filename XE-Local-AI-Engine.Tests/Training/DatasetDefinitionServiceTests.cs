namespace XE_Local_AI_Engine.Tests.Training;

using System.Text.Json;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Training.Datasets;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class DatasetDefinitionServiceTests
{
    [Test]
    [Arguments(0.04)]
    [Arguments(0.31)]
    [Arguments(0.0)]
    [Arguments(1.0)]
    public async Task Definition_HoldoutFraction_BoundsValidated(double fraction)
    {
        var harness = new Harness();

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(() => harness.Service.CreateAsync(Draft(Body() with
        {
            HoldoutFraction = fraction
        })));

        AssertEx.Contains(exception.Message, "hold-out fraction");
    }

    [Test]
    [Arguments(0.05)]
    [Arguments(0.10)]
    [Arguments(0.30)]
    public async Task Definition_HoldoutFraction_WithinBounds_IsAccepted(double fraction)
    {
        var harness = new Harness();

        _ = await harness.Service.CreateAsync(Draft(Body() with
        {
            HoldoutFraction = fraction
        }));

        AssertEx.Equal(fraction, harness.Saved().HoldoutFraction);
    }

    [Test]
    public async Task Definition_DefaultHoldoutFraction_IsTenPercent()
    {
        var harness = new Harness();

        _ = await harness.Service.CreateAsync(Draft(Body()));

        AssertEx.Equal(expected: 0.10, harness.Saved().HoldoutFraction);
    }

    [Test]
    public async Task Definition_ToolSnapshot_CarriesTheComposedApprovalNotTheCatalogDefault()
    {
        var harness = new Harness();

        _ = await harness.Service.CreateAsync(Draft(Body() with
        {
            Tools = [new DatasetToolSnapshotV1("read_file", null, null, RequiresApproval: false, ToolCategory.Unknown)]
        }));

        var snapshot = harness.Saved().Tools.Single();
        AssertEx.Equal("read_file", snapshot.Name);
        AssertEx.Equal(ToolCategory.ReadLocal, snapshot.Category);
        AssertEx.True(snapshot.RequiresApproval, "The snapshot must carry the policy-composed approval, not the catalog default.");
        _ = harness.Policy.Received(1).RequiresApproval("read_file", ToolCategory.ReadLocal, false);
    }

    [Test]
    public async Task Definition_UnknownTool_IsRejected()
    {
        var harness = new Harness();

        var exception = await AssertEx.ThrowsAsync<TrainingValidationException>(() => harness.Service.CreateAsync(Draft(Body() with
        {
            Tools = [new DatasetToolSnapshotV1("no_such_tool", null, null, RequiresApproval: false, ToolCategory.Unknown)]
        })));

        AssertEx.Contains(exception.Message, "no_such_tool");
    }

    [Test]
    public async Task Definition_NonNumericSeed_IsRejected()
    {
        var harness = new Harness();

        _ = await AssertEx.ThrowsAsync<TrainingValidationException>(() => harness.Service.CreateAsync(Draft(Body() with
        {
            BaseSeed = "not-a-seed"
        })));
    }

    private static DatasetDefinitionDraft Draft(DatasetDefinitionBodyV1 body) =>
        new("definition", body);

    private static DatasetDefinitionBodyV1 Body() =>
        new()
        {
            TeacherModelName = "teacher.gguf",
            TeacherOutputMode = TeacherOutputMode.Constrained,
            SystemInstructions = "produce examples",
            SampleKinds = [new DatasetSampleKindTargetV1("tool-call", 4, TrainingSampleLabel.Good)]
        };

    private sealed class Harness
    {
        private TrainingDefinitionInput? _captured;

        public Harness()
        {
            var offerProvider = Substitute.For<ILocalToolOfferProvider>();
            _ = offerProvider.GetOfferedToolsAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
                             .Returns<IReadOnlyList<AllowedToolDto>>([
                                 new AllowedToolDto
                                 {
                                     Id = Guid.NewGuid(),
                                     Name = "read_file",
                                     Location = ToolLocation.ClientLocal,
                                     RequiresApproval = false,
                                     Category = ToolCategory.ReadLocal
                                 }
                             ]);

            Policy = Substitute.For<IToolApprovalPolicy>();
            _ = Policy.RequiresApproval(Arg.Any<string>(), Arg.Any<ToolCategory>(), Arg.Any<bool>()).Returns(true);

            var store = Substitute.For<ITrainingDatasetStore>();
            _ = store.CreateDefinitionAsync(Arg.Do<TrainingDefinitionInput>(input => _captured = input), Arg.Any<CancellationToken>())
                     .Returns(call => Record(call.Arg<TrainingDefinitionInput>()));

            Service = new DatasetDefinitionService(store, offerProvider, Policy);
        }

        public IDatasetDefinitionService Service { get; }

        public IToolApprovalPolicy Policy { get; }

        public DatasetDefinitionBodyV1 Saved() =>
            JsonSerializer.Deserialize<DatasetDefinitionBodyV1>(AssertEx.NotNull(_captured, "The service should have reached the store.").DefinitionJson.Span, TrainingJson.Options)!;

        private static TrainingDefinitionRecord Record(TrainingDefinitionInput input) =>
            new(Guid.NewGuid(), input.Name, input.Kind, input.DefinitionJson, DefinitionVersion: 1, Version: 1, CreatedAtUtc: 0, UpdatedAtUtc: 0);
    }
}
