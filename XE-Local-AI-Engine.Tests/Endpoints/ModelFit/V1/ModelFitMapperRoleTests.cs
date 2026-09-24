namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Guards the operator wire contract for every role supported by the inference benchmark harness.</summary>
[Category(TestCategories.Unit)]
public sealed class ModelFitMapperRoleTests
{
    [Test]
    public void TryParseRole_WhenReranker_ReturnsReranker()
    {
        var role = ModelFitMapper.TryParseRole(" ReRaNkEr ");

        AssertEx.Equal(ModelRole.Reranker, role!.Value);
    }

    [Test]
    public void ToWireString_WhenReranker_ReturnsRerankerToken()
    {
        var token = ModelRole.Reranker.ToWireString();

        AssertEx.Equal("reranker", token);
    }

    [Test]
    [Arguments(true, false, "responsive")]
    [Arguments(false, false, "unresponsive")]
    [Arguments(false, true, "exited")]
    public void RunningModelToResponse_CarriesAStableDetailCodeTheSpaTranslates(bool responsive, bool exited, string expected)
    {
        var health = new LlamaServerProcessHealth
        {
            ModelName = "m",
            Role = ModelRole.Chat,
            IsResponsive = responsive,
            Detail = "free text",
            HasExited = exited
        };

        var response = health.ToResponse();

        AssertEx.Equal(expected, response.DetailCode);
        AssertEx.Equal("free text", response.Detail);
    }
}
