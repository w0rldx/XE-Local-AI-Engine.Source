namespace XE_Local_AI_Engine.Tests.Providers.Training;

using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;

/// <summary>
///     Shared fakes for the training-runtime tests. No test here spawns a process, touches the network, or provisions a
///     venv: the runtime is a multi-gigabyte install whose phase machine still has to be exercised end to end.
/// </summary>
internal static class TrainingRuntimeTestInfrastructure
{
    /// <summary>A handshake line matching what the real probe emits on this box.</summary>
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
        var directory = Path.Combine(cacheRoot, "uv", TrainingRuntimePins.UvVersion, TrainingRuntimePins.UvArchiveRootDirectory);
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, TrainingRuntimePins.UvExecutableName), "#!/bin/sh\n");
    }

    /// <summary>The default runner script: uv sync creates the venv interpreter, then the probe emits a valid handshake.</summary>
    public static FakeProcessRunner SucceedingRunner(string handshake = ValidHandshake)
    {
        return new FakeProcessRunner((file, args, logSink) =>
        {
            if (file.EndsWith(TrainingRuntimePins.UvExecutableName, StringComparison.Ordinal))
            {
                CreateInterpreter(args);
                logSink("Resolved 102 packages");
                return 0;
            }

            // The real probe's stdout is not clean: importing unsloth prints banner lines before the JSON.
            logSink("🦥 Unsloth: Will patch your computer to enable 2x faster free finetuning.");
            logSink(handshake);
            return 0;
        });
    }

    private static void CreateInterpreter(IReadOnlyList<string> args)
    {
        var projectIndex = args.ToList().IndexOf("--project");
        if (projectIndex < 0 || projectIndex + 1 >= args.Count)
        {
            return;
        }

        var binDirectory = Path.Combine(args[projectIndex + 1], ".venv", "bin");
        _ = Directory.CreateDirectory(binDirectory);
        File.WriteAllText(Path.Combine(binDirectory, "python"), "#!/bin/sh\n");
    }

    /// <summary>Records every invocation and answers from a caller-supplied script.</summary>
    internal sealed class FakeProcessRunner(Func<string, IReadOnlyList<string>, Action<string>, int> handler) : ITrainingProcessRunner
    {
        private readonly List<Invocation> _invocations = [];

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
            _invocations.Add(new Invocation(file, [.. args], environment, workingDirectory));
            return Task.FromResult(handler(file, args, logSink));
        }

        internal sealed record Invocation(string File,
            IReadOnlyList<string> Args,
            IReadOnlyDictionary<string, string> Environment,
            string WorkingDirectory);
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
    internal sealed class StubPrerequisiteProbe(TrainingRuntimePrerequisiteReport report) : ITrainingRuntimePrerequisiteProbe
    {
        public Task<TrainingRuntimePrerequisiteReport> ProbeAsync(CancellationToken ct)
        {
            return Task.FromResult(report);
        }

        public static StubPrerequisiteProbe Satisfied()
        {
            return new StubPrerequisiteProbe(new TrainingRuntimePrerequisiteReport(CanInstall: true,
                [new TrainingRuntimePrerequisiteItem(TrainingRuntimePrerequisiteKeys.Platform, Satisfied: true, "Running on Linux.")]));
        }

        public static StubPrerequisiteProbe Unsatisfied(string key)
        {
            return new StubPrerequisiteProbe(new TrainingRuntimePrerequisiteReport(CanInstall: false,
                [new TrainingRuntimePrerequisiteItem(key, Satisfied: false, "Not satisfied.")]));
        }
    }
}
