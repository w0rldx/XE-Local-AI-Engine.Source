namespace XE_Local_AI_Engine.Testing.FakeDocker;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

/// <summary>
///     Everything one <see cref="FakeDockerServer" /> knows. A test drives the scenario by mutating this before or
///     between calls; there is no HTTP control plane, because the test that seeds this state is running in the same
///     process as the server that reads it.
/// </summary>
public sealed class FakeDockerState
{
    /// <summary>The <c>ScriptExec</c> container wildcard: an outcome registered for every container.</summary>
    public const string AnyContainer = "*";

    /// <summary>
    ///     The <c>ScriptExec</c> command wildcard: an outcome registered for every command run in that container.
    ///     It is what makes the write probe scriptable at all — the client builds that command itself, out of a
    ///     shell fragment no caller supplies, so a test cannot name it without copying the fragment verbatim.
    /// </summary>
    public const string AnyCommand = "*";

    /// <summary>The first port handed out for a daemon-assigned publication, at the bottom of the ephemeral range.</summary>
    private const int FirstEphemeralPort = 32768;

    private readonly ConcurrentDictionary<string, FakeDockerExecOutcome> _execScripts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlyList<FakeDockerPullLine>> _pullScripts = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<FakeDockerRequest> _requests = new();
    private int _lastEphemeralPort = FirstEphemeralPort - 1;

    /// <param name="options">The daemon identity this state answers <c>/version</c> and <c>/info</c> from.</param>
    public FakeDockerState(FakeDockerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        DaemonId = options.DaemonId;
        ServerVersion = options.ServerVersion;
        ApiVersion = options.ApiVersion;
        MinimumApiVersion = options.MinimumApiVersion;
        OperatingSystem = options.OperatingSystem;
        Rootless = options.Rootless;
        SupportsSeccomp = options.SupportsSeccomp;
    }

    /// <summary>The daemon installation id. Blank makes the production probe fail closed, which is a valid scenario.</summary>
    public string DaemonId { get; set; }

    /// <summary>The daemon's own version string.</summary>
    public string ServerVersion { get; set; }

    /// <summary>The Engine API version this daemon reports.</summary>
    public string ApiVersion { get; set; }

    /// <summary>The oldest Engine API version this daemon still serves.</summary>
    public string MinimumApiVersion { get; set; }

    /// <summary>The daemon's container operating system.</summary>
    public string OperatingSystem { get; set; }

    /// <summary>Whether <c>/info</c> lists <c>name=rootless</c>.</summary>
    public bool Rootless { get; set; }

    /// <summary>Whether <c>/info</c> lists <c>name=seccomp</c>.</summary>
    public bool SupportsSeccomp { get; set; }

    /// <summary>
    ///     Whether a <c>touch</c> of a path under a declared bind mount appears on the HOST side of that mount.
    ///     <para>
    ///         The one wire effect a scripted fake cannot leave unmodelled, and the same narrow emulation the
    ///         in-memory <c>FakeDockerRuntimeClient</c> already carries for the same reason. The sandbox provider's
    ///         create path ends by touching a probe file inside the container and then looking for it on the host:
    ///         that round trip is how it detects a uid mapping the daemon's own read-back cannot report, so a fake
    ///         that never produced the file would make every sandbox the provider creates unusable. Off by default,
    ///         because a fake that started really writing to the host without being asked would be worse.
    ///     </para>
    /// </summary>
    public bool WritesThroughBindMounts { get; set; }

    /// <summary>Images the daemon holds, keyed by their full digest-pinned reference.</summary>
    public ConcurrentDictionary<string, FakeDockerImage> Images { get; } = new(StringComparer.Ordinal);

    /// <summary>Containers the daemon holds, keyed by id.</summary>
    public ConcurrentDictionary<string, FakeDockerContainer> Containers { get; } = new(StringComparer.Ordinal);

    /// <summary>Networks the daemon holds, keyed by name — a name identifies one network, which is why a create conflicts.</summary>
    public ConcurrentDictionary<string, FakeDockerNetwork> Networks { get; } = new(StringComparer.Ordinal);

    /// <summary>Every <c>exec</c> the client created, keyed by exec id.</summary>
    public ConcurrentDictionary<string, FakeDockerExecSession> ExecSessions { get; } = new(StringComparer.Ordinal);

    /// <summary>Every request the fake served, oldest first.</summary>
    public IReadOnlyList<FakeDockerRequest> RecordedRequests => [.. _requests];

    /// <summary>
    ///     The next host port for a publication that asked the daemon to choose one. Distinct per call, so two
    ///     published ports never read back as the same port and hide a mapping that lost one of them.
    /// </summary>
    public int NextEphemeralPort()
    {
        return Interlocked.Increment(ref _lastEphemeralPort);
    }

    /// <summary>A 64-hex identifier in the shape the daemon mints ids and digests in.</summary>
    public static string NewId()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>Make an image present, optionally declaring the container paths its own <c>VOLUME</c> instructions name.</summary>
    public FakeDockerImage SeedImage(string reference, params string[] declaredVolumes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentNullException.ThrowIfNull(declaredVolumes);

        var image = new FakeDockerImage
        {
            Reference = reference,
            DeclaredVolumes = [.. declaredVolumes]
        };

        Images[reference] = image;
        return image;
    }

    /// <summary>Make a network present without going through <c>POST /networks/create</c> — a foreign network, say.</summary>
    public FakeDockerNetwork SeedNetwork(string name, IReadOnlyDictionary<string, string>? labels = null, string driver = "bridge")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var network = new FakeDockerNetwork
        {
            Id = NewId(),
            Name = name,
            Driver = driver
        };

        if (labels is not null)
        {
            foreach (var (key, value) in labels)
            {
                network.Labels[key] = value;
            }
        }

        Networks[name] = network;
        return network;
    }

    /// <summary>
    ///     Register the outcome of one <c>exec</c>. <paramref name="commandLine" /> is the executable and its
    ///     arguments joined by single spaces, the same key the in-memory <c>FakeDockerRuntimeClient</c> uses.
    ///     <paramref name="containerId" /> may be <see cref="AnyContainer" /> to answer for every container.
    /// </summary>
    public void ScriptExec(string containerId,
        string commandLine,
        long exitCode,
        string standardOutput = "",
        string standardError = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        _execScripts[ExecKey(containerId, commandLine)] = new FakeDockerExecOutcome { ExitCode = exitCode, StandardOutput = standardOutput, StandardError = standardError };
    }

    /// <summary>
    ///     The outcome for one exec, most specific first: this container and this command, then either wildcard,
    ///     then both, then the default — exit zero and no output, matching what the in-memory fake answers for an
    ///     unscripted command.
    /// </summary>
    public FakeDockerExecOutcome ResolveExec(string containerId, string commandLine)
    {
        string[] keys =
        [
            ExecKey(containerId, commandLine),
            ExecKey(AnyContainer, commandLine),
            ExecKey(containerId, AnyCommand),
            ExecKey(AnyContainer, AnyCommand)
        ];

        foreach (var key in keys)
        {
            if (_execScripts.TryGetValue(key, out var scripted))
            {
                return scripted;
            }
        }

        return new FakeDockerExecOutcome { ExitCode = 0 };
    }

    /// <summary>Register the exact progress stream <c>POST /images/create</c> serves for one image reference.</summary>
    public void ScriptPull(string imageReference, params FakeDockerPullLine[] lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);
        ArgumentNullException.ThrowIfNull(lines);

        _pullScripts[imageReference] = [.. lines];
    }

    /// <summary>
    ///     The scripted stream for one image, or a realistic two-layer default. The default opens with the daemon's
    ///     own <c>Pulling from …</c> narration line, which carries an id and is still not a layer: reproducing it is
    ///     the only way a layer-count assertion against this fake means anything.
    /// </summary>
    public IReadOnlyList<FakeDockerPullLine> ResolvePull(string imageReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageReference);

        return _pullScripts.TryGetValue(imageReference, out var scripted) ? scripted : DefaultPullScript(imageReference);
    }

    /// <summary>Record one served request so a test can assert on what the client sent.</summary>
    public void Record(FakeDockerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _requests.Enqueue(request);
    }

    /// <summary>
    ///     The value of one query-string parameter on the most recent request whose path ends with
    ///     <paramref name="pathSuffix" />, or null when no such request was served or it carried no such parameter.
    ///     The graceful-stop period the client sent as <c>t=</c> is what this exists for.
    /// </summary>
    public string? LastQueryValue(string pathSuffix, string parameter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathSuffix);
        ArgumentException.ThrowIfNullOrWhiteSpace(parameter);

        var match = _requests.LastOrDefault(request => request.Path.EndsWith(pathSuffix, StringComparison.Ordinal));
        return match is not null && match.Query.TryGetValue(parameter, out var value) ? value : null;
    }

    private static IReadOnlyList<FakeDockerPullLine> DefaultPullScript(string imageReference)
    {
        var separator = imageReference.IndexOf('@', StringComparison.Ordinal);
        var repository = separator < 0 ? imageReference : imageReference[..separator];
        var lines = new List<FakeDockerPullLine>
        {
            new()
            {
                Id = separator < 0 ? repository : imageReference[(separator + 1)..],
                Status = "Pulling from library/" + repository
            }
        };

        for (var layer = 1; layer <= 2; layer++)
        {
            var id = layer.ToString(CultureInfo.InvariantCulture).PadLeft(totalWidth: 12, '0');
            long total = 1024 * layer;

            lines.Add(new FakeDockerPullLine
            {
                Id = id,
                Status = "Pulling fs layer"
            });
            lines.Add(new FakeDockerPullLine
            {
                Id = id,
                Status = "Downloading",
                Current = total / 2,
                Total = total
            });
            lines.Add(new FakeDockerPullLine
            {
                Id = id,
                Status = "Download complete"
            });
            lines.Add(new FakeDockerPullLine
            {
                Id = id,
                Status = "Extracting",
                Current = total,
                Total = total
            });
            lines.Add(new FakeDockerPullLine
            {
                Id = id,
                Status = "Pull complete"
            });
        }

        lines.Add(new FakeDockerPullLine
        {
            Status = "Status: Downloaded newer image for " + imageReference
        });

        return lines;
    }

    private static string ExecKey(string containerId, string commandLine)
    {
        return containerId + "\n" + commandLine;
    }
}
