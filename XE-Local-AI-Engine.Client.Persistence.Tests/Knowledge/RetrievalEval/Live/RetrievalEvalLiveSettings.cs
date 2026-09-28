namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval.Live;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>One reranker under test: a short report id (<c>bge</c>), its GGUF path and the languages it supports.</summary>
internal sealed record LiveRerankerModel
{
    public required string Id { get; init; }

    public required string ModelPath { get; init; }

    /// <summary>Comma-separated language codes (<c>en</c>, matched ignoring case), or <see langword="null" /> for every language.</summary>
    public string? Languages { get; init; }

    public bool Supports(string language) =>
        Languages is null || Languages.Split(',').Contains(language, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
///     The opt-in gate and knobs of the live retrieval eval, read ONLY from variables unique to it, so a shell that
///     exports <c>XE_LLAMACPP_SERVER_PATH</c> for other work can never trip the eval by accident.
/// </summary>
internal sealed partial record RetrievalEvalLiveSettings
{
    public const string ServerVariable = "XE_RETRIEVAL_EVAL_SERVER";
    public const string EmbedModelVariable = "XE_RETRIEVAL_EVAL_EMBED_MODEL";
    public const string RerankersVariable = "XE_RETRIEVAL_EVAL_RERANKERS";

    private static readonly string[] ReservedIdPrefixes = ["F0", "PROD", "NEG"];
    public const string ReportVariable = "XE_RETRIEVAL_EVAL_REPORT";
    public const string GpuLayersVariable = "XE_RETRIEVAL_EVAL_NGL";
    public const string ChatModelVariable = "XE_RETRIEVAL_EVAL_CHAT_MODEL";
    public const string KVariable = "XE_RETRIEVAL_EVAL_K";

    /// <summary>The cut-off every retrieval-eval test in this folder uses.</summary>
    public const int DefaultK = 5;

    public const int DefaultGpuLayers = 99;

    public required string ServerPath { get; init; }

    public required string EmbedModelPath { get; init; }

    public required IReadOnlyList<LiveRerankerModel> Rerankers { get; init; }

    public string? ReportDirectory { get; init; }

    public required int GpuLayers { get; init; }

    public string? ChatModelPath { get; init; }

    public required int K { get; init; }

    /// <summary>
    ///     The settings, or <see langword="null" /> when the two gate variables are not both set (the caller skips).
    ///     Malformed optional values throw: a typo must fail the run, never silently fall back to a default.
    /// </summary>
    public static RetrievalEvalLiveSettings? FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        var server = read(ServerVariable);
        var embed = read(EmbedModelVariable);
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(embed))
        {
            return null;
        }

        return new RetrievalEvalLiveSettings
        {
            ServerPath = server,
            EmbedModelPath = embed,
            Rerankers = ParseRerankers(read(RerankersVariable)),
            ReportDirectory = NullIfBlank(read(ReportVariable)),
            GpuLayers = ParseInt(GpuLayersVariable, read(GpuLayersVariable), DefaultGpuLayers, minimum: 0),
            ChatModelPath = NullIfBlank(read(ChatModelVariable)),
            K = ParseInt(KVariable, read(KVariable), DefaultK, minimum: 1)
        };
    }

    /// <summary>
    ///     Parses <c>id=path[@lang,lang];...</c>; no suffix means every language. Blank is an empty list; a duplicate id,
    ///     a missing half or an empty suffix throws.
    /// </summary>
    public static IReadOnlyList<LiveRerankerModel> ParseRerankers(string? raw)
    {
        var result = new List<LiveRerankerModel>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = entry.IndexOf('=', StringComparison.Ordinal);
            var id = separator > 0 ? entry[..separator].Trim() : string.Empty;
            var path = separator > 0 ? entry[(separator + 1)..].Trim() : string.Empty;
            string? languages = null;
            var at = path.LastIndexOf('@');
            if (at >= 0 && LanguageSuffix().IsMatch(path[(at + 1)..]))
            {
                languages = path[(at + 1)..];
                path = path[..at].TrimEnd();
            }
            else if (path.EndsWith('@'))
            {
                throw new FormatException($"{RerankersVariable} entry '{entry}' has an empty language suffix.");
            }

            if (id.Length == 0 || path.Length == 0)
            {
                throw new FormatException($"{RerankersVariable} entry '{entry}' is not 'id=path'.");
            }

            // Row ids are "<id>-full" etc. beside F0*, PROD-* and NEG-*; a clashing id would hide or void rows.
            if (ReservedIdPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FormatException($"{RerankersVariable} reranker id '{id}' is reserved (F0, PROD, NEG prefixes name eval rows).");
            }

            if (result.Exists(existing => string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FormatException($"{RerankersVariable} names reranker id '{id}' twice.");
            }

            result.Add(new LiveRerankerModel { Id = id, ModelPath = path, Languages = languages });
        }

        return result;
    }

    private static int ParseInt(string variable, string? raw, int fallback, int minimum)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= minimum
            ? value
            : throw new FormatException($"{variable}='{raw}' is not an integer >= {minimum.ToString(CultureInfo.InvariantCulture)}.");
    }

    // Two- or three-letter codes, comma-separated: a path that merely contains '@' is never mistaken for a suffix.
    [GeneratedRegex("^[A-Za-z]{2,3}(,[A-Za-z]{2,3})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageSuffix();

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}
