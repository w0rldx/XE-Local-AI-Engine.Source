namespace XE_Local_AI_Engine.Tests.Testing;

internal static class AssertEx
{
    public static void True(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new AssertionException(message ?? "Expected condition to be true.");
        }
    }

    public static void False(bool condition, string? message = null)
    {
        if (condition)
        {
            throw new AssertionException(message ?? "Expected condition to be false.");
        }
    }

    public static void Null(object? value, string? message = null)
    {
        if (value is not null)
        {
            throw new AssertionException(message ?? $"Expected null but was {FormatValue(value)}.");
        }
    }

    public static T NotNull<T>(T? value, string? message = null) where T : class
    {
        if (value is null)
        {
            throw new AssertionException(message ?? "Expected non-null value.");
        }

        return value;
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException(message ?? $"Expected: {FormatValue(expected)}{Environment.NewLine}Actual: {FormatValue(actual)}");
        }
    }

    public static void Equal(string expected, string? actual, string? message = null)
    {
        if (actual is null)
        {
            throw new AssertionException(message ?? $"Expected: {FormatValue(expected)}{Environment.NewLine}Actual: <null>");
        }

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new AssertionException(message ?? $"Expected: {FormatValue(expected)}{Environment.NewLine}Actual: {FormatValue(actual)}");
        }
    }

    public static void NotEqual<T>(T notExpected, T actual, string? message = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
        {
            throw new AssertionException(message ?? $"Did not expect: {FormatValue(actual)}");
        }
    }

    public static void NotNullOrEmpty(string? value, string? message = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new AssertionException(message ?? "Expected non-empty string.");
        }
    }

    public static void NotEmpty(string? value, string? message = null)
    {
        NotNullOrEmpty(value, message);
    }

    public static void NotEmpty<T>(IEnumerable<T>? values, string? message = null)
    {
        if (values is null)
        {
            throw new AssertionException(message ?? "Expected non-empty collection but was null.");
        }

        if (!values.Any())
        {
            throw new AssertionException(message ?? "Expected non-empty collection.");
        }
    }

    public static void Empty<T>(IEnumerable<T>? values, string? message = null)
    {
        if (values is null)
        {
            throw new AssertionException(message ?? "Expected empty collection but was null.");
        }

        if (values.Any())
        {
            throw new AssertionException(message ?? "Expected empty collection.");
        }
    }

    public static void Contains<T>(IEnumerable<T>? values, T expected, string? message = null)
    {
        if (values is null)
        {
            throw new AssertionException(message ?? "Expected collection to contain value but was null.");
        }

        if (!values.Contains(expected))
        {
            throw new AssertionException(message ?? $"Expected collection to contain {FormatValue(expected)}.");
        }
    }

    public static void Contains<T>(IEnumerable<T>? values, Func<T, bool> predicate, string? message = null)
    {
        if (values is null)
        {
            throw new AssertionException(message ?? "Expected collection to contain matching element but was null.");
        }

        if (!values.Any(predicate))
        {
            throw new AssertionException(message ?? "Expected collection to contain matching element.");
        }
    }

    public static void Contains(string? actual, string expectedSubstring, StringComparison comparison = StringComparison.Ordinal, string? message = null)
    {
        if (actual is null)
        {
            throw new AssertionException(message ?? "Expected string to contain substring but was null.");
        }

        if (!actual.Contains(expectedSubstring, comparison))
        {
            throw new AssertionException(message ?? $"Expected {FormatValue(actual)} to contain {FormatValue(expectedSubstring)}.");
        }
    }

    public static void ContainsSingle<T>(IEnumerable<T>? values, Func<T, bool> predicate, string? message = null)
    {
        if (values is null)
        {
            throw new AssertionException(message ?? "Expected collection to contain a single matching element but was null.");
        }

        var count = values.Count(predicate);
        if (count != 1)
        {
            throw new AssertionException(message ?? $"Expected single matching element but found {count}.");
        }
    }

    public static TException Throws<TException>(Action action, string? message = null) where TException : Exception
    {
        ArgumentNullException.ThrowIfNull(action);

        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name} but caught {exception.GetType().Name}: {exception.Message}");
        }

        throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name} but no exception was thrown.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string? message = null) where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name} but caught {exception.GetType().Name}: {exception.Message}");
        }

        throw new AssertionException(message ?? $"Expected exception of type {typeof(TException).Name} but no exception was thrown.");
    }

    /// <summary>
    ///     Lets the scheduler drain: every continuation the runtime can already run does run before the caller looks
    ///     again. This is the deterministic stand-in for "sleep, then assert that nothing happened". A sleep can only
    ///     fail when the code under test gets SLOWER, so on a contended runner it hides the very regression it was
    ///     written to catch, while costing its full duration on every green run.
    /// </summary>
    /// <param name="rounds">
    ///     How many yield + thread-pool round trips to make. Each round hands the caller's continuation back to the
    ///     pool and then queues behind everything that yield made runnable, so a continuation chain of this depth has
    ///     run by the time the caller resumes.
    /// </param>
    public static async Task SettleAsync(int rounds = 16)
    {
        for (var round = 0; round < rounds; round++)
        {
            await Task.Yield();
            await Task.Run(static () => { }).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Asserts <paramref name="task" /> is still parked once <see cref="SettleAsync" /> has drained the scheduler:
    ///     the negative half of a gate assertion, without a wall-clock guess.
    /// </summary>
    public static async Task StaysIncompleteAsync(Task task, string message)
    {
        ArgumentNullException.ThrowIfNull(task);

        await SettleAsync().ConfigureAwait(false);

        if (task.IsCompleted)
        {
            throw new AssertionException(message);
        }
    }

    /// <summary>
    ///     Awaits a task that must finish inside <paramref name="timeout" />, failing the run with
    ///     <paramref name="message" /> instead of hanging it. The timeout is a failure deadline, not a sleep: a green
    ///     run returns the moment the task completes, so pass a budget generous enough to survive a contended runner
    ///     (<c>TestBudgets.Contended</c> in XE-Local-AI-Engine.Tests).
    /// </summary>
    public static async Task CompletesAsync(Task task, TimeSpan timeout, string message)
    {
        ArgumentNullException.ThrowIfNull(task);

        try
        {
            await task.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new AssertionException(message);
        }
    }

    public static async Task EventuallyAsync(Func<bool> condition, TimeSpan timeout, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var deadline = DateTimeOffset.UtcNow + timeout;
        var pollInterval = TimeSpan.FromMilliseconds(25);

        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(pollInterval).ConfigureAwait(false);
        }

        if (!condition())
        {
            throw new AssertionException(message ?? $"Condition was not satisfied within {timeout}.");
        }
    }

    private static string FormatValue(object? value)
    {
        return value switch
        {
            null => "<null>",
            string text => $"\"{text}\"",
            _ => value.ToString() ?? "<null>"
        };
    }
}
