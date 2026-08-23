namespace XE_Local_AI_Engine.Tests.Diagnostics;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using TUnit.Core.Exceptions;
using XE_Local_AI_Engine.Testing.FakeOllama;
using XE_Local_AI_Engine.Tests.Testing;

    /// <summary>
///     Opt-in profiler for the cost of ONE <see cref="TestServerWebAppFactory" /> host, the unit that 61 test classes
///     pay per test and 42 pay per class. It answers the only question that decides whether a per-host optimisation is
///     worth building: where do the ~2 s go?
///     <para>
///         Skipped unless <c>XE_FIXTURE_TIMING=1</c>. It builds 30+ hosts sequentially, which is minutes of wall clock
///         and pure noise inside a normal suite run — and it deliberately measures wall clock, so it must not share the
///         box with the rest of the suite (<c>[NotInParallel]</c> keeps it off the other classes; run the class alone
///         for numbers worth quoting).
///     </para>
///     <para>
///         Every phase below is observed from OUTSIDE the product: the fixture's own seams are enough, so no product
///         code is touched. <c>ConfigureAdditionalTestServices</c> is invoked from inside
///         <c>ProgramAppCustomization.ConfigureBuilder</c>, i.e. immediately before <c>builder.Build()</c>, which splits
///         <c>CreateAppAsync</c> into "compose the builder" and "build + migrate + start". The migration share of the
///         second half is isolated by the A/B in <see cref="ProfileHostBuildPhasesAsync" />: the default host (seeded
///         from the fixture's migrated template, migrations no-op) against one built with
///         <c>UsePreMigratedDatabase = false</c> on an empty SQLite file (migrations run).
///     </para>
/// </summary>
[NotInParallel]
public sealed class TestServerWebAppFactoryTimingTests
{
    private const string EnableVariable = "XE_FIXTURE_TIMING";
    private const int Iterations = 10;

    // Phase names, also the CSV's phase column.
    private const string PhaseConstructor = "ctor (dirs + FakeOllama)";
    private const string PhaseFakeOllamaAlone = "FakeOllamaServer.StartAsync (alone)";
    private const string PhaseFakeOllamaDisposeAlone = "FakeOllamaServer.DisposeAsync (alone)";
    private const string PhaseHostBuild = "EnsureApp: CreateAppAsync + StartAsync";
    private const string PhaseBuilderCompose = "  ...of which: builder + AddServices (pre-Build)";
    private const string PhaseBuildAndStart = "  ...of which: Build + migrate + start (post-Build)";
    private const string PhaseHostBuildFreshControl = "EnsureApp on FRESH db, no template (A/B control)";
    private const string PhaseTemplateCopy = "File.Copy of migrated template";
    private const string PhaseCreateClient = "CreateClient()";
    private const string PhaseFirstRequest = "first request GET /health/live";
    private const string PhaseDispose = "DisposeAsync";

    [Test]
    public async Task ProfileHostBuildPhasesAsync()
    {
        RequireOptIn();

        var samples = new List<Sample>();
        var workspace = Path.Combine(Path.GetTempPath(), $"xe-fixture-timing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        try
        {
            await MeasureFakeOllamaAloneAsync(samples).ConfigureAwait(false);
            MeasureTemplateCopy(samples, workspace);
            await MeasureHostsAsync(samples).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(workspace);
        }

        var report = Report(samples);
        TestContext.Current?.Output.WriteLine(report);
        AppendCsv(samples);

        AssertEx.NotEmpty(samples);
    }

    /// <summary>
    ///     <see cref="Iterations" /> full host lifecycles exactly as the suite builds them (<see cref="PhaseHostBuild" />
    ///     — the fixture's migrated template included, the shape every endpoint test class pays), each paired with one
    ///     host on a genuinely empty database (<see cref="PhaseHostBuildFreshControl" />). The delta between them IS the
    ///     migration cost the template avoids.
    ///     <para>
    ///         The two are INTERLEAVED, never run as two consecutive blocks: this box is shared with other test runs and
    ///         the first hosts of a process are still JIT-warming, so a block layout charges whichever leg goes first for
    ///         both — an earlier version of this profiler did exactly that and overstated the delta.
    ///     </para>
    /// </summary>
    private static async Task MeasureHostsAsync(List<Sample> samples)
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            await MeasureOneHostAsync(samples, iteration, usePreMigratedDatabase: true, PhaseHostBuild, measureEveryPhase: true).ConfigureAwait(false);
            await MeasureOneHostAsync(samples, iteration, usePreMigratedDatabase: false, PhaseHostBuildFreshControl, measureEveryPhase: false).ConfigureAwait(false);
        }
    }

    private static async Task MeasureOneHostAsync(List<Sample> samples,
        int iteration,
        bool usePreMigratedDatabase,
        string hostBuildPhase,
        bool measureEveryPhase)
    {
        var hostBuild = Stopwatch.StartNew();
        var preBuild = TimeSpan.Zero;

        var constructor = Stopwatch.StartNew();
        var factory = new TestServerWebAppFactory
        {
            UsePreMigratedDatabase = usePreMigratedDatabase,

            // Invoked from ConfigureBuilder, immediately before builder.Build(): the split point between composing the
            // builder (configuration + AddServices + the fixture's own overrides) and everything after it.
            ConfigureAdditionalTestServices = _ => preBuild = hostBuild.Elapsed
        };
        constructor.Stop();

        try
        {
            if (measureEveryPhase)
            {
                Record(samples, iteration, PhaseConstructor, constructor.Elapsed);
            }

            hostBuild.Restart();
            var services = factory.Services;
            hostBuild.Stop();
            AssertEx.NotNull(services);

            Record(samples, iteration, hostBuildPhase, hostBuild.Elapsed);
            if (measureEveryPhase)
            {
                Record(samples, iteration, PhaseBuilderCompose, preBuild);
                Record(samples, iteration, PhaseBuildAndStart, hostBuild.Elapsed - preBuild);

                await MeasureRequestPhasesAsync(samples, iteration, factory).ConfigureAwait(false);
            }

            var dispose = Stopwatch.StartNew();
            await factory.DisposeAsync().ConfigureAwait(false);
            dispose.Stop();
            if (measureEveryPhase)
            {
                Record(samples, iteration, PhaseDispose, dispose.Elapsed);
            }
        }
        finally
        {
            // Idempotent: a no-op when the timed call above already ran.
            await factory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task MeasureRequestPhasesAsync(List<Sample> samples, int iteration, TestServerWebAppFactory factory)
    {
        var createClient = Stopwatch.StartNew();
        using var client = factory.CreateClient();
        createClient.Stop();
        Record(samples, iteration, PhaseCreateClient, createClient.Elapsed);

        var request = Stopwatch.StartNew();
        using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative)).ConfigureAwait(false);
        request.Stop();
        Record(samples, iteration, PhaseFirstRequest, request.Elapsed);
        AssertEx.True(response.IsSuccessStatusCode, $"/health/live answered {(int)response.StatusCode}.");
    }

    /// <summary>The Kestrel listener the fixture constructor starts eagerly, timed on its own so the constructor's cost splits.</summary>
    private static async Task MeasureFakeOllamaAloneAsync(List<Sample> samples)
    {
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var start = Stopwatch.StartNew();
            var server = await FakeOllamaServer.StartAsync(new FakeOllamaOptions
            {
                Models = ["qwen3.5:0.8b", "qwen3-embedding:0.6b"]
            }).ConfigureAwait(false);
            start.Stop();
            Record(samples, iteration, PhaseFakeOllamaAlone, start.Elapsed);

            var dispose = Stopwatch.StartNew();
            await server.DisposeAsync().ConfigureAwait(false);
            dispose.Stop();
            Record(samples, iteration, PhaseFakeOllamaDisposeAlone, dispose.Elapsed);
        }
    }

    /// <summary>What the fixture pays per host in place of running migrations. Also forces the template to exist before the timed legs.</summary>
    private static void MeasureTemplateCopy(List<Sample> samples, string workspace)
    {
        var template = TestServerWebAppFactory.EnsureMigratedTemplate();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var destination = Path.Combine(workspace, $"copy-{iteration}.sqlite");
            var copy = Stopwatch.StartNew();
            File.Copy(template, destination);
            copy.Stop();
            Record(samples, iteration, PhaseTemplateCopy, copy.Elapsed);
            File.Delete(destination);
        }
    }

    private static void Record(List<Sample> samples, int iteration, string phase, TimeSpan elapsed) =>
        samples.Add(new Sample(iteration, phase, elapsed.TotalMilliseconds));

    private static string Report(List<Sample> samples)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"TestServerWebAppFactory host-build timing — {Iterations} iterations per phase")
               .AppendLine()
               .AppendLine("phase                                                   median      min      max   first")
               .AppendLine("-------------------------------------------------------------------------------------------");

        foreach (var group in samples.GroupBy(sample => sample.Phase, StringComparer.Ordinal))
        {
            var ordered = group.Select(sample => sample.Milliseconds).Order().ToArray();
            var first = group.First(sample => sample.Iteration == 0).Milliseconds;
            builder.Append(CultureInfo.InvariantCulture,
                       $"{group.Key,-52}{Median(ordered),9:F1}{ordered[0],9:F1}{ordered[^1],9:F1}{first,8:F1}")
                   .AppendLine();
        }

        return builder.ToString();
    }

    private static double Median(double[] ordered) =>
        ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[(ordered.Length / 2) - 1] + ordered[ordered.Length / 2]) / 2;

    /// <summary>
    ///     Appends raw samples (never medians) so repeated runs accumulate into one file and can be aggregated across
    ///     runs afterwards — the only way to see through the CPU contention of a shared box.
    /// </summary>
    private static void AppendCsv(List<Sample> samples)
    {
        var path = Path.Combine(Path.GetTempPath(), "xe-fixture-timing.csv");
        var builder = new StringBuilder();
        if (!File.Exists(path))
        {
            builder.AppendLine("runId,iteration,phase,ms");
        }

        var runId = Guid.NewGuid().ToString("N")[..8];
        foreach (var sample in samples)
        {
            builder.Append(CultureInfo.InvariantCulture, $"{runId},{sample.Iteration},\"{sample.Phase}\",{sample.Milliseconds:F3}")
                   .AppendLine();
        }

        File.AppendAllText(path, builder.ToString());
        TestContext.Current?.Output.WriteLine($"Raw samples appended to {path}");
    }

    private static void RequireOptIn()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(EnableVariable), "1", StringComparison.Ordinal))
        {
            throw new SkipTestException($"SKIPPED — this is a profiler, not an assertion: it builds {Iterations * 2} hosts "
                                        + $"sequentially (minutes of wall clock). Set {EnableVariable}=1 to run it.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a locked file is not a measurement failure.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; ignore.
        }
    }

    private sealed record Sample(int Iteration, string Phase, double Milliseconds);
}
