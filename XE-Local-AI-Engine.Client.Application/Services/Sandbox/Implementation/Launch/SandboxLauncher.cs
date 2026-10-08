namespace XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Isolation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch.Mxc;
using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The production <see cref="ISandboxLauncher" />: applies the plan computed by <see cref="SandboxLaunchPlan" /> to a
///     <see cref="ProcessStartInfo" />.
/// </summary>
/// <remarks>
///     All decision logic lives in the plan, a pure function unit-tested without starting processes; this type is the thin adapter that
///     mutates the start info and layers in the wrapper's own environment.
/// </remarks>
public sealed class SandboxLauncher : ISandboxLauncher
{
    /// <summary>Grace added to the command's own timeout to form the scope's <c>RuntimeMaxSec</c>.</summary>
    /// <remarks>
    ///     The engine's timeout must be the control that normally fires; this one exists for when the engine is no longer there to fire
    ///     it, so it sits clearly behind rather than racing it.
    /// </remarks>
    private static readonly TimeSpan ScopeLifetimeGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     The scope ceiling for a command that names no timeout of its own. An isolated command is expected to carry
    ///     one; this bounds the case where it does not, so a jail cannot outlive the engine indefinitely.
    /// </summary>
    private static readonly TimeSpan DefaultScopeLifetime = TimeSpan.FromHours(1);

    /// <summary>The scrub-allow-listed variables that name the REAL profile and have no jail equivalent; dropped under MXC.</summary>
    private static readonly string[] RealProfileVariableNames = ["HOMEDRIVE", "HOMEPATH"];

    private readonly IReadOnlyList<string> _deniedRootCandidates;
    private readonly ILogger<SandboxLauncher> _logger;
    private readonly ISandboxContainmentProbe _probe;

    // The data directory and logger are optional so tests and the provider's own fallback can build a launcher from a probe alone; DI
    // supplies both. Without a data directory the node data root is simply not among the explicit denies (the default deny still holds).
    public SandboxLauncher(ISandboxContainmentProbe probe, INodeDataDirectory? nodeDataDirectory = null, ILogger<SandboxLauncher>? logger = null)
        : this(probe, DefaultDeniedRootCandidates(nodeDataDirectory), logger)
    {
    }

    internal SandboxLauncher(ISandboxContainmentProbe probe, IReadOnlyList<string> deniedRootCandidates, ILogger<SandboxLauncher>? logger = null)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _deniedRootCandidates = deniedRootCandidates ?? throw new ArgumentNullException(nameof(deniedRootCandidates));
        _logger = logger ?? NullLogger<SandboxLauncher>.Instance;
    }

    /// <inheritdoc />
    public SandboxContainment Containment => _probe.Containment;

    /// <inheritdoc />
    public SandboxLaunchDescriptor Apply(ProcessStartInfo startInfo, SandboxLaunchPolicy policy, SandboxLaunchContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(policy);

        var containment = _probe.Containment;
        SandboxLaunchDescriptor descriptor;
        if (policy.Isolation != SandboxIsolationMode.Filesystem)
        {
            descriptor = SandboxLaunchPlan.Create(startInfo.FileName, [.. startInfo.ArgumentList], policy, containment);
        }
        else if (policy.AppContainerBoundary is not null)
        {
            descriptor = CreateAppContainerDescriptor(startInfo, policy, context);
        }
        else
        {
            descriptor = CreateIsolatedDescriptor(startInfo, policy, containment, context);
        }

        startInfo.FileName = descriptor.FileName;
        startInfo.ArgumentList.Clear();
        foreach (var argument in descriptor.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The wrapper's own environment (the user systemd bus address) is layered on AFTER the caller's scrubbed allow-list, systemd-run
        // needing it to reach the user manager. The innermost `env -u` layer removes it again before the executable is exec'd.
        foreach (var pair in descriptor.WrapperEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return descriptor;
    }

    /// <summary>Prepares and renders the isolated chain.</summary>
    /// <remarks>
    ///     Every failure path throws <see cref="SandboxIsolationUnavailableException" />, deliberately and unlike every other branch here:
    ///     a caller that asked for a filesystem boundary and silently got a command on the host filesystem is worse off than one that got
    ///     an error, because it goes on believing the boundary is there.
    /// </remarks>
    private static SandboxLaunchDescriptor CreateIsolatedDescriptor(ProcessStartInfo startInfo,
        SandboxLaunchPolicy policy,
        SandboxContainment containment,
        SandboxLaunchContext? context)
    {
        if (containment.FilesystemIsolation is not { } isolation)
        {
            throw new SandboxIsolationUnavailableException(containment.FilesystemIsolationUnavailableReason ?? "this host cannot run a command behind a filesystem boundary");
        }

        if (context?.JailRoot is not { } jailRoot || string.IsNullOrWhiteSpace(jailRoot))
        {
            throw new SandboxIsolationUnavailableException("an isolated launch needs the sandbox jail directory, and none was supplied");
        }

        var lifetime = context.CommandTimeout is { } timeout && timeout > TimeSpan.Zero
            ? timeout + ScopeLifetimeGrace
            : DefaultScopeLifetime;

        var launch = SandboxIsolationLaunch.Create(isolation,
            new SandboxIsolationLaunchRequest
            {
                JailRoot = jailRoot,
                WorkingDirectory = ResolveSandboxWorkingDirectory(startInfo.WorkingDirectory, jailRoot),
                AdditionalEnvironment = context.CommandEnvironment ?? new Dictionary<string, string>(StringComparer.Ordinal),
                Executable = startInfo.FileName,
                Arguments = [.. startInfo.ArgumentList],
                ReadOnlyTrees = policy.ReadOnlyTrees,
                ResourceLimits = policy.ResourceLimits,
                ThreadLimit = policy.ThreadLimit,
                RuntimeMaxSeconds = (long)Math.Ceiling(lifetime.TotalSeconds),
                Role = policy.Role
            });

        try
        {
            return SandboxLaunchPlan.CreateIsolated(launch, policy, containment);
        }
        catch
        {
            launch.Dispose();
            throw;
        }
    }

    /// <summary>
    ///     Prepares the Windows AppContainer launch: the MXC ProcessContainer request the provider spawns instead of the start info.
    /// </summary>
    /// <remarks>
    ///     The policy was resolved at create time to the boundary it carries (ADR 0019), so this never re-reads eligibility. Fails closed with
    ///     <see cref="SandboxIsolationUnavailableException" /> like the bwrap branch. The child sees host path names, so the working directory
    ///     stays the host path the provider resolved inside the jail. Applied flags mirror <c>SandboxLaunchPlan.CreateIsolated</c>: the
    ///     boundary denies egress, ingress and host loopback itself, so network isolation is applied; there is no process group, scope unit
    ///     or ceiling (no Job Object; Q3: timeout-bounded only).
    /// </remarks>
    private SandboxLaunchDescriptor CreateAppContainerDescriptor(ProcessStartInfo startInfo, SandboxLaunchPolicy policy, SandboxLaunchContext? context)
    {
        // The registry refuses limits under this boundary; a path that skipped it must fail, never drop the ceiling silently.
        if (policy.ResourceLimits is not null)
        {
            throw new SandboxCapabilityNotSupportedException("the AppContainer boundary enforces no CPU, memory or process ceiling, and the launch policy carries one");
        }

        if (context?.JailRoot is not { } jailRoot || string.IsNullOrWhiteSpace(jailRoot))
        {
            throw new SandboxIsolationUnavailableException("an isolated launch needs the sandbox jail directory, and none was supplied");
        }

        var jail = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jailRoot));
        var paths = SandboxIsolatedPaths.ForHostJail(jail);
        try
        {
            // HOME and TEMP must exist before a tool reaches for them; the bwrap branch creates the same two in SandboxIsolationLaunch.
            _ = Directory.CreateDirectory(paths.Home);
            _ = Directory.CreateDirectory(paths.Temp);

            var request = MxcPolicyMapper.Build(new MxcLaunchRequest
            {
                Executable = startInfo.FileName,
                Arguments = [.. startInfo.ArgumentList],
                WorkingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory) ? jail : startInfo.WorkingDirectory,
                Environment = BuildAppContainerEnvironment(startInfo, context.CommandEnvironment, paths, policy.ReadOnlyTrees, policy.ThreadLimit),
                JailRoot = jail,
                ReadOnlyTrees = policy.ReadOnlyTrees,
                DeniedRoots = ResolveDeniedRoots(_deniedRootCandidates, jail, policy.ReadOnlyTrees),
                Timeout = context.CommandTimeout is { } timeout && timeout > TimeSpan.Zero ? timeout : null
            });
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("MXC AppContainer launch for jail {Jail}: explicit deny roots [{DeniedRoots}], read-only trees [{ReadOnlyTrees}].",
                    jail,
                    string.Join(", ", request.Filesystem?.DeniedPaths ?? []),
                    string.Join(", ", policy.ReadOnlyTrees));
            }

            return new SandboxLaunchDescriptor
            {
                FileName = startInfo.FileName,
                Arguments = [.. startInfo.ArgumentList],
                AppliedProcessGroup = false,
                AppliedResourceLimits = false,
                AppliedNetworkIsolation = true,
                AppliedFilesystemIsolation = true,
                MxcRequest = request
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new SandboxIsolationUnavailableException($"the AppContainer launch could not be prepared: {exception.Message}", exception);
        }
    }

    /// <summary>
    ///     The MXC child's complete environment (MXC merges nothing into a supplied block): the scrubbed allow-list, the jail's scratch and
    ///     profile variables and the bwrap chain's pinned thread counts, then the caller's own variables on top.
    /// </summary>
    /// <remarks>
    ///     <c>PATH</c> defaults to the system directories plus the engine-granted read-only trees, never the host <c>PATH</c>, whose
    ///     user-profile entries the child cannot read anyway. <c>HOMEDRIVE</c>/<c>HOMEPATH</c> name the real profile and are dropped.
    /// </remarks>
    internal static IReadOnlyDictionary<string, string> BuildAppContainerEnvironment(ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? commandEnvironment,
        SandboxIsolatedPaths paths,
        IReadOnlyList<string> readOnlyTrees,
        int threadLimit)
    {
        var requested = commandEnvironment ?? new Dictionary<string, string>(StringComparer.Ordinal);

        bool Requested(string name) =>
            requested.Keys.Any(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));

        // Windows variable names are case-insensitive; the start info already holds allow-list + caller variables.
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in startInfo.Environment)
        {
            if (value is not null && (!RealProfileVariableNames.Contains(name, StringComparer.OrdinalIgnoreCase) || Requested(name)))
            {
                environment[name] = value;
            }
        }

        foreach (var (name, value) in paths.ToEnvironment(includeWindowsProfile: true))
        {
            if (!Requested(name))
            {
                environment[name] = value;
            }
        }

        var threads = threadLimit.ToString(CultureInfo.InvariantCulture);
        foreach (var name in SandboxIsolatedChain.ThreadCountVariableNames.Where(name => !Requested(name)))
        {
            environment[name] = threads;
        }

        if (!Requested("PATH"))
        {
            var searchPath = new List<string>();
            if (environment.TryGetValue("SystemRoot", out var systemRoot) && !string.IsNullOrEmpty(systemRoot))
            {
                var root = systemRoot.TrimEnd('\\');
                searchPath.Add(root + "\\System32");
                searchPath.Add(root);
            }

            searchPath.AddRange(readOnlyTrees);
            environment["PATH"] = string.Join(';', searchPath);
        }

        return environment;
    }

    /// <summary>
    ///     The explicit deny roots for one launch: the candidates minus every one that is an ancestor of (or equal to) the jail or a
    ///     read-only tree.
    /// </summary>
    /// <remarks>
    ///     Under AppContainer + DACL a denied path is a DENY ACE, and a deny inherited from an ancestor can beat the grant on the jail or a
    ///     granted tree beneath it; on Windows the jail lives under the temp directory, itself under the user profile. The boundary does not
    ///     rest on these denies: an AppContainer child reaches nothing it was not granted (default deny, proven by the Windows canary test).
    ///     The explicit denies are an extra layer where they cannot shadow a grant (ADR 0019).
    /// </remarks>
    internal static IReadOnlyList<string> ResolveDeniedRoots(IReadOnlyList<string> candidates, string jailRoot, IReadOnlyList<string> readOnlyTrees)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var granted = readOnlyTrees.Append(jailRoot).Select(Normalize).ToList();
        var denied = new List<string>(candidates.Count);
        foreach (var candidate in candidates.Where(static candidate => !string.IsNullOrWhiteSpace(candidate)).Select(Normalize))
        {
            var shadowsAGrant = granted.Exists(path => string.Equals(path, candidate, comparison)
                                                       || path.StartsWith(candidate + Path.DirectorySeparatorChar, comparison));
            if (!shadowsAGrant && !denied.Contains(candidate, StringComparer.FromComparison(comparison)))
            {
                denied.Add(candidate);
            }
        }

        return denied;

        static string Normalize(string path) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    /// <summary>The user profile, the node data root and the engine directory: what the AppContainer boundary must never reach.</summary>
    private static IReadOnlyList<string> DefaultDeniedRootCandidates(INodeDataDirectory? nodeDataDirectory)
    {
        var candidates = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            AppContext.BaseDirectory
        };
        if (nodeDataDirectory?.Root is { Length: > 0 } dataRoot)
        {
            candidates.Insert(1, dataRoot);
        }

        return candidates;
    }

    /// <summary>
    ///     Translates the HOST working directory the provider resolved into the path the same directory has INSIDE the sandbox.
    /// </summary>
    /// <remarks>
    ///     The jail is <c>/work</c> there and never reachable at its host name, so a chain keeping the host path would chdir to a directory
    ///     that does not exist and the command would not start.
    /// </remarks>
    private static string ResolveSandboxWorkingDirectory(string? hostWorkingDirectory, string jailRoot)
    {
        if (string.IsNullOrEmpty(hostWorkingDirectory))
        {
            return SandboxIsolatedChain.WorkPath;
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(hostWorkingDirectory));
        var canonicalJail = Path.TrimEndingDirectorySeparator(Path.GetFullPath(jailRoot));
        if (string.Equals(canonical, canonicalJail, StringComparison.Ordinal))
        {
            return SandboxIsolatedChain.WorkPath;
        }

        if (!canonical.StartsWith(canonicalJail + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            // The provider's own jail guard should already have made this impossible. Refusing rather than falling
            // back to /work keeps a bug there from turning into a command that quietly ran somewhere else.
            throw new SandboxIsolationUnavailableException($"the working directory '{hostWorkingDirectory}' is not inside the sandbox jail");
        }

        var relative = canonical[(canonicalJail.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');

        return $"{SandboxIsolatedChain.WorkPath}/{relative}";
    }
}
