namespace XE_Local_AI_Engine.Tests.ApiFoundation;

using System.Collections.Concurrent;
using System.Text.Json;
using XE_Local_AI_Engine.Client;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     FastEndpoints keeps one process-wide serializer options instance, and two hosts built at once both seed it: the
///     seeding must be safe when it runs concurrently on the same options.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class JsonSerializerOptionsSeedingConcurrencyTests
{
    private const int Threads = 8;
    private const int Rounds = 200;

    [Test]
    public void ConfigureJsonSerializerOptions_RunConcurrentlyOnOneOptions_AddsEachConverterOnceAndNeverThrows()
    {
        var options = Enumerable.Range(0, Rounds).Select(static _ => new JsonSerializerOptions()).ToArray();
        var failures = new ConcurrentQueue<Exception>();
        using var barrier = new Barrier(Threads);

        // Dedicated threads, not pool tasks: every participant blocks on the barrier each round, which would starve the pool.
        var workers = Enumerable.Range(0, Threads).Select(_ => new Thread(() =>
        {
            foreach (var round in options)
            {
                barrier.SignalAndWait();
                try
                {
                    ConfigureServices.ConfigureJsonSerializerOptions(round);
                }
                catch (Exception exception)
                {
                    // Every failure is the finding, and an exception escaping a raw thread would take the test host down instead.
                    failures.Enqueue(exception);
                }
            }
        })).ToArray();
        foreach (var worker in workers)
        {
            worker.Start();
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        AssertEx.Empty(failures, string.Join(Environment.NewLine, failures.Select(static failure => failure.Message).Distinct(StringComparer.Ordinal)));
        foreach (var seeded in options)
        {
            var converterTypes = seeded.Converters.Select(static converter => converter?.GetType().Name ?? "null").ToList();
            AssertEx.Equal(expected: 4, converterTypes.Count, string.Join(", ", converterTypes));
            AssertEx.Equal(converterTypes.Count, converterTypes.Distinct(StringComparer.Ordinal).Count(), string.Join(", ", converterTypes));
        }
    }
}
