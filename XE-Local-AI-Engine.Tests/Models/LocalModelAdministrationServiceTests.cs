namespace XE_Local_AI_Engine.Tests.Models;

using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
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
        await harness.Settings.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task SelectDefaultAsync_ConfiguredModel_PreservesExistingHttpPolicyAndInvalidatesCloudSelection()
    {
        var harness = new Harness();
        harness.Settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings
        {
            DefaultModelName = "old-cloud",
            CustomToolsEnabled = true
        });
        harness.CloudResolver.IsCloudModelAsync("old-cloud", Arg.Any<CancellationToken>()).Returns(true);

        var result = await harness.Service
                                  .SelectDefaultAsync("configured-only", LocalModelSelectionPolicy.ConfiguredModel)
                                  .ConfigureAwait(false);

        AssertEx.True(result.Succeeded);
        AssertEx.Equal("configured-only", result.SelectedModelName);
        await harness.GgufStore.DidNotReceive().ExistsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await harness.Settings.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(settings =>
                settings.DefaultModelName == "configured-only" && settings.CustomToolsEnabled == true),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
        harness.CloudFactory.Received(1).InvalidateSelectionCache();
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
        public Harness()
        {
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
        public INodeSettingsStore Settings { get; } = Substitute.For<INodeSettingsStore>();
        public ICloudModelResolver CloudResolver { get; } = Substitute.For<ICloudModelResolver>();
        public IActiveCloudChatClientFactory CloudFactory { get; } = Substitute.For<IActiveCloudChatClientFactory>();
        public LocalModelAdministrationService Service { get; }
    }
}
