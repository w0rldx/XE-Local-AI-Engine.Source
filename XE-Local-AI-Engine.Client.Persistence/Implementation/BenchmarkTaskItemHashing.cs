namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The two plaintext hashes the whole task-suite staleness story rests on: what ONE item asks, and what the WHOLE
///     leaf set asks. Both are recomputed by the store on every write, so a caller cannot present a stale or invented
///     value, and both are plaintext so the ranking read compares them without decrypting a payload.
/// </summary>
/// <remarks>
///     A length-prefixed byte feed rather than canonical JSON: the canonical-JSON writer lives in the application
///     layer, which this one deliberately cannot see, and re-implementing it here to hash a fixed-arity tuple would be
///     a second canonicalizer to keep in step. Same shape as <c>TrainingDatasetStore</c>'s content fingerprint —
///     explicit separators, invariant formatting, no format that can be reordered.
/// </remarks>
internal static class BenchmarkTaskItemHashing
{
    private const string Prefix = "v1:";
    /// <summary>ASCII UNIT SEPARATOR, and RECORD SEPARATOR below: bytes no kind name and no JSON payload carries.</summary>
    private static readonly byte[] FieldSeparator = "\u001f"u8.ToArray();
    private static readonly byte[] RecordSeparator = "\u001e"u8.ToArray();

    /// <summary>
    ///     <c>v1:</c> + SHA-256 over exactly what an item asks: its kind, its revision and its four payloads. Every
    ///     payload participates by its bytes, so a reference answer or a verifier override changing is as visible as
    ///     the prompt changing — the run that answered the old instance is no longer answering this question.
    /// </summary>
    public static string ComputeInputHash(string kind,
        int revision,
        ReadOnlySpan<byte> promptJson,
        ReadOnlySpan<byte> referenceAnswerJson,
        ReadOnlySpan<byte> verifierConfigJson,
        ReadOnlySpan<byte> generatorConfigJson)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, Encoding.UTF8.GetBytes(kind));
        AppendField(hash, Encoding.UTF8.GetBytes(revision.ToString(CultureInfo.InvariantCulture)));
        AppendField(hash, promptJson);
        AppendField(hash, referenceAnswerJson);
        AppendField(hash, verifierConfigJson);
        AppendField(hash, generatorConfigJson);
        return Prefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <inheritdoc cref="ComputeInputHash(string, int, ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte}, ReadOnlySpan{byte})" />
    public static string ComputeInputHash(BenchmarkTaskItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ComputeInputHash(item.Kind,
            item.Revision,
            item.PromptJson,
            item.ReferenceAnswerJson ?? [],
            item.VerifierConfigJson ?? [],
            item.GeneratorConfigJson ?? []);
    }

    /// <summary>
    ///     <c>v1:</c> + SHA-256 over the project's LEAF items — the ones a freeze fans out over — ordered by their
    ///     immutable <see cref="BenchmarkTaskItem.Id" />, NOT by index.
    ///     <para>
    ///         The ordering is the whole design. Adding or deleting an item changes which questions the project asks
    ///         and must move the hash, because a cell's mean is a mean over that set; reordering changes no question
    ///         and must not, or a cosmetic drag-and-drop would unrank a completed suite.
    ///     </para>
    /// </summary>
    /// <returns><see langword="null" /> when the project has no leaf items to hash.</returns>
    public static string? ComputeSetHash(IEnumerable<BenchmarkTaskItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var leaves = items.Where(static item => Stores.BenchmarkTaskItemKinds.IsLeaf(item.Kind)).OrderBy(static item => item.Id).ToArray();
        if (leaves.Length == 0)
        {
            return null;
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var leaf in leaves)
        {
            var record = string.Create(CultureInfo.InvariantCulture, $"{leaf.Kind}\u001f{leaf.Revision}\u001f{leaf.InputHash}");
            hash.AppendData(Encoding.UTF8.GetBytes(record));
            hash.AppendData(RecordSeparator);
        }

        return Prefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    ///     Length-prefixed, so no arrangement of payload bytes can be read as a different arrangement of fields —
    ///     a separator the payload happens to contain would otherwise let two different items hash the same.
    /// </summary>
    private static void AppendField(IncrementalHash hash, ReadOnlySpan<byte> value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value.Length.ToString(CultureInfo.InvariantCulture)));
        hash.AppendData(FieldSeparator);
        hash.AppendData(value);
        hash.AppendData(RecordSeparator);
    }
}
