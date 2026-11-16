namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The input format and pooling an embedding model was trained with, chosen from the model segment of its installed name.
/// </summary>
/// <remarks>
///     One table for the launcher (pooling) and the knowledge base (prefixes, identity tag), so a shared
///     <c>(model, Embedding)</c> process and the vectors stored from it never disagree. A change to a profile's prefix or
///     pooling bumps its <see cref="IdentityTag" /> version, which stales only that profile's stored vectors.
/// </remarks>
public sealed record EmbeddingModelProfile
{
    // First hit wins; a token is matched case-insensitively against the model segment only, never the owner.
    // simplified: Qwen3-Embedding falls to Plain because last-token pooling needs a GGUF with add_eos_token=true; verify that, then add its row with a new tag.
    private static readonly IReadOnlyList<KeyValuePair<string, EmbeddingModelProfile>> Profiles =
    [
        new("nomic-embed-text",
            new EmbeddingModelProfile
            {
                Id = "nomic",
                DocumentPrefix = "search_document: ",
                QueryPrefix = "search_query: ",
                Pooling = "mean",
                IdentityTag = null
            }),
        new("embeddinggemma",
            new EmbeddingModelProfile
            {
                Id = "embeddinggemma",
                DocumentPrefix = "title: none | text: ",
                QueryPrefix = "task: search result | query: ",
                Pooling = "mean",
                IdentityTag = "prompt-embeddinggemma+pool-mean:v1"
            }),
        new("bge-m3",
            new EmbeddingModelProfile
            {
                Id = "bge-m3",
                DocumentPrefix = "",
                QueryPrefix = "",
                Pooling = "cls",
                IdentityTag = "prompt-none+pool-cls:v1"
            })
    ];

    /// <summary>The profile for a model no token matches: raw text, mean pooling.</summary>
    public static EmbeddingModelProfile Plain { get; } = new()
    {
        Id = "plain",
        DocumentPrefix = "",
        QueryPrefix = "",
        Pooling = "mean",
        IdentityTag = "prompt-none+pool-mean:v1"
    };

    /// <summary>The stable profile name, such as <c>nomic</c> or <c>plain</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The text prepended to a document chunk before it is embedded.</summary>
    public required string DocumentPrefix { get; init; }

    /// <summary>The text prepended to a search query before it is embedded.</summary>
    public required string QueryPrefix { get; init; }

    /// <summary>The <c>--pooling</c> value the embedding process launches with.</summary>
    public required string Pooling { get; init; }

    /// <summary>The vector-identity tag, or <see langword="null" /> for nomic, whose identity predates profiles and stays unchanged.</summary>
    public required string? IdentityTag { get; init; }

    /// <summary>Whether no known token matched and this is the <see cref="Plain" /> fallback.</summary>
    public bool IsFallback => string.Equals(Id, Plain.Id, StringComparison.Ordinal);

    /// <summary>The profile for an installed model name, or <see cref="Plain" /> when its model segment carries no known token.</summary>
    public static EmbeddingModelProfile For(string modelName)
    {
        var segment = ModelSegment(modelName);
        foreach (var (token, profile) in Profiles)
        {
            if (segment.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return profile;
            }
        }

        return Plain;
    }

    /// <summary>The model part of an installed name: after the last <c>/</c>, cut at the first <c>:</c> (<c>org/repo:Q8_0</c> gives <c>repo</c>).</summary>
    public static string ModelSegment(string modelName)
    {
        ArgumentNullException.ThrowIfNull(modelName);
        var segment = modelName[(modelName.LastIndexOf('/') + 1)..];
        var colon = segment.IndexOf(':', StringComparison.Ordinal);
        return colon < 0 ? segment : segment[..colon];
    }
}
