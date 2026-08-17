namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Net;
using System.Net.Sockets;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.LlamaServer.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The supervisor's port reservation set: allocation walks the configured range, skips anything already reserved or
///     still bound by a foreign process, and fails NON-RETRYABLY when the range is exhausted. The reserved count is what
///     the loaded-model cap is measured against, so a release must make the slot countable again immediately.
/// </summary>
public sealed class LlamaServerPortAllocatorTests
{
    private const string NonRetryableMarker = "LlamaServer.NonRetryable";

    // Hands every test its own scan window so two tests running in parallel never probe (and then race for) the same
    // block of loopback ports.
    private static int _nextScanStart = 49_500;

    [Test]
    public void Allocate_ReservesDistinctPortsInRangeAndCountsThem()
    {
        var start = FindFreeRangeStart(count: 3);
        var allocator = new LlamaServerPortAllocator(Options(start, start + 2));

        var first = allocator.Allocate();
        var second = allocator.Allocate();
        var third = allocator.Allocate();

        AssertEx.Equal(3, allocator.ReservedCount);
        AssertEx.Equal(3, new HashSet<int>
        {
            first,
            second,
            third
        }.Count, "Every allocation must hand out a distinct port.");
        AssertEx.True(first >= start && third <= start + 2, "Allocation must stay inside the configured range.");
    }

    [Test]
    public void Release_MakesThePortAllocatableAgain()
    {
        var start = FindFreeRangeStart(count: 2);
        var allocator = new LlamaServerPortAllocator(Options(start, start + 1));

        var first = allocator.Allocate();
        allocator.Release(first);

        AssertEx.Equal(0, allocator.ReservedCount);
        AssertEx.Equal(first, allocator.Allocate(), "A released port is the lowest free candidate again.");
    }

    [Test]
    public void Release_WhenPortWasNeverReserved_IsANoOp()
    {
        var start = FindFreeRangeStart(count: 1);
        var allocator = new LlamaServerPortAllocator(Options(start, start));

        allocator.Release(start);
        allocator.Release(port: 1);

        AssertEx.Equal(0, allocator.ReservedCount);
        AssertEx.Equal(start, allocator.Allocate());
    }

    [Test]
    public void Allocate_SkipsAPortAnotherProcessStillHolds()
    {
        var start = FindFreeRangeStart(count: 2);
        using var squatter = Occupy(start);
        var allocator = new LlamaServerPortAllocator(Options(start, start + 1));

        AssertEx.Equal(start + 1, allocator.Allocate(), "A bound port must be skipped, not handed to the next spawn.");
        AssertEx.Equal(1, allocator.ReservedCount);
    }

    [Test]
    public void Allocate_WhenTheRangeIsExhausted_FailsNonRetryably()
    {
        var start = FindFreeRangeStart(count: 2);
        using var squatterA = Occupy(start);
        using var squatterB = Occupy(start + 1);
        var allocator = new LlamaServerPortAllocator(Options(start, start + 1));

        var exception = AssertEx.Throws<LlamaRuntimeException>(() => allocator.Allocate());

        AssertEx.True(exception.Data.Contains(NonRetryableMarker),
            "A range with no free port is a deterministic outcome; retrying the spawn cannot fix it.");
        AssertEx.Equal(0, allocator.ReservedCount);
    }

    private static LlamaServerSupervisorOptions Options(int portRangeStart, int portRangeEnd)
    {
        return new LlamaServerSupervisorOptions
        {
            PortRangeStart = portRangeStart,
            PortRangeEnd = portRangeEnd
        };
    }

    /// <summary>
    ///     Holds a loopback port the way a live llama-server does. It must LISTEN, not merely bind: .NET leaves
    ///     <c>SO_REUSEADDR</c> on by default on Unix, so a second bind to a bound-but-idle socket succeeds and the
    ///     allocator's probe would (correctly) report the port free.
    /// </summary>
    private static Socket Occupy(int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        socket.Listen(backlog: 1);
        return socket;
    }

    /// <summary>
    ///     Finds the start of a contiguous block of loopback ports nothing else on this box holds, so the allocator's own
    ///     bind probe is exercised against a range whose occupancy the test controls.
    /// </summary>
    private static int FindFreeRangeStart(int count)
    {
        for (var start = Interlocked.Add(ref _nextScanStart, 16) - 16; start <= 60_000 - count; start += count)
        {
            var probes = new List<Socket>(count);
            try
            {
                for (var port = start; port < start + count; port++)
                {
                    probes.Add(Occupy(port));
                }

                return start;
            }
            catch (SocketException)
            {
                continue;
            }
            finally
            {
                foreach (var probe in probes)
                {
                    probe.Dispose();
                }
            }
        }

        throw new InvalidOperationException($"No contiguous block of {count} free loopback ports was available for the test.");
    }
}
