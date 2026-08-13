namespace XE_Local_AI_Engine.Client.Persistence.Tests.Knowledge.RetrievalEval;

using System.Globalization;
using XE_Local_AI_Engine.Client.Persistence.Implementation;
using XE_Local_AI_Engine.Client.Persistence.Tests.Testing;

[NotInParallel]
public sealed class RetrievalCapacityBenchmarkTests : IDisposable
{
    private const string ProfileVariable = "XE_RAG_CAPACITY_PROFILE";
    private const string GateVariable = "XE_RAG_CAPACITY_ENFORCE_P95";
    private const string TargetVariable = "XE_RAG_CAPACITY_P95_TARGET_MS";
    private const string ReportVariable = "XE_RAG_CAPACITY_REPORT";

    private readonly INodeSqliteKeyHolder _keyHolder = new NullNodeSqliteKeyHolder();
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }

        _keyHolder.Dispose();
    }

    [Test]
    public void Profiles_DefineFixedSeedCapacityStepsThroughOneMillionChunks()
    {
        var profiles = RetrievalCapacityProfile.All.Values.OrderBy(static profile => profile.ChunkCount).ToList();

        AssertEx.Equal(expected: RetrievalCapacityBenchmark.Seed, actual: 0x5EED_2026);
        AssertEx.True(profiles.Select(static profile => profile.ChunkCount).SequenceEqual([256, 10_000, 100_000, 250_000, 500_000, 1_000_000]));
        AssertEx.True(profiles.All(static profile => profile.NamespaceCount >= 2));
    }

    [Test]
    public void RefuseVacuousQueryRun_ZeroQueries_Throws()
    {
        var exception = AssertEx.Throws<InvalidOperationException>(() => RetrievalCapacityBenchmark.RefuseVacuousQueryRun(0, 0, 0));

        AssertEx.True(exception.Message.Contains("zero-query", StringComparison.Ordinal));
    }

    [Test]
    public void RefuseVacuousQueryRun_AnswerableQueriesWithZeroResults_Throws()
    {
        var exception = AssertEx.Throws<InvalidOperationException>(() => RetrievalCapacityBenchmark.RefuseVacuousQueryRun(5, 4, 0));

        AssertEx.True(exception.Message.Contains("zero-result", StringComparison.Ordinal));
    }

    [Test]
    public async Task SmokeProfile_RealFtsVectorFusionAndHydration_ReportsCorrectnessAndCapacityMetrics()
    {
        var report = await RunAsync(RetrievalCapacityProfile.Parse("smoke")).ConfigureAwait(false);

        AssertEx.Equal(expected: 256, report.Profile.ChunkCount);
        AssertEx.Equal(expected: 10, report.Query.QueryCount);
        AssertEx.Equal(expected: 8, report.Query.AnswerableQueryCount);
        AssertEx.Equal(expected: 2, report.Query.NoAnswerQueryCount);
        AssertEx.Equal(expected: 8, report.Query.NonEmptyAnswerableResults);
        AssertEx.True(report.Query.RecallAtK >= 1d, report.Summarize());
        AssertEx.True(report.Query.MeanReciprocalRank >= 1d, report.Summarize());
        AssertEx.True(report.Query.NdcgAtK >= 1d, report.Summarize());
        AssertEx.True(report.Query.NoAnswerFalsePositiveRate >= 1d,
            "The current hybrid pipeline has no abstention threshold, so a dense no-answer query must disclose its false positive instead of reporting vacuous accuracy. " + report.Summarize());
        AssertEx.True(report.Build.CorpusMilliseconds > 0d);
        AssertEx.True(report.Build.FtsIndexMilliseconds > 0d);
        AssertEx.True(report.Build.VectorIndexMilliseconds > 0d);
        AssertEx.True(report.Build.DatabaseBytes > 0L);
        AssertEx.True(report.Build.SampledWorkingSetHighWaterBytes >= report.Build.WorkingSetBaselineBytes);
        AssertEx.True(report.Build.SampledManagedHeapHighWaterBytes >= report.Build.ManagedHeapBaselineBytes);
        AssertEx.True(report.Query.Fts.P95Milliseconds >= report.Query.Fts.P50Milliseconds);
        AssertEx.True(report.Query.Vector.P95Milliseconds >= report.Query.Vector.P50Milliseconds);
        AssertEx.True(report.Query.Fusion.P95Milliseconds >= report.Query.Fusion.P50Milliseconds);
        AssertEx.True(report.Query.EndToEnd.P95Milliseconds >= report.Query.EndToEnd.P50Milliseconds);
        Console.WriteLine(report.Summarize());
    }

    [Test]
    public async Task OptInProfile_DeclaredCapacity_ReportsAndOptionallyGatesFiveHundredMillisecondP95()
    {
        var profileName = Environment.GetEnvironmentVariable(ProfileVariable);
        if (string.IsNullOrWhiteSpace(profileName) || string.Equals(profileName, "smoke", StringComparison.OrdinalIgnoreCase))
        {
            Skip.Test($"Set {ProfileVariable}=10k|100k|250k|500k|1m to execute a declared capacity profile.");
        }

        var profile = RetrievalCapacityProfile.Parse(profileName);
        var report = await RunAsync(profile).ConfigureAwait(false);
        Console.WriteLine(report.Summarize());

        var reportPath = Environment.GetEnvironmentVariable(ReportVariable);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            report.WriteJson(reportPath);
            Console.WriteLine($"capacityReport={Path.GetFullPath(reportPath)}");
        }

        AssertEx.Equal(expected: profile.ChunkCount, actual: report.Profile.ChunkCount);
        AssertEx.True(report.Query.RecallAtK >= 1d, report.Summarize());
        AssertEx.True(report.Query.NoAnswerFalsePositiveRate >= 1d,
            "The capacity lane records the current dense pipeline's lack of abstention as a false-positive rate. " + report.Summarize());
        if (string.Equals(Environment.GetEnvironmentVariable(GateVariable), "1", StringComparison.Ordinal))
        {
            AssertEx.True(report.Query.MeetsP95Target,
                $"Declared profile '{profile.Name}' exceeded its {report.Query.P95TargetMilliseconds:F0} ms end-to-end p95 target. {report.Summarize()}");
        }
    }

    private async Task<RetrievalCapacityReport> RunAsync(RetrievalCapacityProfile profile)
    {
        Directory.CreateDirectory(_rootPath);
        var target = ParseP95Target();
        return await RetrievalCapacityBenchmark.RunAsync(Path.Combine(_rootPath, $"capacity-{profile.Name}.sqlite"),
                _keyHolder,
                profile,
                target,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static double ParseP95Target()
    {
        var value = Environment.GetEnvironmentVariable(TargetVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return 500d;
        }

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var target) || target <= 0d)
        {
            throw new InvalidOperationException($"{TargetVariable} must be a positive invariant-culture number.");
        }

        return target;
    }
}
