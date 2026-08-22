namespace XE_Local_AI_Engine.Tests.NodeSettings;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Configuration;
using XE_Local_AI_Engine.Client.Services.Capabilities;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Client.Services.NodeSettings.Implementation;
using XE_Local_AI_Engine.Client.Services.Validation;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class NodeSettingsAdministrationServiceTests
{
    [Test]
    public async Task ApplyAgenticPatchAsync_ChangesApprovedFieldsAndPreservesExcludedFields()
    {
        var store = Substitute.For<INodeSettingsStore>();
        var current = new StoredNodeSettings
        {
            DefaultModelName = "old",
            CustomToolsEnabled = true,
            OllamaEndpoint = "http://127.0.0.1:11434",
            MaxResponseSizeMb = 42,
            VoiceFeatureEnabled = true,
            ToolApprovalPolicy = new NodeToolApprovalPolicySettings()
        };
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(current);
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = " new ",
            EnableTools = false,
            ChatCacheReuse = 512
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated, "a valid agentic patch must be saved.");
        AssertEx.Equal("new", result.Settings.DefaultModelName);
        AssertEx.Equal(false, result.Settings.EnableTools);
        AssertEx.Equal(512, result.Settings.ChatCacheReuse);
        AssertEx.Equal(true, result.Settings.CustomToolsEnabled);
        AssertEx.Equal("http://127.0.0.1:11434", result.Settings.OllamaEndpoint);
        AssertEx.Equal(42, result.Settings.MaxResponseSizeMb);
        AssertEx.Equal(true, result.Settings.VoiceFeatureEnabled);
        AssertEx.NotNull(result.Settings.ToolApprovalPolicy);
        await store.Received(1).SaveAsync(Arg.Is<StoredNodeSettings>(saved =>
                saved.CustomToolsEnabled == current.CustomToolsEnabled
                && saved.OllamaEndpoint == current.OllamaEndpoint
                && saved.MaxResponseSizeMb == current.MaxResponseSizeMb
                && saved.VoiceFeatureEnabled == current.VoiceFeatureEnabled
                && ReferenceEquals(saved.ToolApprovalPolicy, current.ToolApprovalPolicy)),
            Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public void AgenticPatch_IsStructurallyLimitedToApprovedFields()
    {
        var names = typeof(NodeSettingsAgenticPatch).GetProperties().Select(static property => property.Name).ToHashSet(StringComparer.Ordinal);
        string[] approved =
        [
            "DefaultModelName", "EnableTools", "ToolCapableModels", "HuggingFaceDefaultQuant",
            "LlamaMaxLoadedProcesses", "LlamaIdleTimeToLiveSeconds", "KeepModelWarmEnabled",
            "KeepModelWarmModelName", "KeepModelWarmIntervalSeconds", "MaxMessageRequestTimeoutSeconds",
            "ChatCacheReuse", "SpeculativeMode", "SpeculativeDraftModelName", "SpeculativeDraftMaxTokens",
            "SpeculativeDraftGpuLayers", "RerankerModelName"
        ];

        AssertEx.Equal(approved.Length, names.Count);
        AssertEx.True(names.SetEquals(approved), "the agentic patch must expose exactly the approved 16 fields.");
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.CustomToolsEnabled)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.ToolApprovalPolicy)));
        AssertEx.False(names.Contains(nameof(StoredNodeSettings.OllamaEndpoint)));
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenMergedPolicyRejects_DoesNotSave()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            KeepModelWarmEnabled = true,
            KeepModelWarmModelName = " "
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(1, result.ValidationErrors.Count);
        AssertEx.Equal(NodeSettingsField.KeepModelWarmModelName, result.ValidationErrors[0].Field);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenFieldRangeIsInvalid_DoesNotNormalizeSaveOrReport()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var reporter = Substitute.For<ICapabilityReporter>();
        var service = CreateService(store, reporter: reporter);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ChatCacheReuse = StoredNodeSettings.MaxChatCacheReuse + 1
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.ChatCacheReuse, result.ValidationErrors[0].Field);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
        await reporter.DidNotReceive().ReportToApiAsync(Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_WhenToolCapableModelsIsEmpty_DoesNotSave()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings());
        var service = CreateService(store);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            ToolCapableModels = []
        }).ConfigureAwait(false);

        AssertEx.False(result.Updated);
        AssertEx.Equal(NodeSettingsField.ToolCapableModels, result.ValidationErrors[0].Field);
        await store.DidNotReceive().SaveAsync(Arg.Any<StoredNodeSettings>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task ApplyAgenticPatchAsync_DefaultModelCloudTransition_UsesSharedCacheInvalidationPolicy()
    {
        var store = Substitute.For<INodeSettingsStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new StoredNodeSettings { DefaultModelName = "old-cloud" });
        var cloudResolver = Substitute.For<ICloudModelResolver>();
        cloudResolver.IsCloudModelAsync("old-cloud", Arg.Any<CancellationToken>()).Returns(true);
        var cloudFactory = Substitute.For<IActiveCloudChatClientFactory>();
        var service = CreateService(store, cloudResolver: cloudResolver, cloudFactory: cloudFactory);

        var result = await service.ApplyAgenticPatchAsync(new NodeSettingsAgenticPatch
        {
            DefaultModelName = "local-model"
        }).ConfigureAwait(false);

        AssertEx.True(result.Updated);
        AssertEx.Equal("local-model", result.Settings.DefaultModelName);
        cloudFactory.Received(1).InvalidateSelectionCache();
    }

    private static NodeSettingsAdministrationService CreateService(INodeSettingsStore store,
        ICapabilityReporter? reporter = null,
        ICloudModelResolver? cloudResolver = null,
        IActiveCloudChatClientFactory? cloudFactory = null)
    {
        var runtime = Substitute.For<INodeRuntimeSettings>();
        reporter ??= Substitute.For<ICapabilityReporter>();
        reporter.ReportToApiAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        cloudResolver ??= Substitute.For<ICloudModelResolver>();
        cloudFactory ??= Substitute.For<IActiveCloudChatClientFactory>();
        var selectionPolicy = new DefaultModelSelectionPolicy(Substitute.For<IGgufModelStore>(),
            cloudResolver,
            cloudFactory,
            new ModelNameValidator(Options.Create(new SecurityOptions())));
        return new NodeSettingsAdministrationService(store,
            runtime,
            reporter,
            selectionPolicy,
            NullLogger<NodeSettingsAdministrationService>.Instance);
    }
}
