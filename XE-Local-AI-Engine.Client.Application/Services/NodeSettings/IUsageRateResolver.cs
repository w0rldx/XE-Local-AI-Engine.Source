namespace XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Resolves the USD-per-1M-tokens <see cref="ModelRate" /> that prices a run-envelope usage bucket, from the
///     bucket's fine-grained provider and model name.
/// </summary>
/// <remarks>
///     Precedence: local runtimes are always free; otherwise an operator override for the model name wins, then a
///     built-in default-table entry, then zero for an unknown or unpriced model. Model names are matched
///     case-insensitively against the run-envelope <c>ModelName</c>.
/// </remarks>
public interface IUsageRateResolver
{
    /// <summary>
    ///     Returns the rate for the given (provider, model). Never returns <see langword="null" />: an unpriced or
    ///     free-provider combination resolves to a zero rate (both rates 0), which folds to a zero cost.
    /// </summary>
    ModelRate Resolve(string provider, string modelName);
}
