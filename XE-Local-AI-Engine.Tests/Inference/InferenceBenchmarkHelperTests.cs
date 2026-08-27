namespace XE_Local_AI_Engine.Tests.Inference;

using System.Net;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Inference;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class InferenceBenchmarkHelperTests
{
    [Test]
    public async Task EmbeddingProtocol_RestoresInputOrderFromResponseIndices()
    {
        using var handler = new JsonHandler("""
                                            {"data":[
                                              {"index":1,"embedding":[2.0]},
                                              {"index":0,"embedding":[1.0]}
                                            ]}
                                            """);
        using var client = new HttpClient(handler, disposeHandler: false);

        var vectors = await InferenceBenchmarkHttpProtocol.PostEmbeddingAsync(client,
            new Uri("http://localhost/v1/embeddings"),
            "model",
            ["first", "second"],
            CancellationToken.None);

        AssertEx.Equal(1d, vectors[0][0]);
        AssertEx.Equal(2d, vectors[1][0]);
    }

    [Test]
    public async Task RerankProtocol_RejectsDuplicateIndices()
    {
        using var handler = new JsonHandler("""
                                            {"results":[
                                              {"index":0,"relevance_score":0.9},
                                              {"index":0,"relevance_score":0.1}
                                            ]}
                                            """);
        using var client = new HttpClient(handler, disposeHandler: false);

        _ = await AssertEx.ThrowsAsync<InvalidDataException>(() =>
            InferenceBenchmarkHttpProtocol.PostRerankAsync(client,
                new Uri("http://localhost/v1/rerank"),
                "query",
                ["first", "second"],
                CancellationToken.None));
    }

    [Test]
    public void ResourceEvidence_DetectsOnlyMaterialGrowthBeyondLoadedBaseline()
    {
        var collector = new ResourceEvidenceCollector(preSpawnVram: new LlamaServerProfilingVramSnapshot(900, 1000),
            preSpawnAmbientBaselineBytes: 100,
            preSpawnPressureAbsoluteThresholdBytes: 50,
            preSpawnPressureRatioThreshold: 0.05,
            rejectPreSpawnVramPressure: true,
            incrementalAbsoluteThresholdBytes: 100,
            incrementalRatioThreshold: 0.05);

        collector.Add(new ResourceObservation(VramObservation.Create(700, 1000), WorkingSetBytes: 10));
        collector.Add(new ResourceObservation(VramObservation.Create(550, 1000), WorkingSetBytes: 20));

        AssertEx.False(AssertEx.NotNull(collector.PreSpawnVram).ExternalPressureDetected);
        AssertEx.True(collector.ExternalPressureDetected);
        AssertEx.Equal<long?>(550, collector.MinimumGlobalFreeBytes);
        AssertEx.Equal<long?>(20, collector.PeakWorkingSetBytes);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
