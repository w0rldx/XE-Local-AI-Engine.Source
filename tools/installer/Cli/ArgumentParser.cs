namespace XE_Local_AI_Engine.Installer.Cli;

/// <summary>
///     Hand-rolled argument parsing for the four-verb / four-flag surface (plan §7.0 — no
///     <c>System.CommandLine</c> dependency for RC1). Grammar:
///     <code>xe-installer &lt;install|reset|remove|status&gt; [--bundle &lt;dir&gt;] [--yes] [--dry-run] [--keep-models]</code>
///     The first token is the verb; the remainder are flags. <c>--bundle</c> takes a value
///     (either <c>--bundle X</c> or <c>--bundle=X</c>); the rest are booleans.
/// </summary>
public static class ArgumentParser
{
    public const string UsageText =
        "Usage: xe-installer <install|reset|remove|status> [options]\n" +
        "\n" +
        "Verbs:\n" +
        "  install   Install XE Local AI Engine from an RC bundle (requires --bundle).\n" +
        "  reset     Full teardown then fresh install from the bundle (requires --bundle).\n" +
        "  remove    Full purge of an existing install (irreversible).\n" +
        "  status    Print the current install state (read-only).\n" +
        "\n" +
        "Options:\n" +
        "  --bundle <dir>   Path to the unzipped RC bundle. Required for install/reset.\n" +
        "  --yes            Skip the typed confirmation on remove/reset.\n" +
        "  --dry-run        Print the teardown inventory without deleting (remove/reset).\n" +
        "  --keep-models    Preserve pulled models during teardown.";

    public static ArgumentParseResult Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0)
        {
            return ArgumentParseResult.Failure("No verb specified.", UsageText);
        }

        if (!TryParseVerb(args[0], out var verb))
        {
            return ArgumentParseResult.Failure($"Unknown verb '{args[0]}'.", UsageText);
        }

        string? bundlePath = null;
        var assumeYes = false;
        var dryRun = false;
        var keepModels = false;

        var index = 1;
        while (index < args.Count)
        {
            var token = args[index];
            var (name, inlineValue) = SplitInlineValue(token);

            switch (name)
            {
                case "--bundle":
                    var bundleValue = inlineValue;
                    if (bundleValue is null)
                    {
                        if (index + 1 >= args.Count || IsFlag(args[index + 1]))
                        {
                            return ArgumentParseResult.Failure("Option '--bundle' requires a value.", UsageText);
                        }

                        bundleValue = args[++index];
                    }

                    if (string.IsNullOrWhiteSpace(bundleValue))
                    {
                        return ArgumentParseResult.Failure("Option '--bundle' requires a non-empty value.", UsageText);
                    }

                    bundlePath = bundleValue;
                    break;

                case "--yes":
                    if (RejectInlineValue(inlineValue, name, out var yesError))
                    {
                        return yesError;
                    }

                    assumeYes = true;
                    break;

                case "--dry-run":
                    if (RejectInlineValue(inlineValue, name, out var dryRunError))
                    {
                        return dryRunError;
                    }

                    dryRun = true;
                    break;

                case "--keep-models":
                    if (RejectInlineValue(inlineValue, name, out var keepModelsError))
                    {
                        return keepModelsError;
                    }

                    keepModels = true;
                    break;

                default:
                    return ArgumentParseResult.Failure($"Unknown option '{token}'.", UsageText);
            }

            index++;
        }

        if ((verb == InstallerVerb.Install || verb == InstallerVerb.Reset) && string.IsNullOrWhiteSpace(bundlePath))
        {
            return ArgumentParseResult.Failure($"Option '--bundle' is required for '{VerbName(verb)}'.", UsageText);
        }

        var arguments = new InstallerArguments
        {
            Verb = verb,
            BundlePath = bundlePath,
            AssumeYes = assumeYes,
            DryRun = dryRun,
            KeepModels = keepModels
        };

        return ArgumentParseResult.Success(arguments, UsageText);
    }

    private static string VerbName(InstallerVerb verb) => verb switch
    {
        InstallerVerb.Install => "install",
        InstallerVerb.Reset => "reset",
        InstallerVerb.Remove => "remove",
        InstallerVerb.Status => "status",
        _ => verb.ToString()
    };

    private static bool TryParseVerb(string token, out InstallerVerb verb)
    {
        switch (token)
        {
            case "install":
                verb = InstallerVerb.Install;
                return true;
            case "reset":
                verb = InstallerVerb.Reset;
                return true;
            case "remove":
                verb = InstallerVerb.Remove;
                return true;
            case "status":
                verb = InstallerVerb.Status;
                return true;
            default:
                verb = default;
                return false;
        }
    }

    private static (string Name, string? InlineValue) SplitInlineValue(string token)
    {
        var separator = token.IndexOf('=', StringComparison.Ordinal);
        return separator < 0
            ? (token, null)
            : (token[..separator], token[(separator + 1)..]);
    }

    private static bool RejectInlineValue(string? inlineValue, string name, out ArgumentParseResult error)
    {
        if (inlineValue is not null)
        {
            error = ArgumentParseResult.Failure($"Option '{name}' does not take a value.", UsageText);
            return true;
        }

        error = null!;
        return false;
    }

    private static bool IsFlag(string token) => token.StartsWith("--", StringComparison.Ordinal);
}
