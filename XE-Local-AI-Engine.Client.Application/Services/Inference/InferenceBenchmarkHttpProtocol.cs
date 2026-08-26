namespace XE_Local_AI_Engine.Client.Services.Inference;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>llama-server HTTP contracts and index-preserving response parsing for pooled benchmark roles.</summary>
internal static class InferenceBenchmarkHttpProtocol
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static Uri BuildRoleUri(Uri baseAddress, string route) =>
        new($"{baseAddress.AbsoluteUri.TrimEnd('/')}/{route}");

    public static async Task<IReadOnlyList<IReadOnlyList<double>>> PostEmbeddingAsync(HttpClient client,
        Uri endpoint,
        string modelName,
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(endpoint, new EmbeddingRequest(modelName, inputs), SerializerOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(SerializerOptions, ct).ConfigureAwait(false);
        if (payload?.Data is null || payload.Data.Count != inputs.Count)
        {
            throw new InvalidDataException("Embedding response did not contain one vector per input.");
        }

        var ordered = payload.Data.OrderBy(static item => item.Index).ToArray();
        if (ordered.Where(static (item, position) => item.Index != position).Any())
        {
            throw new InvalidDataException("Embedding response indices were incomplete or duplicated.");
        }

        return ordered.Select(static item => item.Embedding).ToArray();
    }

    public static async Task<IReadOnlyList<double>> PostRerankAsync(HttpClient client,
        Uri endpoint,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(endpoint, new RerankRequest(query, documents), SerializerOptions, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<RerankResponse>(SerializerOptions, ct).ConfigureAwait(false);
        if (payload?.Results is null || payload.Results.Count != documents.Count)
        {
            throw new InvalidDataException("Reranker response did not contain one score per document.");
        }

        var scores = new double[documents.Count];
        var assigned = new bool[documents.Count];
        foreach (var result in payload.Results)
        {
            if (result.Index < 0 || result.Index >= documents.Count || assigned[result.Index])
            {
                throw new InvalidDataException("Reranker response indices were incomplete or duplicated.");
            }

            scores[result.Index] = result.RelevanceScore;
            assigned[result.Index] = true;
        }

        return scores;
    }

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")]
        string Model,
        [property: JsonPropertyName("input")]
        IReadOnlyList<string> Input);

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")]
        IReadOnlyList<EmbeddingResult>? Data);

    private sealed record EmbeddingResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("embedding")]
        IReadOnlyList<double> Embedding);

    private sealed record RerankRequest(
        [property: JsonPropertyName("query")]
        string Query,
        [property: JsonPropertyName("documents")]
        IReadOnlyList<string> Documents);

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")]
        IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("relevance_score")]
        double RelevanceScore);
}
