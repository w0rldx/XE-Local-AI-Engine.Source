namespace XE_Local_AI_Engine.Tests.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class LocalModelAdministrationServiceTests
{
    [Test]
    public async Task SelectDefaultAsync_InstalledLocalOnly_RejectsMissingModelWithoutSaving()
    {
        var harness = new Harness();
        harness.GgufStore.ExistsAsync("missing", Arg.Any<CancellationToken>()).Returns(false);

        var result = await harness.Service
                                  .SelectDefaultAsync("missing", LocalModelSelectionPolicy.InstalledLocalOnly)
                                  .ConfigureAwait(false);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.ModelNotInstalled, result.FailureCode);
        await harness.Settings.DidNotReceive()
                     .UpdateAsync(Arg.Any<Func<StoredNodeSettings, StoredNodeSettings>>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
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
                                  .SelectDefaultAsync("configured-only", LocalModelSelectionPolicy.ConfiguredModel)
                                  .ConfigureAwait(false);

        AssertEx.True(result.Succeeded);
        AssertEx.Equal("configured-only", result.SelectedModelName);
        await harness.GgufStore.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
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
                                  .SelectDefaultAsync("configured-only", LocalModelSelectionPolicy.ConfiguredModel)
                                  .ConfigureAwait(false);

        AssertEx.True(result.Succeeded);
        AssertEx.Equal("configured-only", settings.Current.DefaultModelName);
        AssertEx.Equal("minted-while-validating", settings.Current.MachineKey,
            "the key minted in the window must not be written back to null.");
        AssertEx.Equal("selected-by-the-sibling", result.PreviousModelName,
            "the previous name must come from the record at write time, not the one loaded before validation.");
    }

    [Test]
    public async Task DeleteAsync_LlamaCppModel_UsesJournalledCoordinatorAndPurgesCommittedReceipt()
    {
        var harness = new Harness();
        harness.ProviderResolver.ResolveProviderNameForModelAsync("local", Arg.Any<CancellationToken>())
               .Returns(LlamaServerProviderConstants.ProviderName);
        var operationId = Guid.NewGuid();
        var receipt = new GgufDeletionStageReceipt(operationId, "local", [], [], [], "aliases", "members");
        var committed = new CommittedModelDeletion(operationId, "local", ["local"], receipt);
        harness.DeletionCoordinator.CommitDeleteAsync("local", Arg.Any<CancellationToken>()).Returns(committed);

        var result = await harness.Service.DeleteAsync("local").ConfigureAwait(false);

        AssertEx.True(result.Succeeded);
        AssertEx.True(result.Deleted);
        await harness.DeletionCoordinator.Received(1).PurgeAfterSuccessAsync(committed, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_WhenPostCommitPurgeFails_ReturnsLogicalSuccessForStartupReconciliation()
    {
        var harness = new Harness();
        harness.ProviderResolver.ResolveProviderNameForModelAsync("local", Arg.Any<CancellationToken>())
               .Returns(LlamaServerProviderConstants.ProviderName);
        var operationId = Guid.NewGuid();
        var receipt = new GgufDeletionStageReceipt(operationId, "local", [], [], [], "aliases", "members");
        var committed = new CommittedModelDeletion(operationId, "local", ["local"], receipt);
        harness.DeletionCoordinator.CommitDeleteAsync("local", Arg.Any<CancellationToken>()).Returns(committed);
        harness.DeletionCoordinator.PurgeAfterSuccessAsync(committed, CancellationToken.None)
               .Returns(Task.FromException(new IOException("purge failed")));

        var result = await harness.Service.DeleteAsync("local").ConfigureAwait(false);

        AssertEx.True(result.Succeeded);
        AssertEx.True(result.Deleted);
        await harness.DeletionCoordinator.Received(1).PurgeAfterSuccessAsync(committed, CancellationToken.None).ConfigureAwait(false);
    }

    [Test]
    public async Task DeleteAsync_BlankName_IsRejectedBeforeProviderResolution()
    {
        var harness = new Harness();

        var result = await harness.Service.DeleteAsync("  ").ConfigureAwait(false);

        AssertEx.False(result.Succeeded);
        AssertEx.Equal(LocalModelAdministrationFailureCodes.InvalidModelName, result.FailureCode);
        await harness.ProviderResolver.DidNotReceive()
                     .ResolveProviderNameForModelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    private sealed class Harness
    {
        public Harness(INodeSettingsStore? settings = null)
        {
            Settings = settings ?? Substitute.For<INodeSettingsStore>();
            var modelNameValidator = new ModelNameValidator(Options.Create(new SecurityOptions()));
            var selectionPolicy = new DefaultModelSelectionPolicy(GgufStore, CloudResolver, CloudFactory, modelNameValidator);
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
        public INodeSettingsStore Settings { get; }
        public ICloudModelResolver CloudResolver { get; } = Substitute.For<ICloudModelResolver>();
        public IActiveCloudChatClientFactory CloudFactory { get; } = Substitute.For<IActiveCloudChatClientFactory>();
        public LocalModelAdministrationService Service { get; }
    }
}
