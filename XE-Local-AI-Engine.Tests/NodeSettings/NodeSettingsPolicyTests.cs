namespace XE_Local_AI_Engine.Tests.NodeSettings;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The cross-field save policy runs on the MERGED settings, so it must reject a draft-* mode with no draft model
///     and a keep-warm-enabled state with no model, and it must fall back to the EFFECTIVE runtime value (from
///     <see cref="INodeRuntimeSettings" />) for the process-slot and interval rules when the request omitted the knob.
/// </summary>
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
        // The request left LlamaMaxLoadedProcesses null, so the stored/effective value (1) decides — and one slot is
        // not enough to keep a model warm and still admit another.
        var errors = await ValidateAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf"
            },
            maxLoadedProcesses: 1);

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
            maxLoadedProcesses: 1);

        AssertEx.Empty(errors);
    }

    [Test]
    public async Task KeepWarmInterval_NotShorterThanTheEffectiveIdleTimeToLive_IsRejected()
    {
        var errors = await ValidateAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = true,
                KeepModelWarmModelName = "warm.gguf",
                KeepModelWarmIntervalSeconds = 600
            },
            idleTimeToLive: TimeSpan.FromSeconds(600));

        AssertEx.ContainsSingle(errors,
            error => error.Field == NodeSettingsField.KeepModelWarmIntervalSeconds
                     && error.Message == "The keep-model-warm interval must be shorter than the llama.cpp idle time-to-live.");
    }

    [Test]
    public async Task KeepWarmDisabled_SkipsTheRuntimeBackedRulesEntirely()
    {
        var runtimeSettings = CreateRuntimeSettings(maxLoadedProcesses: 1,
            keepWarmInterval: TimeSpan.FromHours(1),
            idleTimeToLive: TimeSpan.FromSeconds(1));

        var errors = await NodeSettingsPolicy.ValidateMergedAsync(new StoredNodeSettings
            {
                KeepModelWarmEnabled = false
            },
            runtimeSettings,
            CancellationToken.None);

        AssertEx.Empty(errors);
        await runtimeSettings.DidNotReceive().GetLlamaMaxLoadedProcessesAsync(Arg.Any<CancellationToken>());
    }

    private static async Task<IReadOnlyList<NodeSettingsValidationError>> ValidateAsync(StoredNodeSettings settings,
        int maxLoadedProcesses = 4,
        TimeSpan? keepWarmInterval = null,
        TimeSpan? idleTimeToLive = null)
    {
        var runtimeSettings = CreateRuntimeSettings(maxLoadedProcesses,
            keepWarmInterval ?? TimeSpan.FromSeconds(60),
            idleTimeToLive ?? TimeSpan.FromSeconds(900));

        return await NodeSettingsPolicy.ValidateMergedAsync(settings, runtimeSettings, CancellationToken.None);
    }

    private static INodeRuntimeSettings CreateRuntimeSettings(int maxLoadedProcesses,
        TimeSpan keepWarmInterval,
        TimeSpan idleTimeToLive)
    {
        var runtimeSettings = Substitute.For<INodeRuntimeSettings>();
        runtimeSettings.GetLlamaMaxLoadedProcessesAsync(Arg.Any<CancellationToken>()).Returns(maxLoadedProcesses);
        runtimeSettings.GetKeepModelWarmIntervalAsync(Arg.Any<CancellationToken>()).Returns(keepWarmInterval);
        runtimeSettings.GetLlamaIdleTimeToLiveAsync(Arg.Any<CancellationToken>()).Returns(idleTimeToLive);
        return runtimeSettings;
    }
}
