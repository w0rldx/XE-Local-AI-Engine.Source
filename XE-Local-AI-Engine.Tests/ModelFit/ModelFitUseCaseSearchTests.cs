namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="ModelFitUseCaseSearch" /> tests: a use case maps to Hub search terms that surface good GGUF
///     models (the literal word under-matches). Known use cases map to curated terms (any case), unknown ones
///     to their own text, null/blank to <c>instruct</c>; the list is never empty.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ModelFitUseCaseSearchTests
{
    [Test]
    public void Resolve_Coding_MapsToCoderAndCode_NotTheLiteralWordCoding()
    {
        // "coding" as a Hub substring misses "Coder"/"code" models; the curated terms surface them.
        AssertEx.Equal("coder,code", string.Join(",", ModelFitUseCaseSearch.Resolve("coding")));
    }

    [Test]
    public void Resolve_KnownUseCases_MapToCuratedTerms()
    {
        AssertEx.Equal("instruct", string.Join(",", ModelFitUseCaseSearch.Resolve("general")));
        AssertEx.Equal("instruct,chat", string.Join(",", ModelFitUseCaseSearch.Resolve("chat")));
        AssertEx.Equal("reasoning,instruct", string.Join(",", ModelFitUseCaseSearch.Resolve("reasoning")));
        AssertEx.Equal("vl,vision", string.Join(",", ModelFitUseCaseSearch.Resolve("multimodal")));
        AssertEx.Equal("embedding", string.Join(",", ModelFitUseCaseSearch.Resolve("embedding")));
    }

    [Test]
    public void Resolve_IsCaseInsensitive()
    {
        AssertEx.Equal("coder,code", string.Join(",", ModelFitUseCaseSearch.Resolve("CODING")));
    }

    [Test]
    public void Resolve_UnknownUseCase_FallsBackToTheVerbatimTerm()
    {
        AssertEx.Equal("rust", string.Join(",", ModelFitUseCaseSearch.Resolve("  rust  ")));
    }

    [Test]
    public void Resolve_NullOrBlank_FallsBackToInstruct()
    {
        AssertEx.Equal("instruct", string.Join(",", ModelFitUseCaseSearch.Resolve(useCase: null)));
        AssertEx.Equal("instruct", string.Join(",", ModelFitUseCaseSearch.Resolve("   ")));
    }
}
