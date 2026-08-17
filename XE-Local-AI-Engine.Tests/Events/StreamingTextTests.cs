namespace XE_Local_AI_Engine.Tests.Events;

using System.Collections.Concurrent;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit coverage for the immutable append-only accumulator backing streamed content/reasoning. The
///     invariants here are what let a per-chunk snapshot clone copy the accumulator by reference without materializing the
///     whole string.
/// </summary>
public sealed class StreamingTextTests
{
    [Test]
    public void Empty_IsEmptyStringWithZeroLength()
    {
        AssertEx.Equal(string.Empty, StreamingText.Empty.Value);
        AssertEx.Equal(expected: 0, StreamingText.Empty.Length);
    }

    [Test]
    public void FromString_RoundTripsValueAndLength()
    {
        var text = StreamingText.FromString("hello world");

        AssertEx.Equal("hello world", text.Value);
        AssertEx.Equal(expected: 11, text.Length);
    }

    [Test]
    public void FromString_NullOrEmpty_IsEmptyInstance()
    {
        AssertEx.True(ReferenceEquals(StreamingText.Empty, StreamingText.FromString(string.Empty)));
    }

    [Test]
    public void Append_ConcatenatesInOrderAndTracksLength()
    {
        var text = StreamingText.Empty
                                .Append("Hello")
                                .Append(", ")
                                .Append("world");

        AssertEx.Equal("Hello, world", text.Value);
        AssertEx.Equal(expected: 12, text.Length);
    }

    [Test]
    public void Append_EmptyChunk_ReturnsSameInstance()
    {
        var text = StreamingText.FromString("abc");

        AssertEx.True(ReferenceEquals(text, text.Append(string.Empty)));
    }

    [Test]
    public void Append_DoesNotMutatePriorSnapshot()
    {
        // A reference captured before an append must stay stable — this is the property that makes cloning the
        // accumulator by reference a safe point-in-time snapshot.
        var first = StreamingText.Empty.Append("one");
        var second = first.Append(" two");

        AssertEx.Equal("one", first.Value);
        AssertEx.Equal("one two", second.Value);
    }

    [Test]
    public void Value_IsStableAcrossRepeatedReads()
    {
        var text = StreamingText.Empty.Append("a").Append("b").Append("c");

        var firstRead = text.Value;
        var secondRead = text.Value;

        AssertEx.Equal("abc", firstRead);
        AssertEx.Equal("abc", secondRead);
        // The value is cached, so repeated reads return the identical string instance.
        AssertEx.True(ReferenceEquals(firstRead, secondRead));
    }

    [Test]
    public void Build_ColdChain_MaterializesCorrectlyWithoutStackOverflow()
    {
        // A long chain with no already-materialized ancestor must build iteratively (not recurse) and produce the exact
        // concatenation. 50k nodes would overflow a naive recursive walk.
        const int count = 50_000;
        var text = StreamingText.Empty;
        var expected = new StringBuilder(count);
        for (var i = 0; i < count; i++)
        {
            text = text.Append("x");
            expected.Append('x');
        }

        AssertEx.Equal(count, text.Length);
        AssertEx.Equal(expected.ToString(), text.Value);
    }

    [Test]
    public void Build_ReusesCachedAncestorPrefix()
    {
        // Read an intermediate snapshot (caching its value), then keep appending and read the newer snapshot: the newer
        // value must still be the full, correct concatenation (the cached-prefix optimization must not drop the tail).
        var prefix = StreamingText.Empty.Append("aaa").Append("bbb");
        _ = prefix.Value; // caches "aaabbb" on this node

        var extended = prefix.Append("ccc").Append("ddd");

        AssertEx.Equal("aaabbbcccddd", extended.Value);
        AssertEx.Equal("aaabbb", prefix.Value);
    }

    [Test]
    public async Task Value_WhenAppendsAndReadsInterleaveAcrossThreads_EverySnapshotMaterializesItsOwnPrefix()
    {
        // Materializing a node COLLAPSES its chain (drops the link to its ancestors) so the intermediate nodes — and the
        // full strings cached on them — can be collected instead of being pinned for the whole turn. That collapse can
        // land while another thread's Build is walking through the very node being collapsed, which is the hazard this
        // exercises: readers materialize snapshots from several threads while the producer keeps appending and keeps
        // materializing some of the nodes itself (the pump's debounced flush pattern, which is what leaves uncollapsed
        // runs between collapsed nodes). Any snapshot whose value is not its own exact prefix is a truncated walk.
        const int count = 4_000;
        var expected = string.Concat(Enumerable.Range(start: 0, count).Select(index => (char)('a' + (index % 26))));

        var snapshots = new ConcurrentQueue<StreamingText>();
        var failures = new ConcurrentQueue<string>();

        var producer = Task.Run(() =>
        {
            var text = StreamingText.Empty;
            for (var index = 0; index < count; index++)
            {
                text = text.Append(expected[index].ToString());
                snapshots.Enqueue(text);

                if (index % 7 == 0)
                {
                    _ = text.Value;
                }
            }

            return text;
        });

        var readers = Enumerable.Range(start: 0, count: 3)
                                .Select(_ => Task.Run(async () =>
                                {
                                    while (!producer.IsCompleted || !snapshots.IsEmpty)
                                    {
                                        if (!snapshots.TryDequeue(out var snapshot))
                                        {
                                            await Task.Yield();
                                            continue;
                                        }

                                        var value = snapshot.Value;
                                        if (!string.Equals(value, expected[..snapshot.Length], StringComparison.Ordinal))
                                        {
                                            failures.Enqueue($"a snapshot of length {snapshot.Length} materialized \"{value}\"");
                                        }
                                    }
                                }))
                                .ToArray();

        var tail = await producer;
        await Task.WhenAll(readers);

        AssertEx.Empty(failures, failures.TryPeek(out var firstFailure) ? firstFailure : null);
        AssertEx.Equal(expected, tail.Value);
    }
}
