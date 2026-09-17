namespace XE_Local_AI_Engine.Client.Persistence.Stores;

public abstract class BenchmarkStoreException : InvalidOperationException
{
    protected BenchmarkStoreException(string message)
        : base(message)
    {
    }

    protected BenchmarkStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BenchmarkNotFoundException : BenchmarkStoreException
{
    public BenchmarkNotFoundException(string message)
        : base(message)
    {
    }

    public BenchmarkNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BenchmarkConflictException : BenchmarkStoreException
{
    public BenchmarkConflictException(string code)
        : base(code) =>
        Code = code;

    public BenchmarkConflictException(string code, Exception innerException)
        : base(code, innerException) =>
        Code = code;

    public string Code { get; }
}

public sealed class BenchmarkValidationException : BenchmarkStoreException
{
    public BenchmarkValidationException(string message)
        : base(message)
    {
    }

    public BenchmarkValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     The project's judge policy moved while a judging was being prepared for the previous revision. Retryable: the
///     caller re-reads the current revision, re-resolves the judge runtime and calls again.
/// </summary>
public sealed class BenchmarkJudgePolicyChangedException(string message) : BenchmarkStoreException(message);
