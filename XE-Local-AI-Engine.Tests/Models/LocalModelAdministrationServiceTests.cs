namespace XE_Local_AI_Engine.Tests.Models;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.Models.Implementation;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class LocalModelAdministrationServiceTests
{
    [Test]
    public async Task SelectDefaultAsync_InstalledLocalOnly_RejectsMissingModelWithoutSaving()
    {
        var settings = new FakeNodeSettingsStore(new StoredNodeSettings());
        var harness = new Harness(settings);
        harness.GgufStore.ExistsAsync("missing", Arg.Any<CancellationToken>()).Returns(false);

        var result = await harness.Service
                                  .SelectDefaultAsync("missing", LocalModelSelectionPolicy.InstalledLocalOnly);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.ModelNotInstalled, result.FailureCode);
        AssertEx.Equal(expected: 0, settings.WriteCount, "a rejected selection must reach the file by no route at all, not merely skip UpdateAsync.");
    }

    [Test]
    public async Task SelectDefaultAsync_ConfiguredModel_PreservesExistingHttpPolicyAndInvalidatesCloudSelection()
    {
        var settings = new FakeNodeSettingsStore(new StoredNodeSettings
        {
            DefaultModelName = "old-cloud",
            CustomToolsEnabled = true
        });
        var harness = new Harness(settings);
        harness.CloudResolver.IsCloudModelAsync("old-cloud", Arg.Any<CancellationToken>()).Returns(true);

        var result = await harness.Service
                                  .SelectDefaultAsync("configured-only", LocalModelSelectionPolicy.ConfiguredModel);

        AssertEx.True(result.Succeeded);
        AssertEx.Equal("configured-only", result.SelectedModelName);
        await harness.GgufStore.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        AssertEx.Equal(expected: 1, settings.WriteCount);
        AssertEx.Equal("configured-only", settings.Current.DefaultModelName);
        AssertEx.Equal<bool?>(expected: true, settings.Current.CustomToolsEnabled, "an unrelated field must survive the selection write.");
        harness.CloudFactory.Received(1).InvalidateSelectionCache();
    }

    [Test]
    public async Task SelectDefaultAsync_WhenAnotherWriterLandsBetweenTheLoadAndTheWrite_KeepsItsFieldsAndReportsTheRealPrevious()
    {
        // Validation is async (it hits the GGUF store and the cloud resolver), so a whole-record save built from the
        // settings loaded before it would roll back everything another writer changed in that window — here a machine
        // key minted at boot. The previous name is read at write time for the same reason: the cache invalidation must
        // describe the transition that actually happened on disk, not the one this call expected.
        var settings = new FakeNodeSettingsStore(new StoredNodeSettings
            {
                DefaultModelName = "loaded-default"
            },
            siblingWriteBeforeTheUpdate: latest => latest with
            {
                MachineKey = "minted-while-validating",
                DefaultModelName = "selected-by-the-sibling"
            });
        var harness = new Harness(settings);

        var result = await harness.Service
                                  .SelectDefaultAsync("configured-only", LocalModelSelectionPolicy.ConfiguredModel);

        AssertEx.True(result.Succeeded);
        AssertEx.Equal("configured-only", settings.Current.DefaultModelName);
        AssertEx.Equal("minted-while-validating", settings.Current.MachineKey,
            "the key minted in the window must not be written back to null.");
        AssertEx.Equal("selected-by-the-sibling", result.PreviousModelName,
            "the previous name must come from the record at write time, not the one loaded before validation.");
    }

    [Test]
    public async Task SelectDefaultAsync_DecisionModel_IsRefusedAsNotChatWithoutSaving()
    {
        var settings = new FakeNodeSettingsStore(new StoredNodeSettings());
        var harness = new Harness(settings);
        harness.GgufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
               .Returns([Installed("org/Laya-GGUF:Q8_0", decisionType: "classify")]);

        var result = await harness.Service.SelectDefaultAsync("org/Laya-GGUF:Q8_0", LocalModelSelectionPolicy.ConfiguredModel);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.ModelKindNotChat, result.FailureCode);
        AssertEx.Equal(expected: 0, settings.WriteCount);
    }

    [Test]
    public async Task SelectDefaultAsync_PersistedEmbeddingModel_IsRefusedAsNotChat()
    {
        var settings = new FakeNodeSettingsStore(new StoredNodeSettings());
        var harness = new Harness(settings);
        harness.Classifications.GetByNameAsync("plain-model", Arg.Any<CancellationToken>())
               .Returns(Classification("plain-model", ModelKind.Embedding));

        var result = await harness.Service.SelectDefaultAsync("plain-model", LocalModelSelectionPolicy.ConfiguredModel);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.ModelKindNotChat, result.FailureCode);
        AssertEx.Equal(expected: 0, settings.WriteCount);
    }

    [Test]
    [Arguments(ModelKind.Chat)]
    [Arguments(ModelKind.Unknown)]
    public async Task SelectDefaultAsync_ChatOrUnknownKind_IsAccepted(ModelKind detectedKind)
    {
        var settings = new FakeNodeSettingsStore(new StoredNodeSettings());
        var harness = new Harness(settings);
        harness.GgufStore.ListInstalledModelsAsync(Arg.Any<CancellationToken>())
               .Returns([Installed("plain-model", decisionType: null)]);
        harness.Classifications.GetByNameAsync("plain-model", Arg.Any<CancellationToken>())
               .Returns(Classification("plain-model", detectedKind));

        var result = await harness.Service.SelectDefaultAsync("plain-model", LocalModelSelectionPolicy.ConfiguredModel);

        AssertEx.True(result.Succeeded);
        AssertEx.Equal("plain-model", settings.Current.DefaultModelName);
    }

    [Test]
    public async Task DeleteAsync_LlamaCppModel_UsesJournalledCoordinatorAndPurgesCommittedReceipt()
    {
        var harness = new Harness();
        harness.ProviderResolver.ResolveProviderNameForModelAsync("local", Arg.Any<CancellationToken>())
               .Returns(LlamaServerProviderConstants.ProviderName);
        var operationId = Guid.NewGuid();
        var receipt = new GgufDeletionStageReceipt(operationId, "local", [], [], [], "aliases", "members");
        var committed = new CommittedModelDeletion
        {
            OperationId = operationId,
            RequestedModelName = "local",
            RemovedModelNames = ["local"],
            StageReceipt = receipt
        };
        harness.DeletionCoordinator.CommitDeleteAsync("local", Arg.Any<CancellationToken>()).Returns(committed);

        var result = await harness.Service.DeleteAsync("local");

        AssertEx.True(result.Succeeded);
        AssertEx.True(result.Deleted);
        await harness.DeletionCoordinator.Received(1).PurgeAfterSuccessAsync(committed, CancellationToken.None);
    }

    [Test]
    public async Task DeleteAsync_WhenPostCommitPurgeFails_ReturnsLogicalSuccessForStartupReconciliation()
    {
        var harness = new Harness();
        harness.ProviderResolver.ResolveProviderNameForModelAsync("local", Arg.Any<CancellationToken>())
               .Returns(LlamaServerProviderConstants.ProviderName);
        var operationId = Guid.NewGuid();
        var receipt = new GgufDeletionStageReceipt(operationId, "local", [], [], [], "aliases", "members");
        var committed = new CommittedModelDeletion
        {
            OperationId = operationId,
            RequestedModelName = "local",
            RemovedModelNames = ["local"],
            StageReceipt = receipt
        };
        harness.DeletionCoordinator.CommitDeleteAsync("local", Arg.Any<CancellationToken>()).Returns(committed);
        harness.DeletionCoordinator.PurgeAfterSuccessAsync(committed, CancellationToken.None)
               .Returns(Task.FromException(new IOException("purge failed")));

        var result = await harness.Service.DeleteAsync("local");

        AssertEx.True(result.Succeeded);
        AssertEx.True(result.Deleted);
        await harness.DeletionCoordinator.Received(1).PurgeAfterSuccessAsync(committed, CancellationToken.None);
    }

    [Test]
    public async Task DeleteAsync_BlankName_IsRejectedBeforeProviderResolution()
    {
        var harness = new Harness();

        var result = await harness.Service.DeleteAsync("  ");

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.InvalidModelName, result.FailureCode);
        await harness.ProviderResolver.DidNotReceive()
                     .ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static LocalModelDescriptor Installed(string modelName, string? decisionType) =>
        new()
        {
            ModelName = modelName,
            ProviderName = LlamaServerProviderConstants.ProviderName,
            IsAvailable = true,
            SizeBytes = null,
            ModifiedAt = null,
            MaxContextTokens = null,
            DecisionType = decisionType
        };

    private static ModelClassificationRecord Classification(string modelName, ModelKind detectedKind) =>
        new()
        {
            ModelName = modelName,
            Digest = null,
            DetectedKind = detectedKind,
            DetectedCapabilitiesJson = null,
            OverrideKind = null,
            DetectedAtUtc = null,
            UpdatedAtUtc = 1
        };

    private sealed class Harness
    {
        public Harness(INodeSettingsStore? settings = null)
        {
            Settings = settings ?? Substitute.For<INodeSettingsStore>();
            var modelNameValidator = new ModelNameValidator(Options.Create(new SecurityOptions()));
            var scopeFactory = new ServiceCollection().AddSingleton(Classifications)
                                                      .BuildServiceProvider()
                                                      .GetRequiredService<IServiceScopeFactory>();
            var selectionPolicy = new DefaultModelSelectionPolicy(GgufStore, CloudResolver, CloudFactory, modelNameValidator, scopeFactory);
            Service = new LocalModelAdministrationService(DeletionCoordinator,
                ProviderResolver,
                Settings,
                selectionPolicy,
                modelNameValidator,
                NullLogger<LocalModelAdministrationService>.Instance);
        }

        public ILocalModelDeletionCoordinator DeletionCoordinator { get; } = Substitute.For<ILocalModelDeletionCoordinator>();
        public ILocalModelProviderResolver ProviderResolver { get; } = Substitute.For<ILocalModelProviderResolver>();
        public IGgufModelStore GgufStore { get; } = Substitute.For<IGgufModelStore>();
        public IModelClassificationStore Classifications { get; } = Substitute.For<IModelClassificationStore>();
        public INodeSettingsStore Settings { get; }
        public ICloudModelResolver CloudResolver { get; } = Substitute.For<ICloudModelResolver>();
        public IActiveCloudChatClientFactory CloudFactory { get; } = Substitute.For<IActiveCloudChatClientFactory>();
        public LocalModelAdministrationService Service { get; }
    }
}
