namespace XE_Local_AI_Engine.Client.Common.Caching;

/// <summary>The byte budget shared by the per-item embedding caches (playbook ranking, tool relevance, memory dedup).</summary>
internal static class EmbeddingVectorCacheBudget
{
    // Byte ceiling beside each cache's configured entry bound: 4 MiB holds well over the default 512 entries at 768
    // dimensions and caps a 4096-dimension model, where the entry bound alone would retain 8 MB.
    public const long MaxBytes = 4L * 1024 * 1024;

    // Flat allowance per entry for the key struct plus dictionary node — the budget bounds RAM, it does not measure it.
    public const long EntryOverheadBytes = 64;
}
