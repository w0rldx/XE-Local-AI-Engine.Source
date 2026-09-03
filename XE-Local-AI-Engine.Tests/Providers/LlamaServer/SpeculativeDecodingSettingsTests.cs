namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Mode-classification coverage for <see cref="SpeculativeDecodingSettings" />.</summary>
public sealed class SpeculativeDecodingSettingsTests
{
    [Test]
    [Arguments("draft-dflash")]
    [Arguments("draft-dspark")]
    public void DflashAndDspark_AreExternalDraftAndRequireADraftModel(string mode)
    {
        // Both load a SECOND GGUF, so they must land in the same class as draft-simple: the existing
        // --spec-draft-model / --spec-draft-ngl plumbing then covers them with no mode-specific field.
        AssertEx.True(SpeculativeDecodingSettings.IsAllowedMode(mode), $"'{mode}' must be an exposed --spec-type value.");
        AssertEx.Equal<SpeculativeModeClass?>(SpeculativeModeClass.ExternalDraft, SpeculativeDecodingSettings.ClassOf(mode));
        AssertEx.True(SpeculativeDecodingSettings.ModeRequiresDraftModel(mode),
            $"'{mode}' runs a second GGUF, so the settings boundary must require a draft model.");

        var withoutDraft = new SpeculativeDecodingSettings(mode, DraftModelPath: null, DraftMaxTokens: 3, DraftGpuLayers: null);
        AssertEx.True(withoutDraft.RequiresExternalDraftModel);
        AssertEx.False(withoutDraft.TryValidate(out var error), $"'{mode}' must not validate without a draft model path.");
        AssertEx.NotNull(error);

        var withDraft = new SpeculativeDecodingSettings(mode, "/models/draft.gguf", DraftMaxTokens: 15, DraftGpuLayers: null);
        AssertEx.True(withDraft.TryValidate(out var draftError), $"'{mode}' must validate once a draft model path is set.");
        AssertEx.Null(draftError);
        AssertEx.True(withDraft.IsEnabled);
    }

    [Test]
    [Arguments("DRAFT-DFLASH", "draft-dflash")]
    [Arguments("Draft-DSpark", "draft-dspark")]
    public void NormalizedMode_ForTheNewModes_ForgivesOperatorCasing(string input, string expected)
    {
        var settings = new SpeculativeDecodingSettings(input, "/models/draft.gguf", DraftMaxTokens: 7, DraftGpuLayers: null);
        AssertEx.Equal(expected, settings.NormalizedMode);
    }
}
