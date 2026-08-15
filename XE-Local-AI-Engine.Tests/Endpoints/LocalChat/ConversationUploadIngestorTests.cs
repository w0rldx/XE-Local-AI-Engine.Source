namespace XE_Local_AI_Engine.Tests.Endpoints.LocalChat;

using System.Text;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for the gate/extract/persist orchestration extracted out of the conversation upload endpoint: an
///     accepted image skips text extraction and is stored with the CANONICAL media type for its extension (never the
///     client-supplied Content-Type), a non-image keeps the extract-then-persist path and the client type (with the
///     octet-stream fallback), and a full admission gate rejects before any bytes are buffered.
/// </summary>
public sealed class ConversationUploadIngestorTests
{
    [Test]
    public void IsSupportedExtension_AcceptsImagesAndExtractorTypes_RejectsAnythingElse()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.IsSupported(".txt").Returns(true);
        using var gate = new DocumentExtractionAdmissionGate();
        var ingestor = new ConversationUploadIngestor(Substitute.For<IConversationUploadedFileStore>(), extractor, gate);

        // Images are admitted by the ingestor's own allowlist, case-insensitively, without consulting the extractor.
        AssertEx.True(ingestor.IsSupportedExtension(".PNG"));
        AssertEx.True(ingestor.IsSupportedExtension(".txt"));
        AssertEx.False(ingestor.IsSupportedExtension(".exe"));
    }

    [Test]
    public async Task IngestAsync_WhenImage_SkipsExtractionAndStoresTheCanonicalMediaType()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var store = Substitute.For<IConversationUploadedFileStore>();
        ConversationUploadedFileInput? captured = null;
        store.AddAsync(Arg.Do<ConversationUploadedFileInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(callInfo => Task.FromResult(ToInfo(callInfo.Arg<ConversationUploadedFileInput>())));
        using var gate = new DocumentExtractionAdmissionGate();
        var ingestor = new ConversationUploadIngestor(store, extractor, gate);

        var conversationId = Guid.NewGuid();
        using var content = new MemoryStream([0x89, 0x50, 0x4E, 0x47]);
        var info = await ingestor.IngestAsync(conversationId, content, "photo.jpg", ".jpg", "text/plain", CancellationToken.None)
                                 .ConfigureAwait(false);

        AssertEx.NotNull(info);
        var input = AssertEx.NotNull(captured);
        AssertEx.Equal(conversationId, input.ConversationId);
        AssertEx.Equal(DocumentExtractionStatus.Image, input.ExtractionStatus);
        // The spoofable client Content-Type never reaches storage for an image: the extension picks the media type.
        AssertEx.Equal("image/jpeg", input.MimeType);
        AssertEx.Null(input.ExtractedMarkdown);
        AssertEx.Null(input.ExtractedChars);
        AssertEx.Equal(expected: 4L, input.SizeBytes);
        await extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task IngestAsync_WhenNonImage_ExtractsAndKeepsTheClientContentType()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DocumentExtractionResult(DocumentExtractionStatus.Extracted, "# hi", ExtractedChars: 4, Error: null)));
        var store = Substitute.For<IConversationUploadedFileStore>();
        ConversationUploadedFileInput? captured = null;
        store.AddAsync(Arg.Do<ConversationUploadedFileInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(callInfo => Task.FromResult(ToInfo(callInfo.Arg<ConversationUploadedFileInput>())));
        using var gate = new DocumentExtractionAdmissionGate();
        var ingestor = new ConversationUploadIngestor(store, extractor, gate);

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        _ = await ingestor.IngestAsync(Guid.NewGuid(), content, "notes.txt", ".txt", "text/markdown", CancellationToken.None)
                          .ConfigureAwait(false);

        var input = AssertEx.NotNull(captured);
        AssertEx.Equal(DocumentExtractionStatus.Extracted, input.ExtractionStatus);
        AssertEx.Equal("# hi", input.ExtractedMarkdown);
        AssertEx.Equal("text/markdown", input.MimeType);
    }

    [Test]
    public async Task IngestAsync_WhenClientContentTypeIsBlank_FallsBackToOctetStream()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.ExtractAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                 .Returns(Task.FromResult(new DocumentExtractionResult(DocumentExtractionStatus.Extracted, "x", ExtractedChars: 1, Error: null)));
        var store = Substitute.For<IConversationUploadedFileStore>();
        ConversationUploadedFileInput? captured = null;
        store.AddAsync(Arg.Do<ConversationUploadedFileInput>(input => captured = input), Arg.Any<CancellationToken>())
             .Returns(callInfo => Task.FromResult(ToInfo(callInfo.Arg<ConversationUploadedFileInput>())));
        using var gate = new DocumentExtractionAdmissionGate();
        var ingestor = new ConversationUploadIngestor(store, extractor, gate);

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        _ = await ingestor.IngestAsync(Guid.NewGuid(), content, "notes.txt", ".txt", "   ", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal("application/octet-stream", AssertEx.NotNull(captured).MimeType);
    }

    [Test]
    public async Task IngestAsync_WhenAdmissionGateIsFull_ReturnsNullWithoutTouchingTheStore()
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        var store = Substitute.For<IConversationUploadedFileStore>();
        using var gate = new DocumentExtractionAdmissionGate(maxConcurrentExtractions: 1);

        // Hold the only slot so the ingest attempt below finds the gate at capacity.
        AssertEx.True(gate.TryAcquire(out var heldLease));
        using (heldLease)
        {
            var ingestor = new ConversationUploadIngestor(store, extractor, gate);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));

            var info = await ingestor.IngestAsync(Guid.NewGuid(), content, "notes.txt", ".txt", "text/plain", CancellationToken.None)
                                     .ConfigureAwait(false);

            // Null is the busy signal the endpoint turns into 503 + Retry-After; nothing was buffered or persisted.
            AssertEx.Null(info);
        }

        await store.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default!, default!, default);
    }

    private static ConversationUploadedFileInfo ToInfo(ConversationUploadedFileInput input)
    {
        return new ConversationUploadedFileInfo(input.FileId,
            input.ConversationId,
            input.OriginalFileName,
            input.MimeType,
            input.Extension,
            input.SizeBytes,
            input.ExtractionStatus,
            input.ExtractedChars,
            CreatedAtUtc: 0);
    }
}
