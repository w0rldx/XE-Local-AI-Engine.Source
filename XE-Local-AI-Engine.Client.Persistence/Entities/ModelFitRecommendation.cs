namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A single normalized recommendation row projected from a recommendation snapshot's raw output. All columns are
///     plaintext — model metadata and fit scores are not secrets; the sensitive raw output stays in the parent
///     snapshot's encrypted columns. Cascades when its parent snapshot is deleted.
/// </summary>
internal sealed record class ModelFitRecommendation
{
    public Guid Id { get; set; }

    /// <summary>Parent snapshot; real FK with cascade delete, indexed. Plaintext (structural).</summary>
    public Guid SnapshotId { get; set; }

    /// <summary>Rank within the recommendation list (1-based). Plaintext (structural).</summary>
    public int Rank { get; set; }

    /// <summary>Canonical model name. Plaintext.</summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Provider-specific model name (e.g. ollama name), or null. Plaintext.</summary>
    public string? ProviderModelName { get; set; }

    /// <summary>Fit score (0–100). Plaintext.</summary>
    public double Score { get; set; }

    /// <summary>Fit level label (e.g. Perfect/Good/Marginal/Too Tight), or null. Plaintext.</summary>
    public string? FitLevel { get; set; }

    /// <summary>Run mode label (e.g. CPU/GPU/CPU+GPU), or null. Plaintext.</summary>
    public string? RunMode { get; set; }

    /// <summary>Recommended quantization (e.g. Q5_K_M), or null. Plaintext.</summary>
    public string? Quantization { get; set; }

    /// <summary>Estimated tokens per second, or null. Plaintext.</summary>
    public double? EstimatedTokensPerSecond { get; set; }

    /// <summary>Estimated required system RAM in MB, or null. Plaintext.</summary>
    public double? RequiredRamMb { get; set; }

    /// <summary>Estimated required VRAM in MB, or null (no separate per-model VRAM field upstream). Plaintext.</summary>
    public double? RequiredVramMb { get; set; }

    /// <summary>Effective context length in tokens, or null. Plaintext.</summary>
    public int? ContextTokens { get; set; }

    /// <summary>Whether the model is already installed for the provider. Plaintext (structural).</summary>
    public bool IsInstalled { get; set; }

    /// <summary>Provider pull name to install the model, or null. Plaintext.</summary>
    public string? PullModelName { get; set; }

    /// <summary>Optional sanitized per-row diagnostics JSON. Plaintext (sanitized, not secret).</summary>
    public string? DiagnosticsJson { get; set; }
}
