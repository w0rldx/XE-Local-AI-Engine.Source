namespace XE_Local_AI_Engine.Tests.CodexOAuth;

using XE_Local_AI_Engine.Providers.CodexOAuth.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>The Codex context-window table: the sourced ids carry the catalog's default window, anything else is unknown.</summary>
[Category(TestCategories.Unit)]
public sealed class CodexModelCatalogTests
{
    [Test]
    [Arguments("gpt-5.6-sol")]
    [Arguments("gpt-5.6-terra")]
    [Arguments("GPT-5.6-LUNA")]
    public void ContextTokensFor_ASourcedId_IsTheCatalogDefaultWindow(string modelId)
    {
        AssertEx.Equal(272_000, CodexModelCatalog.ContextTokensFor(modelId));
    }

    [Test]
    [Arguments("gpt-5.5")]
    [Arguments("not-a-codex-model")]
    [Arguments(null)]
    public void ContextTokensFor_AnUnsourcedOrUnknownId_IsNull(string? modelId)
    {
        AssertEx.Null(CodexModelCatalog.ContextTokensFor(modelId));
    }
}
