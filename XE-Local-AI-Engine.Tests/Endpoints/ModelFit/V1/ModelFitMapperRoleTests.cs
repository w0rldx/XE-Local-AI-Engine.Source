namespace XE_Local_AI_Engine.Tests.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>Guards the operator wire contract for every role supported by the inference benchmark harness.</summary>
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
}
