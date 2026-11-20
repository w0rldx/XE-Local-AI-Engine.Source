namespace XE_Local_AI_Engine.Tests.NodeSettings;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;
using XE_Local_AI_Engine.Tests.Testing.Builders;

/// <summary>
///     The cross-field save policy runs on the MERGED settings: it rejects a draft-* mode without a draft model and
///     keep-warm without a model, and reads an unset slot or TTL knob as that record's effective value.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class NodeSettingsPolicyTests
{
    [Test]
    public async Task DraftSpeculativeMode_WithoutDraftModel_IsRejected()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
        {
            SpeculativeMode = "draft-simple"
        });

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.SpeculativeDraftModelName
                     && error.Message == "Speculative decoding is set to a draft model mode, but no draft model was selected.");
    }

    [Test]
    public async Task DraftSpeculativeMode_WithDraftModel_IsAccepted()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
        {
            SpeculativeMode = "draft-simple",
            SpeculativeDraftModelName = "draft.gguf"
        });

        AssertEx.Empty(errors);
    }

    [Test]
    public async Task KeepWarmEnabled_WithoutModel_IsRejected()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
        {
            KeepModelWarmEnabled = true
        });

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.KeepModelWarmModelName
                     && error.Message == "Keep model warm is enabled, but no model was selected.");
    }

    [Test]
    public async Task KeepWarmEnabled_UsesTheEffectiveProcessSlotCount_WhenTheRequestOmitsIt()
    {
        // The request omits the cap, so the merged record carries the stored 1 — and one slot is not enough to keep a model
        // warm and still admit another.
        var stored = new StoredNodeSettings
        {
            LlamaMaxLoadedProcesses = 1
        };
        var errors = await ValidateAsync(stored with
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf"
            },
            stored);

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.LlamaMaxLoadedProcesses
                     && error.Message == "Keep model warm requires at least two loaded-process slots so another local model can still be admitted.");
    }

    [Test]
    public async Task KeepWarmEnabled_ExplicitProcessSlotCount_OverridesTheEffectiveValue()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf",
                LlamaMaxLoadedProcesses = 2
            },
            new StoredNodeSettings
            {
                LlamaMaxLoadedProcesses = 1
            });

        AssertEx.Empty(errors);
    }

    [Test]
    public async Task KeepWarmInterval_NotShorterThanTheEffectiveIdleTimeToLive_IsRejected()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf",
                KeepModelWarmIntervalSeconds = 600,
                LlamaIdleTimeToLiveSeconds = 600
            });

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.KeepModelWarmIntervalSeconds
                     && error.Message == "The keep-model-warm interval must be shorter than the llama.cpp idle time-to-live.");
    }

    [Test]
    public async Task KeepWarmDisabled_SkipsTheRuntimeBackedRulesEntirely()
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = false
            },
            runtimeSettings,
            CancellationToken.None);

        AssertEx.Empty(errors);
        // A call-shape pin: the keep-warm rules are the only readers of the effective values here, so none is resolved.
        runtimeSettings.DidNotReceive().ResolveEffectiveValues(Arg.Any<StoredNodeSettings>());
    }

    /// <summary>An interval cleared by the save resolves to the default (300 s), which a 60 s TTL no longer outlasts.</summary>
    [Test]
    public async Task KeepWarmEnabled_AnIntervalClearedFromAStoredOne_IsJudgedAsTheDefault()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf",
                LlamaIdleTimeToLiveSeconds = 60
            },
            new StoredNodeSettings
            {
                KeepModelWarmIntervalSeconds = 20
            });

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.KeepModelWarmIntervalSeconds
                     && error.Message == "The keep-model-warm interval must be shorter than the llama.cpp idle time-to-live.");
    }

    /// <summary>Clearing both resolves both to their defaults (300 s against 900 s), so the save is valid whatever was stored.</summary>
    [Test]
    public async Task KeepWarmEnabled_ClearingTheTtlAndTheInterval_IsJudgedAsBothDefaults()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf"
            },
            new StoredNodeSettings
            {
                KeepModelWarmIntervalSeconds = 1200,
                LlamaIdleTimeToLiveSeconds = 3600
            });

        AssertEx.Empty(errors);
    }

    /// <summary>A cap cleared by the save resolves to the default the save applies, not to the stored value it replaces.</summary>
    [Test]
    public async Task KeepWarmEnabled_ACapClearedFromAStoredOne_IsJudgedAsTheDefault()
    {
        var runtimeSettings = SeededNodeRuntimeSettings.Create(stored: static () => new StoredNodeSettings
        {
            LlamaMaxLoadedProcesses = 1
        });

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf"
            },
            runtimeSettings,
            CancellationToken.None);

        AssertEx.Empty(errors);
    }

    /// <summary>A TTL cleared by the save resolves to the default (900 s), not to the stored 3600 s.</summary>
    [Test]
    public async Task KeepWarmEnabled_ATtlClearedFromAStoredOne_IsJudgedAsTheDefault()
    {
        var runtimeSettings = SeededNodeRuntimeSettings.Create(stored: static () => new StoredNodeSettings
        {
            LlamaIdleTimeToLiveSeconds = 3600
        });

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf",
                KeepModelWarmIntervalSeconds = 1200
            },
            runtimeSettings,
            CancellationToken.None);

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.KeepModelWarmIntervalSeconds
                     && error.Message == "The keep-model-warm interval must be shorter than the llama.cpp idle time-to-live.");
    }

    /// <summary>
    ///     A save that clears the stored list leaves the runtime on the configuration seed, so the AgentHome rule judges the
    ///     seed rather than the list the save is about to remove.
    /// </summary>
    [Test]
    public async Task AgentHomeOn_ClearingTheStoredToolList_IsRejected_WhenTheSeedIsEmpty()
    {
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithAgentHomeEnabled(true).WithToolCapableModels("stored.gguf").WithToolCapableModelsSeed().Build();

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(new StoredNodeSettings
        {
            ToolCapableModels = []
        }, runtimeSettings, CancellationToken.None);

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.ToolCapableModels
                     && error.Message == "AgentHome needs at least one tool-capable model. Add one, or turn AgentHome off.");
    }

    [Test]
    public async Task AgentHomeOn_ClearingTheStoredToolList_IsAccepted_WhenTheSeedNamesAModel()
    {
        var runtimeSettings = StubNodeRuntimeSettings.Create().WithAgentHomeEnabled(true).WithToolCapableModels("stored.gguf").WithToolCapableModelsSeed("seed.gguf").Build();

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(new StoredNodeSettings
        {
            ToolCapableModels = []
        }, runtimeSettings, CancellationToken.None);

        AssertEx.Empty(errors);
    }

    // The real resolution (stored > seed > default) over the stored record the merged one replaces.
    private static async Task<IReadOnlyList<NodeSettingsValidationError>> ValidateAsync(StoredNodeSettings merged, StoredNodeSettings? stored = null)
    {
        var runtimeSettings = SeededNodeRuntimeSettings.Create(stored: () => stored ?? new StoredNodeSettings());
        return await NodeSettingsPolicy.ValidateMergedAsync(merged, runtimeSettings, CancellationToken.None);
    }
}
