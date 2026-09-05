namespace XE_Local_AI_Engine.Tests.Providers.Training;

using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;
using XE_Local_AI_Engine.Providers.Training.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using static TrainingRuntimeTestInfrastructure;
using OS = TUnit.Core.Enums.OS;

public sealed class TrainingRuntimePrerequisiteProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-training-probe-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Probe_WhenEverythingIsPresent_ReportsEveryItemSatisfied()
    {
        var report = await ProbeAsync(WithScripts(), DriverPresent());

        AssertEx.Contains(report.Items, static item => item.Key == TrainingRuntimePrerequisiteKeys.Platform && item.Satisfied);
        AssertEx.Contains(report.Items, static item => item.Key == TrainingRuntimePrerequisiteKeys.Lockfile && item.Satisfied);
        AssertEx.Contains(report.Items, static item => item.Key == TrainingRuntimePrerequisiteKeys.NvidiaDriver && item.Satisfied);
        AssertEx.Contains(report.Items, static item => item.Key == TrainingRuntimePrerequisiteKeys.FreeDisk);
        AssertEx.Contains(report.Items, static item => item.Key == TrainingRuntimePrerequisiteKeys.SystemMemory);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Probe_WhenTheLockfileIsMissing_RefusesAndSaysSo()
    {
        var report = await ProbeAsync(Path.Combine(_root, "no-scripts"), DriverPresent());

        AssertEx.False(report.CanInstall);
        var item = AssertEx.NotNull(report.Items.FirstOrDefault(static item => item.Key == TrainingRuntimePrerequisiteKeys.Lockfile),
            "The lockfile item must always be reported.");
        AssertEx.False(item.Satisfied);
        AssertEx.Contains(item.Detail, "lockfile");
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Probe_WhenNvidiaSmiIsAbsent_ReportsNoDriverRatherThanThrowing()
    {
        // The runner throwing is what an absent nvidia-smi looks like: setsid cannot exec it.
        var runner = new FakeProcessRunner((_, _, _) => throw new TrainingRuntimeException("The process did not start."));

        var report = await ProbeAsync(WithScripts(), runner);

        AssertEx.False(report.CanInstall);
        AssertEx.Contains(report.Items,
            static item => item.Key == TrainingRuntimePrerequisiteKeys.NvidiaDriver && !item.Satisfied);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Probe_WhenNvidiaSmiExitsNonZero_ReportsNoDriver()
    {
        var report = await ProbeAsync(WithScripts(), new FakeProcessRunner((_, _, _) => 9));

        AssertEx.Contains(report.Items,
            static item => item.Key == TrainingRuntimePrerequisiteKeys.NvidiaDriver && !item.Satisfied);
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task Probe_DoesNotCreateTheCacheRoot()
    {
        var cacheRoot = Path.Combine(_root, "never-created");
        _ = await new TrainingRuntimePrerequisiteProbe(DriverPresent(), cacheRoot, WithScripts()).ProbeAsync(CancellationToken.None);

        AssertEx.False(Directory.Exists(cacheRoot),
            "Probing is read-only: the UI calls it before the operator commits to a multi-gigabyte install.");
    }

    [Test]
    public void ResolveScriptsDirectory_FindsTheCommittedLockfileFromATestRun()
    {
        // The shipped app reads the scripts from training-scripts/ beside the executable (an explicit Content Include
        // in the Client csproj, because the repo root is outside the publish glob); a dev or test run walks up to
        // tools/training/ instead. If this resolution breaks, the runtime install fails its own lockfile prerequisite
        // with nothing in the diff to explain why.
        var resolved = TrainingRuntimeLayout.ResolveScriptsDirectory();

        AssertEx.True(File.Exists(Path.Combine(resolved, TrainingRuntimeLayout.LockfileName)),
            $"The pinned lockfile must be resolvable from a test run; resolved to '{resolved}'.");
        AssertEx.True(File.Exists(Path.Combine(resolved, TrainingRuntimeLayout.ProbeScriptName)),
            "The probe script ships beside the lockfile.");
    }

    private Task<TrainingRuntimePrerequisiteReport> ProbeAsync(string scriptsDirectory, ITrainingProcessRunner runner)
    {
        return new TrainingRuntimePrerequisiteProbe(runner, Path.Combine(_root, "cache"), scriptsDirectory)
            .ProbeAsync(CancellationToken.None);
    }

    private string WithScripts()
    {
        var scripts = Path.Combine(_root, "scripts");
        WriteScripts(scripts);
        return scripts;
    }

    private static FakeProcessRunner DriverPresent()
    {
        return new FakeProcessRunner((_, _, logSink) =>
        {
            logSink("610.88, NVIDIA GeForce RTX 5090");
            return 0;
        });
    }
}
