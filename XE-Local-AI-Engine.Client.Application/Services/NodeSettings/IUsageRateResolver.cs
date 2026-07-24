namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Resolves the USD-per-1M-tokens <see cref="ModelRate" /> to price a run-envelope usage bucket, given the bucket's
///     fine-grained provider and model name. Precedence: local runtimes (llama.cpp / Ollama) are always free
///     (zero); otherwise an operator override for the model name wins, then a built-in default-table entry, then zero
///     (unknown / unpriced). Model names are matched case-insensitively to the run-envelope <c>ModelName</c>.
/// </summary>
public interface IUsageRateResolver
{
    /// <summary>
    ///     Returns the rate for the given (provider, model). Never returns <see langword="null" />: an unpriced or
    ///     free-provider combination resolves to a zero rate (both rates 0), which folds to a zero cost.
    /// </summary>
    ModelRate Resolve(string provider, string modelName);
}
