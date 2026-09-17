namespace XE_Local_AI_Engine.Tests.Transcription;

using XE_Local_AI_Engine.Client.Services.Transcription;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Drives the real <see cref="TranscriptionUploadSlot" /> through the real
///     <c>ITranscriptionService.BeginUploadAsync</c>. Nothing here is faked: the object under test <i>is</i> the
///     cleanup guarantee, so a stand-in would assert nothing at all.
/// </summary>
/// <remarks>
///     The first three cases are the three ways an upload dies before a transcription ever starts — a stream that
///     breaks, a body over the cap, and a client that goes away. Each one left a plaintext audio file on disk under the
///     design where the delete lived in the transcription's own cleanup, because that cleanup only exists after the
///     copy has already succeeded.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class TranscriptionUploadSlotTests
{
    private const long Cap = 16 * 1024;

    // Above TranscriptionUploadSlot's 64 KiB copy buffer, so at least one full chunk is written before the cap trips.
    private const long OverBufferCap = 128 * 1024;

    [Test]
    public async Task Slot_WhenCopyFailsMidStream_DeletesThePartialFile()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        var slot = await harness.Service.BeginUploadAsync(session.Id, ".wav", CancellationToken.None);
        await using (slot)
        {
            using var source = new FailingStream(bytesBeforeFailure: 8 * 1024);
            _ = await AssertEx.ThrowsAsync<IOException>(() => slot.CopyFromAsync(source, Cap, CancellationToken.None),
                "A broken upload stream must surface, not be swallowed.");
            AssertEx.True(File.Exists(slot.SourcePath), "The partial file exists until the slot is disposed.");
        }

        AssertEx.Empty(harness.TempFiles, "The partial upload must be gone.");
    }

    [Test]
    public async Task Slot_WhenUploadOverrunsTheCap_DeletesThePartialFile()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        // The cap must sit ABOVE the copy buffer, or the very first read trips it and nothing is ever written — the
        // test would then pass without a partial file existing, which is the thing it exists to prove gets deleted.
        var slot = await harness.Service.BeginUploadAsync(session.Id, ".wav", CancellationToken.None);
        await using (slot)
        {
            using var source = new MemoryStream(new byte[OverBufferCap * 4]);
            _ = await AssertEx.ThrowsAsync<TranscriptionUploadTooLargeException>(() => slot.CopyFromAsync(source, OverBufferCap, CancellationToken.None),
                "An over-cap body must be refused while it is being read, not after it has all landed.");

            AssertEx.True(File.Exists(slot.SourcePath), "Bytes reached the disk before the cap tripped.");
            AssertEx.True(new FileInfo(slot.SourcePath).Length > 0, "The partial upload is real, not an empty placeholder.");
        }

        AssertEx.Empty(harness.TempFiles, "The rejected upload must be gone.");
        AssertEx.Equal(0, harness.Transcriber.CallCount, "An over-cap upload must never reach the runtime.");
    }

    [Test]
    public async Task Slot_WhenCancelledDuringCopy_DeletesThePartialFile()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        var slot = await harness.Service.BeginUploadAsync(session.Id, ".wav", CancellationToken.None);
        await using (slot)
        {
            // The source cancels the caller's token part-way through, which is what a client disconnect looks like.
            using var source = new CancellingStream(cancellation, bytesBeforeCancel: 8 * 1024);
            _ = await AssertEx.ThrowsAsync<OperationCanceledException>(() => slot.CopyFromAsync(source, Cap * 64, cancellation.Token),
                "Cancellation must still propagate; the slot swallows nothing on this path.");
        }

        AssertEx.Empty(harness.TempFiles, "The abandoned upload must be gone.");
    }

    [Test]
    public async Task Slot_WhenTranscodeAdded_DisposeDeletesBothPaths()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        string sourcePath;
        string destinationPath;
        var slot = await harness.Service.BeginUploadAsync(session.Id, ".ogg", CancellationToken.None);
        await using (slot)
        {
            sourcePath = slot.SourcePath;
            destinationPath = slot.AddOwnedPath(".wav");
            AssertEx.NotEqual(sourcePath, destinationPath);

            await File.WriteAllBytesAsync(sourcePath, TranscriptionAudioFixtures.Ogg);
            await File.WriteAllBytesAsync(destinationPath, TranscriptionAudioFixtures.Wav);
            AssertEx.Equal(2, harness.TempFiles.Count);
        }

        AssertEx.False(File.Exists(sourcePath), "The upload must be deleted.");
        AssertEx.False(File.Exists(destinationPath), "The conversion output must be deleted by the same disposal.");
        AssertEx.Empty(harness.TempFiles);
    }

    [Test]
    [Arguments("./../x", "a relative traversal dressed up as an extension")]
    [Arguments(".wav/../../etc", "a separator smuggled in after a plausible extension")]
    [Arguments(".", "a bare dot, which names a directory and not an extension")]
    [Arguments(".verylongextensionname", "longer than any real extension")]
    [Arguments(".w a v", "spaces, which no extension has")]
    public async Task Slot_WhenTheExtensionIsNotAnExtension_KeepsThePathInsideTheTempDirectory(string extension, string because)
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        var slot = await harness.Service.BeginUploadAsync(session.Id, extension, CancellationToken.None);
        await using (slot)
        {
            var directory = Path.GetFullPath(harness.TempDirectory);
            var actual = Path.GetFullPath(slot.SourcePath);

            AssertEx.Equal(directory,
                Path.GetDirectoryName(actual),
                $"The upload file must stay in the engine's temporary directory for {because}.");
            AssertEx.Equal(string.Empty,
                Path.GetExtension(actual),
                $"An extension that is not an extension is dropped, not repaired, for {because}.");

            await File.WriteAllBytesAsync(slot.SourcePath, TranscriptionAudioFixtures.Wav);
            AssertEx.Equal(1, harness.TempFiles.Count);
        }

        AssertEx.Empty(harness.TempFiles);
    }

    [Test]
    public async Task Slot_WhenTheExtensionIsOrdinary_KeepsIt()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        // The negative control for the case above: a real extension must survive, or the rule would be "drop always"
        // and the table would pass for the wrong reason.
        var slot = await harness.Service.BeginUploadAsync(session.Id, ".flac", CancellationToken.None);
        await using (slot)
        {
            AssertEx.Equal(".flac", Path.GetExtension(slot.SourcePath));
        }
    }

    [Test]
    public async Task Slot_DisposeIsIdempotent()
    {
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        var slot = await harness.Service.BeginUploadAsync(session.Id, ".wav", CancellationToken.None);
        await File.WriteAllBytesAsync(slot.SourcePath, TranscriptionAudioFixtures.Wav);

        await slot.DisposeAsync();
        AssertEx.Empty(harness.TempFiles);

        // The endpoint's `await using` disposes a slot the handler may already have disposed; the second call is a
        // no-op rather than a second delete of a path something else may since have taken.
        await slot.DisposeAsync();
        AssertEx.Empty(harness.TempFiles);
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task Slot_WhenAFileIsLocked_DisposeDoesNotThrow()
    {
        // Only Windows refuses to unlink an open file; on Unix the delete succeeds and this case cannot exist.
        await using var harness = await TranscriptionServiceHarness.CreateAsync();
        var session = await harness.Service.CreateSessionAsync(new CreateTranscriptionSessionInput(), CancellationToken.None);

        var slot = await harness.Service.BeginUploadAsync(session.Id, ".wav", CancellationToken.None);
        await File.WriteAllBytesAsync(slot.SourcePath, TranscriptionAudioFixtures.Wav);

        using (new FileStream(slot.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // A temporary file that cannot be removed must never turn a finished transcription into a failed request.
            await slot.DisposeAsync();
        }

        AssertEx.True(File.Exists(slot.SourcePath), "The locked file survives, which is exactly why the failure is logged and not thrown.");
    }

    /// <summary>A body that breaks part-way through, the way a dropped connection does.</summary>
    private sealed class FailingStream(int bytesBeforeFailure) : Stream
    {
        private int _remaining = bytesBeforeFailure;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                throw new IOException("The upload stream broke.");
            }

            var count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Clear();
            _remaining -= count;
            return ValueTask.FromResult(count);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>A body whose reader cancels the caller's token part-way through, the way a client disconnect does.</summary>
    private sealed class CancellingStream(CancellationTokenSource cancellation, int bytesBeforeCancel) : Stream
    {
        private int _remaining = bytesBeforeCancel;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_remaining <= 0)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return 0;
            }

            var count = Math.Min(buffer.Length, _remaining);
            buffer.Span[..count].Clear();
            _remaining -= count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
