namespace XE_Local_AI_Engine.Tests.Models;

using NSubstitute;
using XE_Local_AI_Engine.Client.Services.ExternalProviders;
using XE_Local_AI_Engine.Client.Services.Models;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The shared node-locality gate of the background model steps: only a scheme-less id the trust resolver calls Local
///     runs; anything else is skipped with one warning naming the model and the setting.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class BackgroundModelLocalityGuardTests
{
    [Test]
    [Arguments("gpt-5-codex", ModelTrustLocality.Cloud)]
    [Arguments("ext:unknown/model", ModelTrustLocality.Unresolved)]
    [Arguments("ext:lan-box/llama", ModelTrustLocality.Local)]
    public async Task AllowAsync_NonLocalModel_RefusesAndWarnsWithModelAndSetting(string modelName, ModelTrustLocality locality)
    {
        var trust = Substitute.For<IModelTrustResolver>();
        trust.ResolveAsync(modelName, Arg.Any<CancellationToken>()).Returns(locality);
        var logger = new CapturingLogger<BackgroundModelLocalityGuardTests>();

        var allowed = await BackgroundModelLocalityGuard.AllowAsync(modelName, "MemoryExtractionModelName", trust, logger, CancellationToken.None);

        AssertEx.False(allowed);
        AssertEx.Contains(logger.AllText, "Skipped a background step");
        AssertEx.Contains(logger.AllText, modelName);
        AssertEx.Contains(logger.AllText, "MemoryExtractionModelName");
    }

    [Test]
    public async Task AllowAsync_NodeLocalModel_AllowsWithoutWarning()
    {
        var trust = Substitute.For<IModelTrustResolver>();
        trust.ResolveAsync("qwen3:8b", Arg.Any<CancellationToken>()).Returns(ModelTrustLocality.Local);
        var logger = new CapturingLogger<BackgroundModelLocalityGuardTests>();

        var allowed = await BackgroundModelLocalityGuard.AllowAsync("qwen3:8b", "PlaybookEvalModelName", trust, logger, CancellationToken.None);

        AssertEx.True(allowed);
        AssertEx.Equal(string.Empty, logger.AllText);
    }
}
