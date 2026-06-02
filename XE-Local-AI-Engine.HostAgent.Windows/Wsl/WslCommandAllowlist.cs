namespace XE_Local_AI_Engine.HostAgent.Windows.Wsl;

using System.Text.RegularExpressions;

public static partial class WslCommandAllowlist
{
    private const string DistroUser = "xe-engine";
    private const string HostAgentUnit = "xe-host-agent.service";
    private const string HostAgentCtlPath = "/opt/xe-host-agent/bin/xe-host-agent-ctl";
    private static readonly string[] UserSystemctlVerbs = ["start", "restart", "stop", "is-active"];
    private static readonly string[] HostAgentCtlVerbs = ["status", "reload"];

    private static readonly IReadOnlyList<WslCommandPattern> AllowedPatterns =
    [
        new("list-running-quiet", args => SequenceEqual(args, "--list", "--running", "--quiet")),
        new("list-verbose", args => SequenceEqual(args, "--list", "--verbose")),
        new("status", args => SequenceEqual(args, "--status")),
        new("install-no-distribution", args => SequenceEqual(args, "--install", "--no-distribution")),
        new("shutdown", args => SequenceEqual(args, "--shutdown")),
        new("import-wsl2", IsImport),
        new("unregister", args => HasDistroArgument(args, "--unregister")),
        new("terminate", args => HasDistroArgument(args, "--terminate")),
        new("bootstrap-script", args => IsDistributionCommand(args, "root", "bash", "-s")),
        new("runtime-install-script", args => IsDistributionCommand(args, DistroUser, "bash", "-s")),
        new("wake", args => IsDistributionCommand(args, DistroUser, "/bin/true")),
        new("user-systemctl", IsUserSystemctl),
        new("host-agent-ctl", IsHostAgentCtl),
        new("system-running", args => IsDistributionCommand(args, DistroUser, "systemctl", "is-system-running")),
        new("init-version", args => IsDistributionCommand(args, DistroUser, "/sbin/init", "--version"))
    ];

    public static WslCommand Create(IReadOnlyList<string> arguments, string? standardInput = null, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (!AllowedPatterns.Any(pattern => pattern.Matches(arguments)))
        {
            throw new WslArgumentNotAllowedException(string.Join(' ', arguments));
        }

        return new WslCommand(arguments.ToArray(), standardInput, timeout);
    }

    public static WslCommand ListRunningQuiet()
    {
        return Create(["--list", "--running", "--quiet"]);
    }

    public static WslCommand ListVerbose()
    {
        return Create(["--list", "--verbose"]);
    }

    public static WslCommand Status()
    {
        return Create(["--status"]);
    }

    public static WslCommand InstallNoDistribution()
    {
        return Create(["--install", "--no-distribution"]);
    }

    public static WslCommand Shutdown()
    {
        return Create(["--shutdown"]);
    }

    public static WslCommand Import(string distroName, string installPath, string tarballPath)
    {
        return Create(["--import", distroName, installPath, tarballPath, "--version", "2"]);
    }

    public static WslCommand Unregister(string distroName)
    {
        return Create(["--unregister", distroName]);
    }

    public static WslCommand Terminate(string distroName)
    {
        return Create(["--terminate", distroName]);
    }

    public static WslCommand BootstrapScript(string distroName, string script, TimeSpan? timeout = null)
    {
        return Create(["--distribution", distroName, "--user", "root", "--", "bash", "-s"], script, timeout);
    }

    public static WslCommand RuntimeInstallScript(string distroName, string script, TimeSpan? timeout = null)
    {
        return Create(["--distribution", distroName, "--user", DistroUser, "--", "bash", "-s"], script, timeout);
    }

    public static WslCommand Wake(string distroName)
    {
        return Create(["--distribution", distroName, "--user", DistroUser, "--", "/bin/true"]);
    }

    public static WslCommand UserSystemctl(string distroName, string verb)
    {
        return Create(["--distribution", distroName, "--user", DistroUser, "--", "systemctl", "--user", verb, HostAgentUnit]);
    }

    public static WslCommand HostAgentCtl(string distroName, string verb, string? argument = null)
    {
        return argument is null
            ? Create(["--distribution", distroName, "--user", DistroUser, "--", HostAgentCtlPath, verb])
            : Create(["--distribution", distroName, "--user", DistroUser, "--", HostAgentCtlPath, verb, argument]);
    }

    public static WslCommand SystemIsRunning(string distroName)
    {
        return Create(["--distribution", distroName, "--user", DistroUser, "--", "systemctl", "is-system-running"]);
    }

    public static WslCommand InitVersion(string distroName)
    {
        return Create(["--distribution", distroName, "--user", DistroUser, "--", "/sbin/init", "--version"]);
    }

    private static bool IsImport(IReadOnlyList<string> args)
    {
        return args.Count == 6
               && args[0] == "--import"
               && IsValidDistroName(args[1])
               && Path.IsPathRooted(args[2])
               && Path.IsPathRooted(args[3])
               && args[4] == "--version"
               && args[5] == "2";
    }

    private static bool HasDistroArgument(IReadOnlyList<string> args, string verb)
    {
        return args.Count == 2 && args[0] == verb && IsValidDistroName(args[1]);
    }

    private static bool IsUserSystemctl(IReadOnlyList<string> args)
    {
        return args.Count == 10
               && IsDistributionPrefix(args, DistroUser)
               && args[6] == "systemctl"
               && args[7] == "--user"
               && UserSystemctlVerbs.Contains(args[8], StringComparer.Ordinal)
               && args[9] == HostAgentUnit;
    }

    private static bool IsHostAgentCtl(IReadOnlyList<string> args)
    {
        if (args.Count < 8 || args.Count > 9 || !IsDistributionPrefix(args, DistroUser) || args[6] != HostAgentCtlPath)
        {
            return false;
        }

        return args.Count == 8
            ? HostAgentCtlVerbs.Contains(args[7], StringComparer.Ordinal)
            : args[7] == "read-phase-exit" && IsKnownPhase(args[8]);
    }

    private static bool IsDistributionCommand(IReadOnlyList<string> args, string user, params string[] command)
    {
        return args.Count == 6 + command.Length
               && IsDistributionPrefix(args, user)
               && args.Skip(6).SequenceEqual(command, StringComparer.Ordinal);
    }

    private static bool IsDistributionPrefix(IReadOnlyList<string> args, string user)
    {
        return args.Count >= 6
               && args[0] == "--distribution"
               && IsValidDistroName(args[1])
               && args[2] == "--user"
               && args[3] == user
               && args[4] == "--";
    }

    private static bool SequenceEqual(IReadOnlyList<string> args, params string[] expected)
    {
        return args.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool IsKnownPhase(string phase)
    {
        return string.Equals(phase, "bootstrap", StringComparison.Ordinal)
               || string.Equals(phase, "runtime-install", StringComparison.Ordinal);
    }

    private static bool IsValidDistroName(string value)
    {
        return DistroNameRegex().IsMatch(value);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DistroNameRegex();

    private sealed record WslCommandPattern(string Name, Func<IReadOnlyList<string>, bool> Matches);
}
