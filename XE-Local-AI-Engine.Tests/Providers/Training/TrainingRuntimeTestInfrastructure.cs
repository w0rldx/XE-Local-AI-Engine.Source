namespace XE_Local_AI_Engine.Tests.Providers.Training;

using XE_Local_AI_Engine.Providers.Python;
using XE_Local_AI_Engine.Providers.Python.Contracts;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Providers.Training.Contracts;

/// <summary>
///     Shared fakes for the training-runtime tests. No test here spawns a process, touches the network, or provisions a
///     venv: the runtime is a multi-gigabyte install whose phase machine still has to be exercised end to end.
/// </summary>
internal static class TrainingRuntimeTestInfrastructure
{
    /// <summary>A handshake line in the shape the real probe emits (synthetic values).</summary>
    public const string ValidHandshake =
        """{"bitsandbytes":"0.50.1","contractVersion":1,"cudaAvailable":true,"cudaVersion":"12.8","deviceCapability":"12.0","deviceName":"NVIDIA GeForce RTX 5090","numpy":"2.5.2","platform":"linux","python":"3.13.15","ready":true,"torch":"2.11.0+cu128","transformers":"4.57.6","unsloth":"2026.8.18"}""";

    /// <summary>
    ///     Writes the three files the runtime install reads out of the scripts directory. Contents are irrelevant — the
    ///     project files are copied verbatim and the probe script is executed by the fake runner.
    /// </summary>
    public static void WriteScripts(string scriptsDirectory)
    {
        _ = Directory.CreateDirectory(scriptsDirectory);
        File.WriteAllText(Path.Combine(scriptsDirectory, "pyproject.toml"), "[project]\nname = \"xe-training-runtime\"\n");
        File.WriteAllText(Path.Combine(scriptsDirectory, "uv.lock"), "version = 1\n");
        File.WriteAllText(Path.Combine(scriptsDirectory, "probe.py"), "print('{}')\n");
    }

    /// <summary>
    ///     Seeds the pinned uv into the cache so <see cref="UvBinaryAcquirer" /> takes its cache-hit path and no test
    ///     needs a network stub just to reach the phases after acquisition.
    /// </summary>
    public static void SeedCachedUv(string cacheRoot)
    {
        var directory = Path.Combine(cacheRoot, "uv", ManagedPythonPins.UvVersion, ManagedPythonPins.Current.ArchiveRootDirectory);
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, ManagedPythonPins.Current.ExecutableName), "#!/bin/sh\n");
    }

    /// <summary>
    ///     The default runner script: uv sync creates the venv interpreter and a <c>pyvenv.cfg</c> whose <c>home</c> lies
    ///     under <c>UV_PYTHON_INSTALL_DIR</c>, as real uv writes it; then the probe emits a valid handshake.
    /// </summary>
    public static FakeProcessRunner SucceedingRunner(string handshake = ValidHandshake)
    {
        return new FakeProcessRunner((file, args, environment, logSink) =>
        {
            if (file.EndsWith(ManagedPythonPins.Current.ExecutableName, StringComparison.Ordinal))
            {
                CreateInterpreter(args, environment.GetValueOrDefault("UV_PYTHON_INSTALL_DIR"));
                logSink("Resolved 102 packages");
                return 0;
            }

            // The real probe's stdout is not clean: importing unsloth prints banner lines before the JSON.
            logSink("🦥 Unsloth: Will patch your computer to enable 2x faster free finetuning.");
            logSink(handshake);
            return 0;
        });
    }

    /// <summary>Lays out a venv the way uv leaves one: <c>bin/python</c> plus a <c>pyvenv.cfg</c> naming the CPython it runs on.</summary>
    public static void WriteVenv(string venvDirectory, string? pythonInstallDirectory)
    {
        var binDirectory = Path.Combine(venvDirectory, ".venv", "bin");
        _ = Directory.CreateDirectory(binDirectory);
        File.WriteAllText(Path.Combine(binDirectory, "python"), "#!/bin/sh\n");
        if (pythonInstallDirectory is not null)
        {
            File.WriteAllText(Path.Combine(venvDirectory, ".venv", "pyvenv.cfg"),
                $"home = {Path.Combine(pythonInstallDirectory, "cpython-3.13-linux-x86_64-gnu", "bin")}\nversion_info = 3.13\n");
        }
    }

    /// <summary>Seeds the per-feature <c>uv</c>, <c>pythons</c> and <c>uv-cache</c> a root kept before the shared store.</summary>
    public static void SeedLegacyToolchain(string featureRoot)
    {
        foreach (var name in new[] { "uv", "pythons", "uv-cache" })
        {
            var directory = Directory.CreateDirectory(Path.Combine(featureRoot, name, "nested"));
            File.WriteAllText(Path.Combine(directory.FullName, "marker"), name);
        }
    }

    private static void CreateInterpreter(IReadOnlyList<string> args, string? pythonInstallDirectory)
    {
        var projectIndex = args.ToList().IndexOf("--project");
        if (projectIndex < 0 || projectIndex + 1 >= args.Count)
        {
            return;
        }

        WriteVenv(args[projectIndex + 1], pythonInstallDirectory);
    }

    /// <summary>Records every invocation and answers from a caller-supplied script.</summary>
    internal sealed class FakeProcessRunner : IPythonToolRunner
    {
        private readonly List<Invocation> _invocations = [];
        private readonly Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Action<string>, int> _handler;

        public FakeProcessRunner(Func<string, IReadOnlyList<string>, Action<string>, int> handler)
            : this((file, args, _, logSink) => handler(file, args, logSink))
        {
        }

        public FakeProcessRunner(Func<string, IReadOnlyList<string>, IReadOnlyDictionary<string, string>, Action<string>, int> handler)
        {
            _handler = handler;
        }

        public IReadOnlyList<Invocation> Invocations => _invocations;

        public Task<int> RunAsync(string file,
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string> environment,
            string workingDirectory,
            Action<string> logSink,
            TimeSpan timeout,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _invocations.Add(new Invocation
            {
                File = file,
                Args = [.. args],
                Environment = environment,
                WorkingDirectory = workingDirectory
            });
            return Task.FromResult(_handler(file, args, environment, logSink));
        }

        internal sealed class Invocation
        {
            public required string File { get; init; }

            public required IReadOnlyList<string> Args { get; init; }

            public required IReadOnlyDictionary<string, string> Environment { get; init; }

            public required string WorkingDirectory { get; init; }
        }
    }

    /// <summary>Captures published status events so phase order can be asserted.</summary>
    internal sealed class RecordingPublisher : ITrainingRuntimeEventPublisher
    {
        private readonly List<TrainingRuntimeStatusHubEvent> _events = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<TrainingRuntimeStatusHubEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return [.. _events];
                }
            }
        }

        public Task PublishStatusAsync(TrainingRuntimeStatusHubEvent statusEvent, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                _events.Add(statusEvent);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A probe with a fixed verdict, so runtime tests do not depend on the host's real disk or GPU.</summary>
    internal sealed class StubPrerequisiteProbe : ITrainingRuntimePrerequisiteProbe
    {
        private readonly TrainingRuntimePrerequisiteReport _report;

        public StubPrerequisiteProbe(TrainingRuntimePrerequisiteReport report)
        {
            _report = report;
        }

        public Task<TrainingRuntimePrerequisiteReport> ProbeAsync(CancellationToken ct)
        {
            return Task.FromResult(_report);
        }

        public static StubPrerequisiteProbe Satisfied()
        {
            return new StubPrerequisiteProbe(new TrainingRuntimePrerequisiteReport
            {
                CanInstall = true,
                Items =
                [
                    new TrainingRuntimePrerequisiteItem
                    {
                        Key = TrainingRuntimePrerequisiteKeys.Platform,
                        Satisfied = true,
                        Detail = "Running on Linux."
                    }
                ]
            });
        }

        public static StubPrerequisiteProbe Unsatisfied(string key)
        {
            return new StubPrerequisiteProbe(new TrainingRuntimePrerequisiteReport
            {
                CanInstall = false,
                Items =
                [
                    new TrainingRuntimePrerequisiteItem
                    {
                        Key = key,
                        Satisfied = false,
                        Detail = "Not satisfied."
                    }
                ]
            });
        }
    }
}
