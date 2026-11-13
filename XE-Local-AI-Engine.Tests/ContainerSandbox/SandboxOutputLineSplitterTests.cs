namespace XE_Local_AI_Engine.Tests.ContainerSandbox;

using System.Buffers.Binary;
using System.Text;
using Docker.DotNet;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Tests.Sandbox;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The Docker exec path's chunk-to-line splitter: a daemon chunk boundary falls anywhere, inside a UTF-8 sequence or between the
///     two bytes of <c>\r\n</c>, and the observer must still see the lines the process pump would have produced.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class SandboxOutputLineSplitterTests
{
    private const byte StandardOutputFrame = 1;
    private const byte StandardErrorFrame = 2;

    [Test]
    public void Write_MultiByteCharactersCutAcrossChunks_DecodeWhole()
    {
        var observer = new RecordingSandboxOutputObserver();
        var splitter = new SandboxOutputLineSplitter(observer, SandboxOutputSource.StandardOutput);
        var bytes = Encoding.UTF8.GetBytes("café €\n");

        // One byte per chunk cuts the two-byte and the three-byte sequence at every possible point.
        foreach (var value in bytes)
        {
            splitter.Write([value]);
        }

        splitter.Complete();

        AssertEx.Equal("café €", string.Join('|', observer.StandardOutput));
    }

    [Test]
    public void Write_SplitsOnLfCrLfAndLoneCr_EvenWhenCrLfIsCutAcrossChunks()
    {
        var observer = new RecordingSandboxOutputObserver();
        var splitter = new SandboxOutputLineSplitter(observer, SandboxOutputSource.StandardOutput);

        splitter.Write("a\nb\r"u8);
        splitter.Write("\nc\rd\r\re"u8);
        splitter.Complete();

        AssertEx.Equal("a|b|c|d||e", string.Join('|', observer.StandardOutput));
    }

    [Test]
    public void Complete_FlushesAnUnterminatedLastLine_AndATruncatedSequenceAsAReplacementCharacter()
    {
        var observer = new RecordingSandboxOutputObserver();
        var splitter = new SandboxOutputLineSplitter(observer, SandboxOutputSource.StandardError);

        splitter.Write("done\npartial "u8);
        splitter.Write([0xE2, 0x82]);
        splitter.Complete();

        AssertEx.Equal("done|partial �", string.Join('|', observer.StandardError));
    }

    [Test]
    public void Write_ALineLongerThanTheCap_ReachesTheObserverAsItsPrefix()
    {
        var observer = new RecordingSandboxOutputObserver();
        var splitter = new SandboxOutputLineSplitter(observer, SandboxOutputSource.StandardOutput);

        splitter.Write(Encoding.ASCII.GetBytes(new string('x', SandboxOutputLineSplitter.MaxLineChars + 100) + "\nnext\n"));
        splitter.Complete();

        AssertEx.Equal(expected: 2, observer.StandardOutput.Count);
        AssertEx.Equal(SandboxOutputLineSplitter.MaxLineChars, observer.StandardOutput[0].Length);
        AssertEx.Equal("next", observer.StandardOutput[1]);
    }

    [Test]
    public async Task ReadBounded_WithObserver_FeedsEachStreamItsOwnLines_AndKeepsTheCapturedText()
    {
        var euro = Encoding.UTF8.GetBytes("€");
        using var framed = new MemoryStream([
            .. Frame(StandardOutputFrame, [.. "price "u8, euro[0]]),
            .. Frame(StandardErrorFrame, "warn\r"u8.ToArray()),
            .. Frame(StandardOutputFrame, [euro[1], euro[2], .. "5\ntail"u8]),
            .. Frame(StandardErrorFrame, "\nlast"u8.ToArray())
        ]);
        using var stream = new MultiplexedStream(framed, multiplexed: true);
        var observer = new RecordingSandboxOutputObserver();

        var captured = await DockerDotNetRuntimeClient.ReadBoundedAsync(stream, maxBytesPerStream: 4096, observer, CancellationToken.None);

        AssertEx.Equal("price €5|tail", string.Join('|', observer.StandardOutput));
        AssertEx.Equal("warn|last", string.Join('|', observer.StandardError));
        AssertEx.Equal("price €5\ntail", captured.StandardOutput);
        AssertEx.Equal("warn\r\nlast", captured.StandardError);
    }

    /// <summary>One chunk in Docker's exec framing: target stream byte, three zero bytes, big-endian length, payload.</summary>
    private static byte[] Frame(byte targetStream, byte[] payload)
    {
        var frame = new byte[8 + payload.Length];
        frame[0] = targetStream;
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(start: 4), payload.Length);
        payload.CopyTo(frame, 8);
        return frame;
    }
}
