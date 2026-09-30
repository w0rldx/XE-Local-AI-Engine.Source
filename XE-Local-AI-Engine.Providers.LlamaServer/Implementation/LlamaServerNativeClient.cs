namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Production <see cref="ILlamaServerNativeClient" />.</summary>
/// <remarks>
///     Metrics, props and the pooled-role POSTs use the host's default factory client, created per call. Tokenize uses
///     (and the template-rendering count) its own long-lived client whose handler follows no redirect and uses no ambient proxy, because its caller runs
///     unattended against an endpoint captured long before the request and must never be steered elsewhere.
/// </remarks>
internal sealed class LlamaServerNativeClient : ILlamaServerNativeClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpClient _tokenizeClient;

    /// <param name="httpClientFactory">Source of the default client for every route but <c>/tokenize</c>.</param>
    /// <param name="tokenizeClient">The <c>/tokenize</c> transport; production passes one over <see cref="CreateTokenizeHandler" />.</param>
    public LlamaServerNativeClient(IHttpClientFactory httpClientFactory, HttpClient tokenizeClient)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(tokenizeClient);
        _httpClientFactory = httpClientFactory;
        _tokenizeClient = tokenizeClient;
    }

    public async Task<string> GetMetricsTextAsync(Uri baseAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        using var client = _httpClientFactory.CreateClient();
        return await client.GetStringAsync(new Uri(baseAddress, "/metrics"), ct).ConfigureAwait(false);
    }

    public async Task<LlamaServerProps?> GetPropsAsync(Uri baseAddress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        using var client = _httpClientFactory.CreateClient();
        using var response = await client.GetAsync(new Uri(baseAddress, "/props"), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await LlamaServerProps.ReadAsync(stream, ct).ConfigureAwait(false);
    }

    public async Task<LlamaServerTokenizeResponse> TokenizeAsync(Uri baseAddress, string content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(content);

        // /tokenize is a sibling of /health at the server root, never under /v1.
        var tokenizeUri = new Uri($"{baseAddress.Scheme}://{baseAddress.Authority}/tokenize");
        var response = await _tokenizeClient.PostAsJsonAsync(tokenizeUri,
                                                new
                                                {
                                                    content,
                                                    add_special = false,
                                                    parse_special = false,
                                                    with_pieces = false
                                                },
                                                ct)
                                            .ConfigureAwait(false);
        return new LlamaServerTokenizeResponse(response);
    }

    public async Task<LlamaServerTokenizeResponse> CountPromptTokensAsync(Uri baseAddress,
        string systemPrompt,
        string userMessage,
        IReadOnlyList<AIFunctionDeclaration> tools,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(userMessage);
        ArgumentNullException.ThrowIfNull(tools);

        // Anthropic-shaped: llama-server converts it to its OpenAI chat parameters and renders the template, so the count is the prompt the model sees.
        var toolArray = new JsonArray();
        foreach (var tool in tools)
        {
            toolArray.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.JsonSchema.GetRawText())
            });
        }

        var body = new JsonObject
        {
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = userMessage
            }),
            ["tools"] = toolArray
        };
        var countUri = new Uri($"{baseAddress.Scheme}://{baseAddress.Authority}/v1/messages/count_tokens");
        var response = await _tokenizeClient.PostAsJsonAsync(countUri, body, ct).ConfigureAwait(false);
        return new LlamaServerTokenizeResponse(response);
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> PostEmbeddingsAsync(Uri baseAddress,
        string modelName,
        IReadOnlyList<string> inputs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(inputs);
        using var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsJsonAsync(RoleUri(baseAddress, "embeddings"),
                                             new EmbeddingRequest
                                             {
                                                 Model = modelName,
                                                 Input = inputs
                                             },
                                             SerializerOptions,
                                             ct)
                                         .ConfigureAwait(false);
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

    public async Task<IReadOnlyList<double>> PostRerankAsync(Uri baseAddress,
        string query,
        IReadOnlyList<string> documents,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentNullException.ThrowIfNull(documents);
        using var client = _httpClientFactory.CreateClient();
        using var response = await client.PostAsJsonAsync(RoleUri(baseAddress, "rerank"),
                                             new RerankRequest
                                             {
                                                 Query = query,
                                                 Documents = documents
                                             },
                                             SerializerOptions,
                                             ct)
                                         .ConfigureAwait(false);
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

    internal static HttpClientHandler CreateTokenizeHandler()
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            CheckCertificateRevocationList = true
        };
    }

    private static Uri RoleUri(Uri baseAddress, string route) =>
        new($"{baseAddress.AbsoluteUri.TrimEnd('/')}/{route}");

    private sealed record EmbeddingRequest
    {
        [JsonPropertyName("model")]
        public required string Model { get; init; }

        [JsonPropertyName("input")]
        public required IReadOnlyList<string> Input { get; init; }
    }

    private sealed record EmbeddingResponse(
        [property: JsonPropertyName("data")]
        IReadOnlyList<EmbeddingResult>? Data);

    private sealed record EmbeddingResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("embedding")]
        IReadOnlyList<double> Embedding);

    private sealed record RerankRequest
    {
        [JsonPropertyName("query")]
        public required string Query { get; init; }

        [JsonPropertyName("documents")]
        public required IReadOnlyList<string> Documents { get; init; }
    }

    private sealed record RerankResponse(
        [property: JsonPropertyName("results")]
        IReadOnlyList<RerankResult>? Results);

    private sealed record RerankResult(
        [property: JsonPropertyName("index")]
        int Index,
        [property: JsonPropertyName("relevance_score")]
        double RelevanceScore);
}
