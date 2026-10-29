namespace XE_Local_AI_Engine.Client.Hosting;

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Options;

/// <summary>
///     Recognizes a setting that failed its startup validation, so startup names the setting instead of crashing with
///     a generic failure.
/// </summary>
/// <remarks>
///     The host throws one <see cref="OptionsValidationException" /> when a hosted service reads the options first, and
///     an <see cref="AggregateException" /> of them when <c>ValidateOnStart</c> finds more than one. The desktop shell
///     reads the stderr line <see cref="Describe" /> builds: it starts with <see cref="StandardErrorPrefix" /> and goes
///     with exit code 13. Keep both stable.
/// </remarks>
internal static class SettingValidationFailure
{
    /// <summary>The stderr prefix the desktop shell matches; the validation failures follow it.</summary>
    internal const string StandardErrorPrefix = "Setting out of range: ";

    /// <summary>Finds the options validation failures <paramref name="exception" /> consists of, and nothing else.</summary>
    /// <param name="exception">The startup failure.</param>
    /// <param name="failures">The validation exceptions, when the failure is made only of them.</param>
    internal static bool TryFind(Exception? exception, [NotNullWhen(true)] out IReadOnlyList<OptionsValidationException>? failures)
    {
        failures = exception switch
        {
            OptionsValidationException single => [single],
            AggregateException aggregate when aggregate.InnerExceptions.Count > 0
                                              && aggregate.InnerExceptions.All(static inner => inner is OptionsValidationException) =>
                [.. aggregate.InnerExceptions.Cast<OptionsValidationException>()],
            _ => null
        };
        return failures is not null;
    }

    /// <summary>The single stderr line: <see cref="StandardErrorPrefix" />, then every failure message, separated by "; ".</summary>
    internal static string Describe(IReadOnlyList<OptionsValidationException> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        var messages = failures.SelectMany(static failure => failure.Failures)
                               .Select(static message => message.ReplaceLineEndings(" "));
        return StandardErrorPrefix + string.Join("; ", messages);
    }

    /// <summary>The failing options types' names, for the log.</summary>
    internal static string OptionsTypeNames(IReadOnlyList<OptionsValidationException> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);

        return string.Join(", ", failures.Select(static failure => failure.OptionsType.Name).Distinct(StringComparer.Ordinal));
    }
}
