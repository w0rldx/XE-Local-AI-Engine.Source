namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Persisted per-model override of extra <c>llama-server</c> command-line arguments a node operator typed under the
///     developer/advanced settings. Keyed by model name (<c>NOCASE</c>). Stored as the RAW string the operator entered;
///     tokenizing and stripping the reserved process-contract flags happens on the spawn path, not here. Not encrypted —
///     llama.cpp flags are not secrets. This is a developer experimentation knob: it lets an operator try parameters the
///     bundled launch policy does not expose (sampling, RoPE, batch, …) for ONE model without affecting any other.
/// </summary>
internal sealed record class ModelLaunchArguments
{
    /// <summary>Model name (primary key, <c>NOCASE</c> collation). The stable per-model key.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>The raw, operator-entered extra argument string (for example <c>--top-k 40 --repeat-penalty 1.1</c>).</summary>
    public string RawArguments { get; set; } = string.Empty;

    /// <summary>Unix ms of the last row write.</summary>
    public long UpdatedAtUtc { get; set; }
}
