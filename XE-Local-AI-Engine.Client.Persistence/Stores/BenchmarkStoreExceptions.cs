namespace XE_Local_AI_Engine.Client.Persistence.Stores;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

public abstract class BenchmarkStoreException(string message) : InvalidOperationException(message);

public sealed class BenchmarkNotFoundException(string message) : BenchmarkStoreException(message);

public sealed class BenchmarkConflictException(string code) : BenchmarkStoreException(code)
{
    public string Code { get; } = code;
}

public sealed class BenchmarkValidationException(string message) : BenchmarkStoreException(message);

/// <summary>
///     The project's judge policy moved while a judging was being prepared for the previous revision. Retryable: the
///     caller re-reads the current revision, re-resolves the judge runtime and calls again.
/// </summary>
public sealed class BenchmarkJudgePolicyChangedException(string message) : BenchmarkStoreException(message);
