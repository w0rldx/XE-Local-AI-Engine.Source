namespace XE_Local_AI_Engine.Tests.Knowledge;

using Microsoft.Extensions.Options;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.DocumentIngestion;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Workspace.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

public sealed class KnowledgeRepositoryImportServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task ImportAsync_UsesGitIgnoreAndSensitiveExclusions_AndPreservesRelativeProvenance()
    {
        Directory.CreateDirectory(_root);
        var git = new HostGitRunner(timeoutSeconds: 30);
        var initialized = await git.RunAsync(_root, AgentHomeGit.Arguments("init"), CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, initialized.ExitCode, initialized.StandardError);

        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(_root, ".gitignore"), "ignored.md\n").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "src", "Widget.cs"), "public sealed class Widget { }").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# Widget repository").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "ignored.md"), "ignored content").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, ".env"), "SECRET=value").ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "node_modules", "package.js"), "generated").ConfigureAwait(false);

        var selectedFolderId = Guid.NewGuid();
        var repositories = Substitute.For<IDevelopmentRepositoryBindingService>();
        repositories.ResolveFolderAsync(selectedFolderId, Arg.Any<CancellationToken>())
                    .Returns(new DevelopmentRepositoryBinding(Guid.Empty, selectedFolderId, "widget", _root, "identity"));
        var inputs = new List<KnowledgeDocumentInput>();
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                 .Returns(call =>
                 {
                     var input = call.ArgAt<KnowledgeDocumentInput>(0);
                     inputs.Add(input);
                     return new KnowledgeDocumentAddResult(input.DocumentId, WasInserted: true);
                 });
        var dispatcher = new AcceptingDispatcher();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
               .Returns(KnowledgeDocumentStatus.Pending);
        catalog.ListAsync("PROJECT-WIDGET", "repository", selectedFolderId.ToString("N"), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<KnowledgeDocumentSummary>());
        var purge = Substitute.For<IKnowledgeDocumentPurgeService>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.IsSupported(Arg.Any<string>()).Returns(call => call.ArgAt<string>(0) is ".cs" or ".md");
        var service = new KnowledgeRepositoryImportService(repositories,
            new SensitiveFileExclusionService(),
            blobStore,
            new KnowledgeIngestionAdmissionService(catalog, dispatcher),
            catalog,
            purge,
            extractor,
            Options.Create(new KnowledgeBaseOptions()));

        var result = await service.ImportAsync(selectedFolderId, "project-widget", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal("PROJECT-WIDGET", result.CollectionId);
        AssertEx.Equal(expected: 2, result.AddedDocuments);
        AssertEx.Equal(expected: 2, result.EnqueuedDocuments);
        AssertEx.True(inputs.Select(static input => input.SourcePath).ToHashSet(StringComparer.Ordinal)
                            .SetEquals(["README.md", "src/Widget.cs"]));
        AssertEx.True(inputs.All(input => input.CollectionId == "PROJECT-WIDGET"
                                          && input.SourceKind == "repository"
                                          && input.SourceId == selectedFolderId.ToString("N")));
        AssertEx.False(inputs.Any(static input => input.SourcePath?.Contains("ignored", StringComparison.Ordinal) == true));
        AssertEx.False(inputs.Any(static input => input.SourcePath?.Contains("node_modules", StringComparison.Ordinal) == true));
        AssertEx.False(inputs.Any(static input => input.SourcePath?.Contains(".env", StringComparison.Ordinal) == true));
    }

    [Test]
    public async Task ImportAsync_WhenUnchangedDocumentIsAlreadyIndexed_DoesNotQueueRedundantIngestion()
    {
        Directory.CreateDirectory(_root);
        var git = new HostGitRunner(timeoutSeconds: 30);
        var initialized = await git.RunAsync(_root, AgentHomeGit.Arguments("init"), CancellationToken.None).ConfigureAwait(false);
        AssertEx.Equal(expected: 0, initialized.ExitCode, initialized.StandardError);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "# Existing repository").ConfigureAwait(false);

        var documentId = Guid.NewGuid();
        var selectedFolderId = Guid.NewGuid();
        var repositories = Substitute.For<IDevelopmentRepositoryBindingService>();
        repositories.ResolveFolderAsync(selectedFolderId, Arg.Any<CancellationToken>())
                    .Returns(new DevelopmentRepositoryBinding(Guid.Empty, selectedFolderId, "existing", _root, "identity"));
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                 .Returns(new KnowledgeDocumentAddResult(documentId, WasInserted: false));
        var dispatcher = Substitute.For<IKnowledgeIngestionDispatcher>();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(documentId, Arg.Any<CancellationToken>()).Returns(KnowledgeDocumentStatus.Indexed);
        catalog.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<KnowledgeDocumentSummary>());
        var purge = Substitute.For<IKnowledgeDocumentPurgeService>();
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.IsSupported(".md").Returns(true);
        var service = new KnowledgeRepositoryImportService(repositories,
            new SensitiveFileExclusionService(),
            blobStore,
            new KnowledgeIngestionAdmissionService(catalog, dispatcher),
            catalog,
            purge,
            extractor,
            Options.Create(new KnowledgeBaseOptions()));

        var result = await service.ImportAsync(selectedFolderId, collectionId: null, CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.DeduplicatedDocuments);
        AssertEx.Equal(expected: 0, result.EnqueuedDocuments);
        await dispatcher.DidNotReceive().EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImportAsync_WhenRepositoryPathChanged_RequeuesExistingDocumentWithoutCountingItAsDuplicate()
    {
        await InitializeRepositoryAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "changed repository content").ConfigureAwait(false);

        var existingId = Guid.NewGuid();
        var selectedFolderId = Guid.NewGuid();
        var repositories = RepositoryBinding(selectedFolderId);
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                 .Returns(new KnowledgeDocumentAddResult(existingId, WasInserted: false, WasUpdated: true));
        var dispatcher = new AcceptingDispatcher();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        // Indexed, not Pending: a changed repository file is the one case where an already-indexed document MUST be
        // re-enqueued. It pins that the admission rule keys on "the store wrote it", not only on a retryable status.
        catalog.GetStatusAsync(existingId, Arg.Any<CancellationToken>()).Returns(KnowledgeDocumentStatus.Indexed);
        catalog.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<KnowledgeDocumentSummary>());
        var service = CreateService(repositories, blobStore, dispatcher, catalog, Substitute.For<IKnowledgeDocumentPurgeService>());

        var result = await service.ImportAsync(selectedFolderId, "repo", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.UpdatedDocuments);
        AssertEx.Equal(expected: 0, result.DeduplicatedDocuments);
        AssertEx.Equal(expected: 1, result.EnqueuedDocuments);
    }

    [Test]
    public async Task ImportAsync_AfterCompleteScan_PurgesRepositoryDocumentsWhosePathsDisappeared()
    {
        await InitializeRepositoryAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "current repository content").ConfigureAwait(false);

        var removedId = Guid.NewGuid();
        var selectedFolderId = Guid.NewGuid();
        var repositories = RepositoryBinding(selectedFolderId);
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                 .Returns(call => new KnowledgeDocumentAddResult(call.Arg<KnowledgeDocumentInput>().DocumentId, WasInserted: true));
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(KnowledgeDocumentStatus.Pending);
        catalog.ListAsync("REPO", "repository", selectedFolderId.ToString("N"), Arg.Any<CancellationToken>())
               .Returns(new[]
               {
                   Summary(Guid.NewGuid(), "README.md"),
                   Summary(removedId, "src/Removed.cs"),
                   Summary(Guid.NewGuid(), "manual.txt", sourceKind: "upload")
               });
        var purge = Substitute.For<IKnowledgeDocumentPurgeService>();
        purge.PurgeAsync(removedId, Arg.Any<CancellationToken>()).Returns(true);
        var service = CreateService(repositories, blobStore, new AcceptingDispatcher(), catalog, purge);

        var result = await service.ImportAsync(selectedFolderId, "repo", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 1, result.RemovedDocuments);
        await catalog.Received(1).ListAsync("REPO",
            "repository",
            selectedFolderId.ToString("N"),
            Arg.Any<CancellationToken>());
        await purge.Received(1).PurgeAsync(removedId, Arg.Any<CancellationToken>());
        await purge.DidNotReceive().PurgeAsync(Arg.Is<Guid>(id => id != removedId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImportAsync_AfterCompleteScan_DoesNotReconcileAnotherRepositoryInTheSameCollection()
    {
        await InitializeRepositoryAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "current repository content").ConfigureAwait(false);

        var selectedFolderId = Guid.NewGuid();
        var otherFolderId = Guid.NewGuid();
        var otherDocumentId = Guid.NewGuid();
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                 .Returns(call => new KnowledgeDocumentAddResult(call.Arg<KnowledgeDocumentInput>().DocumentId, WasInserted: true));
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(KnowledgeDocumentStatus.Pending);
        catalog.ListAsync("SHARED", "repository", selectedFolderId.ToString("N"), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<KnowledgeDocumentSummary>());
        catalog.ListAsync("SHARED", "repository", otherFolderId.ToString("N"), Arg.Any<CancellationToken>())
               .Returns([Summary(otherDocumentId, "src/OnlyInOtherRepository.cs")]);
        catalog.ClearReceivedCalls();
        var purge = Substitute.For<IKnowledgeDocumentPurgeService>();
        var service = CreateService(RepositoryBinding(selectedFolderId), blobStore, new AcceptingDispatcher(), catalog, purge);

        var result = await service.ImportAsync(selectedFolderId, "shared", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, result.RemovedDocuments);
        await catalog.Received(1).ListAsync("SHARED",
            "repository",
            selectedFolderId.ToString("N"),
            Arg.Any<CancellationToken>());
        await catalog.DidNotReceive().ListAsync("SHARED",
            "repository",
            otherFolderId.ToString("N"),
            Arg.Any<CancellationToken>());
        await purge.DidNotReceive().PurgeAsync(otherDocumentId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImportAsync_WhenFileExceedsPerFileLimit_RejectsBeforeBlobStoreAllocation()
    {
        await InitializeRepositoryAsync().ConfigureAwait(false);
        await File.WriteAllBytesAsync(Path.Combine(_root, "large.md"), new byte[33]).ConfigureAwait(false);

        var selectedFolderId = Guid.NewGuid();
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        var service = CreateService(RepositoryBinding(selectedFolderId),
            blobStore,
            new AcceptingDispatcher(),
            catalog,
            Substitute.For<IKnowledgeDocumentPurgeService>(),
            new KnowledgeBaseOptions
            {
                MaxRepositoryImportFileBytes = 32
            });

        // A configured bound is the caller's problem, so it carries the rejected type the endpoint maps to 400 —
        // not the bare InvalidOperationException that used to make every I/O failure in here a 400 too.
        var exception = await AssertEx.ThrowsAsync<KnowledgeRepositoryImportRejectedException>(() =>
            service.ImportAsync(selectedFolderId, "repo", CancellationToken.None)).ConfigureAwait(false);

        AssertEx.Contains(exception.Message, "per-file");
        await blobStore.DidNotReceive().AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ImportAsync_WhenQueueCapacityStopsScan_DoesNotReconcileDeletedPathsFromPartialSnapshot()
    {
        await InitializeRepositoryAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "current repository content").ConfigureAwait(false);
        // A second file the scan would reach next: the first QueueFull has to stop the loop before it is even stored.
        await File.WriteAllTextAsync(Path.Combine(_root, "SECOND.md"), "never reached").ConfigureAwait(false);

        var selectedFolderId = Guid.NewGuid();
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        blobStore.AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>())
                 .Returns(call => new KnowledgeDocumentAddResult(call.Arg<KnowledgeDocumentInput>().DocumentId, WasInserted: true));
        var dispatcher = Substitute.For<IKnowledgeIngestionDispatcher>();
        dispatcher.EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                  .Returns(KnowledgeIngestionEnqueueResult.QueueFull);
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(KnowledgeDocumentStatus.Pending);
        var purge = Substitute.For<IKnowledgeDocumentPurgeService>();
        var service = CreateService(RepositoryBinding(selectedFolderId), blobStore, dispatcher, catalog, purge);

        var result = await service.ImportAsync(selectedFolderId, "repo", CancellationToken.None).ConfigureAwait(false);

        AssertEx.True(result.QueueCapacityReached);
        AssertEx.Equal(expected: 0, result.EnqueuedDocuments);
        await blobStore.Received(1).AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>());
        await dispatcher.Received(1).EnqueueAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await catalog.DidNotReceive().ListAsync(Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await purge.DidNotReceive().PurgeAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    [RunOn(OS.Linux)]
    public async Task ImportAsync_SymbolicLinkSource_IsSkippedWithoutReadingTarget()
    {
        await InitializeRepositoryAsync().ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(_root, "target.bin"), "outside the admitted extension set").ConfigureAwait(false);
        File.CreateSymbolicLink(Path.Combine(_root, "linked.md"), Path.Combine(_root, "target.bin"));

        var selectedFolderId = Guid.NewGuid();
        var blobStore = Substitute.For<IKnowledgeDocumentBlobStore>();
        var catalog = Substitute.For<IKnowledgeDocumentCatalogService>();
        catalog.ListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
               .Returns(Array.Empty<KnowledgeDocumentSummary>());
        var service = CreateService(RepositoryBinding(selectedFolderId),
            blobStore,
            new AcceptingDispatcher(),
            catalog,
            Substitute.For<IKnowledgeDocumentPurgeService>());

        var result = await service.ImportAsync(selectedFolderId, "repo", CancellationToken.None).ConfigureAwait(false);

        AssertEx.Equal(expected: 0, result.AddedDocuments);
        AssertEx.Equal(expected: 2, result.SkippedFiles);
        await blobStore.DidNotReceive().AddAsync(Arg.Any<KnowledgeDocumentInput>(), Arg.Any<CancellationToken>());
    }

    private async Task InitializeRepositoryAsync()
    {
        Directory.CreateDirectory(_root);
        var result = await new HostGitRunner(timeoutSeconds: 30)
                           .RunAsync(_root, AgentHomeGit.Arguments("init"), CancellationToken.None)
                           .ConfigureAwait(false);
        AssertEx.Equal(expected: 0, result.ExitCode, result.StandardError);
    }

    private IDevelopmentRepositoryBindingService RepositoryBinding(Guid selectedFolderId)
    {
        var repositories = Substitute.For<IDevelopmentRepositoryBindingService>();
        repositories.ResolveFolderAsync(selectedFolderId, Arg.Any<CancellationToken>())
                    .Returns(new DevelopmentRepositoryBinding(Guid.Empty, selectedFolderId, "repository", _root, "identity"));
        return repositories;
    }

    private static KnowledgeRepositoryImportService CreateService(IDevelopmentRepositoryBindingService repositories,
        IKnowledgeDocumentBlobStore blobStore,
        IKnowledgeIngestionDispatcher dispatcher,
        IKnowledgeDocumentCatalogService catalog,
        IKnowledgeDocumentPurgeService purge,
        KnowledgeBaseOptions? options = null)
    {
        var extractor = Substitute.For<IDocumentTextExtractor>();
        extractor.IsSupported(Arg.Any<string>()).Returns(call => call.Arg<string>() is ".md" or ".cs");
        // The importer now goes through the shared admission service, so the tests wire the real rule over their own
        // dispatcher + catalog fakes: the assertions stay on the dispatcher, and the rule under test is the shipped one.
        return new KnowledgeRepositoryImportService(repositories,
            new SensitiveFileExclusionService(),
            blobStore,
            new KnowledgeIngestionAdmissionService(catalog, dispatcher),
            catalog,
            purge,
            extractor,
            Options.Create(options ?? new KnowledgeBaseOptions()));
    }

    private static KnowledgeDocumentSummary Summary(Guid documentId, string sourcePath, string sourceKind = "repository")
    {
        return new KnowledgeDocumentSummary(documentId,
            sourcePath,
            KnowledgeDocumentStatus.Indexed,
            FailureReason: null,
            ChunkCount: 1,
            EmbeddingModel: "nomic-embed-text",
            StaleModel: false,
            SizeBytes: 1,
            CreatedAtUtc: 1,
            CollectionId: "REPO",
            SourcePath: sourcePath,
            SourceKind: sourceKind);
    }

    private sealed class AcceptingDispatcher : IKnowledgeIngestionDispatcher
    {
        public ValueTask<KnowledgeIngestionEnqueueResult> EnqueueAsync(Guid documentId, CancellationToken cancellationToken)
        {
            return ValueTask.FromResult(KnowledgeIngestionEnqueueResult.Accepted);
        }
    }
}
