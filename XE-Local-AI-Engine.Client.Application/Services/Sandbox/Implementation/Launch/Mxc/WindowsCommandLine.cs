namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;

using System.Text;

/// <summary>
///     Builds one Windows command line from an argv, so that <c>CommandLineToArgvW</c> (and the MSVC CRT) parse it back to exactly
///     the same argv. MXC takes a command LINE, not an argv; the quoting is ours.
/// </summary>
/// <remarks>
///     argv[0] follows the program-name rule: backslashes are literal and a quote cannot be escaped, so a program path containing
///     <c>"</c> is rejected. Every later argument follows the regular rule: wrap in quotes when empty or when it contains space, tab or
///     <c>"</c>; inside quotes a run of backslashes is doubled when it precedes a quote (embedded or closing), and each quote is
///     escaped with a backslash. This is the algorithm .NET's own <c>ProcessStartInfo.ArgumentList</c> uses.
/// </remarks>
public static class WindowsCommandLine
{
    public static string Join(string executable, IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(executable);
        ArgumentNullException.ThrowIfNull(arguments);
        if (executable.Contains('"', StringComparison.Ordinal) || executable.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A Windows program path cannot contain a quote or a NUL character.", nameof(executable));
        }

        var builder = new StringBuilder();
        if (executable.Contains(' ', StringComparison.Ordinal) || executable.Contains('\t', StringComparison.Ordinal))
        {
            builder.Append('"').Append(executable).Append('"');
        }
        else
        {
            builder.Append(executable);
        }

        foreach (var argument in arguments)
        {
            if (argument is null || argument.Contains('\0', StringComparison.Ordinal))
            {
                throw new ArgumentException("A command-line argument cannot be null or contain a NUL character.", nameof(arguments));
            }

            builder.Append(' ');
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\"") < 0)
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var index = 0;
        while (index < argument.Length)
        {
            var backslashes = 0;
            while (index < argument.Length && argument[index] == '\\')
            {
                index++;
                backslashes++;
            }

            if (index == argument.Length)
            {
                // Before the closing quote: double them so none escapes it.
                builder.Append('\\', backslashes * 2);
            }
            else if (argument[index] == '"')
            {
                builder.Append('\\', (backslashes * 2) + 1).Append('"');
                index++;
            }
            else
            {
                builder.Append('\\', backslashes).Append(argument[index]);
                index++;
            }
        }

        builder.Append('"');
    }
}
