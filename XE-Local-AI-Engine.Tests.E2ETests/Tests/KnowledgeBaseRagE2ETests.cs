namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OllamaSharp.Models.Chat;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Focused offline RAG journey over the real authenticated browser surface and the FakeOllama embedding/chat server.
///     The repository is a fresh local Git root containing only regular files; no network, external model, or symlink is
///     involved. One scenario deliberately keeps the lifecycle ordering visible: import two collection snapshots, prove
///     isolation and provenance, reconcile an update plus deletion, then ground a plain-chat turn with persisted sources.
/// </summary>
[Category("Page")]
public sealed class KnowledgeBaseRagE2ETests : XESerialE2ETestBase
{
    private const string DefaultCollection = "DEFAULT";
    private const string SecondaryCollection = "RAG-E2E-SECONDARY";
    private const string SharedDocument = "shared.md";
    private const string ObsoleteDocument = "obsolete.md";
    private const string AlphaToken = "alpha-orbit-7319";
    private const string BetaToken = "beta-canyon-8421";
    private const string GammaToken = "gamma-harbor-9537";
    private const string ObsoleteToken = "obsolete-forest-1648";
    private const string GroundedReplyMarker = "GROUNDING-CONTEXT-VERIFIED";
    private const string FakeOllamaChatModel = "qwen3.5:0.8b";

    private string? _repositorySourceId;
    private Guid? _selectedFolderId;

    [Test]
    public async Task RepositorySnapshots_AreCollectionIsolated_Reconciled_AndGroundPlainChat()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "xe-rag-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repositoryRoot);

        try
        {
            await WriteRepositorySnapshotAsync(repositoryRoot, AlphaToken, includeObsolete: true);
            var repositoryAlias = await RegisterRepositoryAsync(repositoryRoot);

            await NavigateToKnowledgeBaseAsync();

            using var firstImport = await ImportRepositoryAsync(repositoryAlias);
            await AssertImportCountsAsync(firstImport, added: 2, updated: 0, removed: 0);
            await WaitForIndexedDocumentAsync(SharedDocument, DefaultCollection, AlphaToken);
            await WaitForIndexedDocumentAsync(ObsoleteDocument, DefaultCollection, ObsoleteToken);

            await WriteRepositorySnapshotAsync(repositoryRoot, BetaToken, includeObsolete: false);
            await OpenCollectionAsync(SecondaryCollection);

            using var secondImport = await ImportRepositoryAsync(repositoryAlias);
            await AssertImportCountsAsync(secondImport, added: 1, updated: 0, removed: 0);
            await WaitForIndexedDocumentAsync(SharedDocument, SecondaryCollection, BetaToken);

            await AssertSearchHitAsync(BetaToken, SharedDocument, sourcePath: SharedDocument);
            await AssertSearchExcludesAsync(AlphaToken, forbiddenText: AlphaToken, expectedText: BetaToken);

            await OpenCollectionAsync(DefaultCollection);
            await AssertSearchHitAsync(AlphaToken, SharedDocument, sourcePath: SharedDocument);
            await AssertSearchHitAsync(ObsoleteToken, ObsoleteDocument, sourcePath: ObsoleteDocument);
            await AssertSearchExcludesAsync(BetaToken, forbiddenText: BetaToken, expectedText: AlphaToken);

            await WriteRepositorySnapshotAsync(repositoryRoot, GammaToken, includeObsolete: false);
            using var reconciliation = await ImportRepositoryAsync(repositoryAlias);
            await AssertImportCountsAsync(reconciliation, added: 0, updated: 1, removed: 1);
            await WaitForIndexedDocumentAsync(SharedDocument, DefaultCollection, GammaToken);
            await Expect(DocumentRow(ObsoleteDocument)).ToHaveCountAsync(0);

            await AssertSearchHitAsync(GammaToken, SharedDocument, sourcePath: SharedDocument);
            await AssertSearchExcludesAsync(AlphaToken, forbiddenText: AlphaToken, expectedText: GammaToken);
            await AssertSearchExcludesAsync(ObsoleteToken, forbiddenText: ObsoleteToken, expectedText: GammaToken);

            await OpenCollectionAsync(SecondaryCollection);
            await AssertSearchHitAsync(BetaToken, SharedDocument, sourcePath: SharedDocument);
            await AssertSearchExcludesAsync(GammaToken, forbiddenText: GammaToken, expectedText: BetaToken);

            await OpenCollectionAsync(DefaultCollection);
            await AssertPlainChatGroundingAsync(GammaToken);
        }
        finally
        {
            Factory.FakeOllamaState.ChatScript = null;
            await PurgeScenarioDocumentsAsync();
            await RevokeScenarioRepositoryAsync();
            TryDeleteDirectory(repositoryRoot);
        }
    }

    private async Task NavigateToKnowledgeBaseAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/knowledge-base", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Knowledge base"
            }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByTestId("knowledge-active-collection")).ToContainTextAsync(DefaultCollection);
    }

    private async Task<string> RegisterRepositoryAsync(string repositoryRoot)
    {
        var alias = "rag-e2e-" + Guid.NewGuid().ToString("N");
        await using var scope = Factory.Services.CreateAsyncScope();
        var repositories = scope.ServiceProvider.GetRequiredService<IDevelopmentRepositoryBindingService>();
        var reference = await repositories.RegisterAsync(alias, repositoryRoot);
        _repositorySourceId = reference.Id;
        _selectedFolderId = Guid.Parse(reference.Id);

        return reference.Alias;
    }

    private async Task<JsonDocument> ImportRepositoryAsync(string repositoryAlias)
    {
        var select = Page.GetByTestId("knowledge-repository-select");
        if (!string.Equals(await select.InputValueAsync(), repositoryAlias, StringComparison.Ordinal))
        {
            await select.ClickAsync();
            await Page.GetByRole(AriaRole.Option, new PageGetByRoleOptions
                      {
                          Name = repositoryAlias,
                          Exact = true
                      })
                      .ClickAsync();
        }

        await Expect(Page.GetByTestId("knowledge-repository-import")).ToBeEnabledAsync();

        var responseTask = Page.WaitForResponseAsync(response =>
            response.Request.Method == "POST"
            && response.Url.EndsWith("/api/local/v1/knowledge-base/repositories/import", StringComparison.Ordinal));

        await Page.GetByTestId("knowledge-repository-import").ClickAsync();
        var response = await responseTask;
        await Assert.That(response.Ok).IsTrue();

        var payload = JsonDocument.Parse(await response.TextAsync());
        await Assert.That(payload.RootElement.GetProperty("collectionId").GetString()).IsEqualTo(await Page.GetByTestId("knowledge-active-collection").TextContentAsync());
        return payload;
    }

    private static async Task AssertImportCountsAsync(JsonDocument payload, int added, int updated, int removed)
    {
        var root = payload.RootElement;
        await Assert.That(root.GetProperty("addedDocuments").GetInt32()).IsEqualTo(added);
        await Assert.That(root.GetProperty("updatedDocuments").GetInt32()).IsEqualTo(updated);
        await Assert.That(root.GetProperty("removedDocuments").GetInt32()).IsEqualTo(removed);
        await Assert.That(root.GetProperty("queueCapacityReached").GetBoolean()).IsFalse();
    }

    private async Task OpenCollectionAsync(string collectionId)
    {
        await Page.GetByTestId("knowledge-collection-input").FillAsync(collectionId);
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                  {
                      Name = "Open collection"
                  })
                  .ClickAsync();

        await Expect(Page.GetByTestId("knowledge-active-collection")).ToContainTextAsync(collectionId);
    }

    private ILocator DocumentRow(string displayName)
    {
        return Page.Locator("[data-testid^='knowledge-row-']").Filter(new LocatorFilterOptions
        {
            HasTextString = displayName
        });
    }

    private async Task WaitForIndexedDocumentAsync(string displayName, string collectionId, string expectedContent)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await using var scope = Factory.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentCatalogService>();
            var summary = (await catalog.ListAsync(collectionId, timeout.Token))
                .SingleOrDefault(document => string.Equals(document.DisplayName, displayName, StringComparison.Ordinal));
            if (summary?.Status == KnowledgeDocumentStatus.Indexed)
            {
                var detail = await catalog.GetAsync(summary.DocumentId, timeout.Token);
                if (detail is not null && detail.Chunks.Any(chunk => chunk.Content.Contains(expectedContent, StringComparison.Ordinal)))
                {
                    break;
                }
            }

            await Task.Delay(100, timeout.Token);
        }

        // The "Repository import queued …" toast (top-right, autoClose 5 s) sits over the Refresh button, and Mantine
        // PAUSES auto-close while the pointer hovers a notification — which is exactly where Playwright parks the mouse
        // while retrying the intercepted click, so the toast never expires and the click times out. Park the pointer
        // elsewhere and let the toasts drain before clicking.
        await Page.Mouse.MoveAsync(x: 0, y: 0);
        await Expect(Page.Locator(".mantine-Notification-root")).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions
        {
            Timeout = 15_000
        });

        var refreshResponse = Page.WaitForResponseAsync(response =>
            response.Request.Method == "GET"
            && response.Url.Contains("/api/local/v1/knowledge-base/documents", StringComparison.Ordinal));
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
                  {
                      Name = "Refresh",
                      Exact = true
                  })
                  .ClickAsync();
        await refreshResponse;

        var row = DocumentRow(displayName);
        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });
        await Expect(row.GetByTestId("knowledge-status-Indexed")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 30_000
        });
        await Expect(row).ToContainTextAsync("repository");
        await Expect(row).ToContainTextAsync(displayName);
    }

    private async Task AssertSearchHitAsync(string query, string displayName, string sourcePath)
    {
        await SubmitSearchAsync(query);
        var results = Page.GetByTestId("knowledge-search-results");
        await Expect(results).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });
        await Expect(results).ToContainTextAsync(displayName);
        await Expect(results).ToContainTextAsync(sourcePath);
    }

    private async Task AssertSearchExcludesAsync(string query, string forbiddenText, string expectedText)
    {
        await SubmitSearchAsync(query);
        var results = Page.GetByTestId("knowledge-search-results");
        await Expect(results).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 15_000
        });
        await Expect(results).ToContainTextAsync(expectedText);
        await Expect(results).Not.ToContainTextAsync(forbiddenText);
    }

    private async Task SubmitSearchAsync(string query)
    {
        var responseTask = Page.WaitForResponseAsync(response =>
            response.Request.Method == "POST"
            && response.Url.EndsWith("/api/local/v1/knowledge-base/search", StringComparison.Ordinal));

        var input = Page.GetByTestId("knowledge-search-input");
        await input.FillAsync(query);
        await input.PressAsync("Enter");

        var response = await responseTask;
        await Assert.That(response.Ok).IsTrue();
    }

    private async Task AssertPlainChatGroundingAsync(string query)
    {
        Factory.FakeOllamaState.ChatScript = request => GroundedReplyAsync(request, query);
        await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        var newConversation = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "New plain chat"
        });
        await Expect(newConversation).ToBeVisibleAsync();
        await newConversation.ClickAsync();
        await SelectFakeOllamaModelAsync();

        var knowledgeToggle = Page.GetByTestId("chat-knowledge-base-toggle");
        await Expect(knowledgeToggle).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions
        {
            Timeout = 10_000
        });
        if (!string.Equals(await knowledgeToggle.GetAttributeAsync("aria-pressed"), "true", StringComparison.OrdinalIgnoreCase))
        {
            await knowledgeToggle.ClickAsync();
        }

        var input = Page.GetByPlaceholder("Type your message");
        var send = Page.GetByTestId("chat-send-button");
        await input.FillAsync($"What does {query} describe?");
        await send.ClickAsync();
        await Expect(send).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 20_000
        });
        await Expect(Page.GetByText(GroundedReplyMarker, new PageGetByTextOptions
            {
                Exact = true
            }).Last)
            .ToBeVisibleAsync();

        await Expect(Page.GetByTestId("chat-sources-strip")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Page.GetByTestId("chat-sources-toggle").ClickAsync();
        var sourceCard = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "Open source: Shared knowledge",
            Exact = true
        });
        await Expect(sourceCard).ToBeVisibleAsync();
        await sourceCard.ClickAsync();
        var detail = Page.GetByTestId("knowledge-detail");
        await Expect(detail).ToContainTextAsync(DefaultCollection);
        await Expect(detail).ToContainTextAsync(SharedDocument);
        await Expect(detail).ToContainTextAsync(query);
    }

    private async Task SelectFakeOllamaModelAsync()
    {
        var trigger = Page.GetByTestId("chat-model-selector-trigger");
        await Expect(trigger).ToBeVisibleAsync();
        await trigger.ClickAsync();

        var option = Page.GetByTestId($"chat-model-selector-option-{FakeOllamaChatModel}");
        await Expect(option).ToBeVisibleAsync();
        await option.ClickAsync();
        await Expect(Page.GetByTestId("chat-model-selector-selected"))
            .ToContainTextAsync(FakeOllamaChatModel);
    }

    private async Task PurgeScenarioDocumentsAsync()
    {
        if (_repositorySourceId is null)
        {
            return;
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentCatalogService>();
        var purge = scope.ServiceProvider.GetRequiredService<IKnowledgeDocumentPurgeService>();
        foreach (var collection in new[]
                 {
                     DefaultCollection,
                     SecondaryCollection
                 })
        {
            var documents = await catalog.ListAsync(collection, "repository", _repositorySourceId, CancellationToken.None);
            foreach (var document in documents)
            {
                _ = await purge.PurgeAsync(document.DocumentId, CancellationToken.None);
            }
        }
    }

    private async Task RevokeScenarioRepositoryAsync()
    {
        if (_selectedFolderId is not { } selectedFolderId)
        {
            return;
        }

        await using var scope = Factory.Services.CreateAsyncScope();
        _ = await scope.ServiceProvider.GetRequiredService<INodeSelectedFolderStore>()
                       .RevokeAsync(selectedFolderId, CancellationToken.None);
    }

    private static async IAsyncEnumerable<string> GroundedReplyAsync(ChatRequest request, string expectedToken)
    {
        await Task.Yield();
        var prompt = string.Join('\n', (request.Messages ?? []).Select(static message => message.Content ?? string.Empty));
        yield return prompt.Contains(expectedToken, StringComparison.Ordinal)
                     && prompt.Contains(SharedDocument, StringComparison.Ordinal)
            ? GroundedReplyMarker
            : "GROUNDING-CONTEXT-MISSING";
    }

    private static async Task WriteRepositorySnapshotAsync(string repositoryRoot, string token, bool includeObsolete)
    {
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, SharedDocument),
            $"# Shared knowledge\n\nThe deterministic repository fact is {token}.\n");

        var obsoletePath = Path.Combine(repositoryRoot, ObsoleteDocument);
        if (includeObsolete)
        {
            await File.WriteAllTextAsync(obsoletePath,
                $"# Retiring knowledge\n\nThis tracked fact is {ObsoleteToken}.\n");
        }
        else if (File.Exists(obsoletePath))
        {
            File.Delete(obsoletePath);
        }

        if (!Directory.Exists(Path.Combine(repositoryRoot, ".git")))
        {
            await RunGitAsync(repositoryRoot, "init", "--quiet");
        }

        await RunGitAsync(repositoryRoot, "add", "--all");
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Could not start git for the deterministic RAG E2E repository.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {await standardError}");
        }

        _ = await standardOutput;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort local fixture cleanup.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort local fixture cleanup.
        }
    }
}
