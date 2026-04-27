namespace XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

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

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new AssertionException(message ?? $"Expected: {FormatValue(expected)}{Environment.NewLine}Actual: {FormatValue(actual)}");
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

    public static void Empty<T>(IEnumerable<T> values, string? message = null)
    {
        if (values.Any())
        {
            throw new AssertionException(message ?? "Expected collection to be empty.");
        }
    }

    public static TException Throws<TException>(Action action, string? message = null) where TException : Exception
    {
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
