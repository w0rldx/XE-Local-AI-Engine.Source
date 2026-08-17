namespace XE_Local_AI_Engine.Tests.Chat;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.AI.Agent.Configuration;
using XE_Local_AI_Engine.Client.Services.Chat;
using XE_Local_AI_Engine.Client.Services.Chat.Implementation;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.CodexOAuth;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the synthetic turn-context composition the send and regenerate paths share: the attachment-presence probe
///     that gates the cloud withhold notice, the inlined attachment text, the per-turn image budget, and the
///     best-effort knowledge grounding.
/// </summary>
public sealed class ChatTurnContextBuilderTests
{
    [Test]
    public async Task HasAttachmentContentAsync_WhenTheSendNamesFileIds_ShortCircuitsWithoutReadingTheStore()
    {
        var conversationId = Guid.NewGuid();
        var store = Substitute.For<IConversationUploadedFileStore>();
        var builder = CreateBuilder(store);

        var result = await builder.HasAttachmentContentAsync(conversationId, [Guid.NewGuid()]).ConfigureAwait(false);

        AssertEx.True(result);
        await store.DidNotReceive().ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task HasAttachmentContentAsync_WhenTheConversationHasOnlyPendingExtractions_ReturnsFalse()
    {
        var conversationId = Guid.NewGuid();
        var store = Substitute.For<IConversationUploadedFileStore>();
        store.ListAsync(conversationId, Arg.Any<CancellationToken>())
             .Returns([File(conversationId, "notes.pdf", "application/pdf", ".pdf", DocumentExtractionStatus.Pending)]);
        var builder = CreateBuilder(store);

        var result = await builder.HasAttachmentContentAsync(conversationId, requestedFileIds: null).ConfigureAwait(false);

        AssertEx.False(result);
    }

    [Test]
    public async Task BuildAttachmentContextAsync_InlinesTheExtractedMarkdownOfTheRequestedFiles()
    {
        var conversationId = Guid.NewGuid();
        var store = Substitute.For<IConversationUploadedFileStore>();
        var file = File(conversationId, "runbook.md", "text/markdown", ".md", DocumentExtractionStatus.Extracted);
        store.ListAsync(conversationId, Arg.Any<CancellationToken>()).Returns([file]);
        store.ReadExtractedMarkdownAsync(conversationId, file.FileId, Arg.Any<CancellationToken>()).Returns("restart the service");
        var builder = CreateBuilder(store);

        var message = await builder.BuildAttachmentContextAsync(conversationId, [file.FileId]).ConfigureAwait(false);

        AssertEx.NotNull(message);
        AssertEx.Equal(MessageRole.User, message!.Role);
        AssertEx.True(message.Content.Contains("runbook.md", StringComparison.Ordinal));
        AssertEx.True(message.Content.Contains("restart the service", StringComparison.Ordinal));
    }

    [Test]
    public async Task BuildAttachmentContextAsync_WhenTheSendNamesNoFiles_ReturnsNullWithoutReadingTheStore()
    {
        var store = Substitute.For<IConversationUploadedFileStore>();
        var builder = CreateBuilder(store);

        var message = await builder.BuildAttachmentContextAsync(Guid.NewGuid(), attachmentFileIds: null).ConfigureAwait(false);

        AssertEx.Null(message);
        await store.DidNotReceive().ListAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public async Task BuildImageContextAsync_WhenTheAggregateByteBudgetIsExceeded_KeepsTheFirstRequestedImages()
    {
        var conversationId = Guid.NewGuid();
        var store = Substitute.For<IConversationUploadedFileStore>();
        var first = File(conversationId, "a.png", "image/png", ".png", DocumentExtractionStatus.Image);
        var second = File(conversationId, "b.png", "image/png", ".png", DocumentExtractionStatus.Image);
        store.ListAsync(conversationId, Arg.Any<CancellationToken>()).Returns([first, second]);
        store.ReadBytesAsync(conversationId, first.FileId, Arg.Any<CancellationToken>()).Returns<ReadOnlyMemory<byte>?>(new byte[4]);
        store.ReadBytesAsync(conversationId, second.FileId, Arg.Any<CancellationToken>()).Returns<ReadOnlyMemory<byte>?>(new byte[4]);
        var builder = CreateBuilder(store,
            new LocalChatAgentOptions
            {
                MaxImageAttachmentBytes = 4
            });

        var message = await builder.BuildImageContextAsync(conversationId, [first.FileId, second.FileId]).ConfigureAwait(false);

        AssertEx.NotNull(message);
        AssertEx.Equal(expected: 1, message!.Images!.Count);
    }

    [Test]
    public async Task BuildKnowledgeContextAsync_WhenHitsAreReturned_ComposesTheGroundingAndItsSources()
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns(new KnowledgeSearchResult([Hit("Runbook", "restart the service with the eject command")]));
        var builder = CreateBuilder(scopeFactory: ScopeFactoryFor(searchService));

        var grounding = await builder.BuildKnowledgeContextAsync("how do I restart it?").ConfigureAwait(false);

        AssertEx.NotNull(grounding);
        AssertEx.True(grounding!.Message.Content.Contains("restart the service", StringComparison.Ordinal));
        AssertEx.Equal(expected: 1, grounding.Sources.Count);
    }

    [Test]
    public async Task BuildKnowledgeContextAsync_WhenRetrievalThrows_ReturnsNull()
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns<Task<KnowledgeSearchResult>>(_ => throw new InvalidOperationException("embedding provider is down"));
        var logger = new CapturingLogger<ChatTurnContextBuilder>();
        var builder = CreateBuilder(scopeFactory: ScopeFactoryFor(searchService), logger: logger);

        var grounding = await builder.BuildKnowledgeContextAsync("how do I restart it?").ConfigureAwait(false);

        AssertEx.Null(grounding);
        AssertEx.True(logger.AllText.Contains("failed for the plain-chat turn", StringComparison.Ordinal));
    }

    [Test]
    public async Task BuildKnowledgeContextAsync_WhenRetrievalThrowsForARerun_NamesTheRegeneratedTurnInTheWarning()
    {
        // The send and the rerun share this builder, so the warning has to keep naming which one degraded — a support
        // read of the log otherwise cannot tell a failed rerun from a failed send.
        var searchService = Substitute.For<IKnowledgeSearchService>();
        searchService.SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>())
                     .Returns<Task<KnowledgeSearchResult>>(_ => throw new InvalidOperationException("embedding provider is down"));
        var logger = new CapturingLogger<ChatTurnContextBuilder>();
        var builder = CreateBuilder(scopeFactory: ScopeFactoryFor(searchService), logger: logger);

        var grounding = await builder.BuildKnowledgeContextAsync("how do I restart it?", isRegeneratedTurn: true).ConfigureAwait(false);

        AssertEx.Null(grounding);
        AssertEx.True(logger.AllText.Contains("failed for the regenerated plain-chat turn", StringComparison.Ordinal));
    }

    [Test]
    public async Task BuildKnowledgeContextAsync_WhenTheQueryIsBlank_ReturnsNullWithoutSearching()
    {
        var searchService = Substitute.For<IKnowledgeSearchService>();
        var builder = CreateBuilder(scopeFactory: ScopeFactoryFor(searchService));

        var grounding = await builder.BuildKnowledgeContextAsync("   ").ConfigureAwait(false);

        AssertEx.Null(grounding);
        await searchService.DidNotReceive().SearchAsync(Arg.Any<KnowledgeSearchRequest>(), Arg.Any<CancellationToken>()).ConfigureAwait(false);
    }

    [Test]
    public void BuildAgentAttachmentHint_WhenNothingWasStaged_ReturnsNull()
    {
        var message = CreateBuilder().BuildAgentAttachmentHint(Guid.NewGuid(), []);

        AssertEx.Null(message);
    }

    [Test]
    public void BuildAgentAttachmentHint_NamesTheStagedPathsAsFencedData()
    {
        var message = CreateBuilder().BuildAgentAttachmentHint(Guid.NewGuid(), ["attachments/runbook.md"]);

        AssertEx.NotNull(message);
        AssertEx.Equal(MessageRole.User, message!.Role);
        AssertEx.True(message.Content.Contains("attachments/runbook.md", StringComparison.Ordinal));
    }

    private static ChatTurnContextBuilder CreateBuilder(IConversationUploadedFileStore? uploadedFileStore = null,
        LocalChatAgentOptions? options = null,
        IServiceScopeFactory? scopeFactory = null,
        ILogger<ChatTurnContextBuilder>? logger = null)
    {
        return new ChatTurnContextBuilder(uploadedFileStore ?? Substitute.For<IConversationUploadedFileStore>(),
            CreateFenceSeedProvider(),
            scopeFactory ?? Substitute.For<IServiceScopeFactory>(),
            Options.Create(options ?? new LocalChatAgentOptions()),
            logger ?? NullLogger<ChatTurnContextBuilder>.Instance);
    }

    private static IServiceScopeFactory ScopeFactoryFor(IKnowledgeSearchService searchService)
    {
        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IKnowledgeSearchService)).Returns(searchService);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        var factory = Substitute.For<IServiceScopeFactory>();
        factory.CreateScope().Returns(scope);
        return factory;
    }

    private static ConversationUploadedFileInfo File(Guid conversationId,
        string fileName,
        string mimeType,
        string extension,
        DocumentExtractionStatus status)
    {
        return new ConversationUploadedFileInfo(Guid.NewGuid(), conversationId, fileName, mimeType, extension, SizeBytes: 4, status, ExtractedChars: null, CreatedAtUtc: 0);
    }

    private static KnowledgeSearchHit Hit(string title, string content)
    {
        return new KnowledgeSearchHit(Guid.NewGuid(), Guid.NewGuid(), title, "Section", content, "knowledge-base", Score: 0.9, ChunkIndex: 0, KnowledgeDocumentStatus.Indexed,
            ServingLastKnownGood: false);
    }

    // The real seed derivation has its own coverage; this suite only needs a stable, non-empty seed so the fenced
    // markers are deterministic.
    private static IUntrustedContentFenceSeedProvider CreateFenceSeedProvider()
    {
        var provider = Substitute.For<IUntrustedContentFenceSeedProvider>();
        provider.DeriveSeed(Arg.Any<Guid>()).Returns("chat-turn-context-builder-tests-seed");
        return provider;
    }
}
