namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Text.Json;

/// <summary>Fail-closed parse of one pairwise verdict.</summary>
/// <remarks>
///     Anything outside the schema — a missing member, an unknown verdict token, a rationale past the bound — fails
///     the comparison rather than being coerced into a verdict, because a coerced verdict is a vote nobody cast.
/// </remarks>
public static class BenchmarkPairwiseResultParser
{
    public static BenchmarkPairwiseResultV1 Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new BenchmarkExecutionException("The pairwise judge returned no output.");
        }

        try
        {
            using var document = JsonDocument.Parse(ExtractObject(content));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var version)
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1
                || !root.TryGetProperty("verdict", out var verdictElement)
                || verdictElement.GetString() is not { } verdict
                || verdict is not (BenchmarkBradleyTerry.VerdictA or BenchmarkBradleyTerry.VerdictB or BenchmarkBradleyTerry.VerdictTie))
            {
                throw new JsonException();
            }

            var rationale = root.TryGetProperty("rationale", out var rationaleElement) ? rationaleElement.GetString()?.Trim() : null;
            return rationale is null || rationale.Length == 0 || rationale.Length > BenchmarkPairwiseOutputSchemaV1.MaximumRationaleLength
                ? throw new JsonException()
                : new BenchmarkPairwiseResultV1
                {
                    SchemaVersion = schemaVersion,
                    Verdict = verdict,
                    Rationale = rationale
                };
        }
        catch (JsonException)
        {
            throw new BenchmarkExecutionException("The pairwise judge output did not match the required schema.");
        }
    }

    /// <summary>
    ///     The outermost JSON object in the response. Constrained decoding makes this the whole string in practice; a
    ///     model that wrapped it in prose still has its object read rather than the whole judging thrown away.
    /// </summary>
    private static string ExtractObject(string content)
    {
        var start = content.IndexOf('{', StringComparison.Ordinal);
        var end = content.LastIndexOf('}');
        return start >= 0 && end > start ? content[start..(end + 1)] : content;
    }

    /// <summary>Turns a verdict about the PRESENTATION order into one about the canonical pair.</summary>
    /// <remarks>
    ///     With <paramref name="order" /> 1 the runs were shown swapped, so a verdict of <c>a</c> means the canonical
    ///     B won. This is the whole bookkeeping of the position swap, and it lives in exactly one place.
    /// </remarks>
    public static string ToCanonicalVerdict(string verdict, int order)
    {
        if (order == 0 || string.Equals(verdict, BenchmarkBradleyTerry.VerdictTie, StringComparison.Ordinal))
        {
            return verdict;
        }

        return string.Equals(verdict, BenchmarkBradleyTerry.VerdictA, StringComparison.Ordinal)
            ? BenchmarkBradleyTerry.VerdictB
            : BenchmarkBradleyTerry.VerdictA;
    }
}
