namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Tests.E2ETests.Common;
using XE_Local_AI_Engine.Tests.E2ETests.Infrastructure;

/// <summary>
///     Browser coverage for optional progressive tutorials. The normal fixture seeds all tutorial keys completed;
///     these tests temporarily clear them to exercise the first-use invitations.
/// </summary>
public sealed class OnboardingTourE2ETests : XESerialE2ETestBase
{
    private const string DefaultChatModel = "qwen3.5:0.8b";
    private const string DefaultEmbeddingModel = "qwen3-embedding:0.6b";
    private const string QuickStartProgressKey = XENodeE2EWebApplicationFactory.TourProgressStorageKey;
    private const string AgentsProgressKey = "xe-onboarding-agents-v1-step";
    private const string KnowledgeBaseProgressKey = "xe-onboarding-knowledge-base-v1-step";

    private ILocator WelcomeDialog => Page.GetByTestId("onboarding-welcome-dialog");

    private ILocator ActiveTooltip => Page.Locator("[id^='react-joyride-step-']");

    private ILocator PrimaryTourAction => ActiveTooltip.Locator("[data-action='primary']");

    [Before(Test)]
    public async Task ClearSeededTutorialStateAsync()
    {
        Factory.FakeOllamaState.Models = [DefaultChatModel, DefaultEmbeddingModel];
        await Factory.SetAdminTutorialStateAsync(null);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.Commit
        });
        await Page.EvaluateAsync($$"""
                                   globalThis.localStorage.removeItem('{{QuickStartProgressKey}}');
                                   globalThis.localStorage.removeItem('{{AgentsProgressKey}}');
                                   globalThis.localStorage.removeItem('{{KnowledgeBaseProgressKey}}');
                                   """);
    }

    [After(Test)]
    public async Task RestoreSeededTutorialStateAsync()
    {
        Factory.FakeOllamaState.ChatScript = null;
        Factory.FakeOllamaState.Models = [DefaultChatModel, DefaultEmbeddingModel];
        await SetDefaultModelAsync(null);
        await Factory.SetAdminTutorialStateAsync(XENodeE2EWebApplicationFactory.CompletedMainAppTourState);
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_WithSavedProgress_OffersResumeButNeverAutoStarts()
    {
        await Page.AddInitScriptAsync($$"""
                                        globalThis.localStorage.setItem('{{XENodeE2EWebApplicationFactory.TourProgressStorageKey}}', '{"format":1,"stepId":"chatInput"}');
                                        """);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(WelcomeDialog).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("onboarding-welcome-start")).ToHaveTextAsync("Resume");
        await Expect(Page.Locator("[id^='react-joyride-step-']")).ToHaveCountAsync(0);
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_StartsOnlyOnClick_AndPersistsVersionedStepId()
    {
        await Page.SetViewportSizeAsync(640, 800);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Expect(WelcomeDialog).ToBeVisibleAsync();
        await Expect(Page.Locator("[id^='react-joyride-step-']")).ToHaveCountAsync(0);

        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();
        await Expect(Page.Locator("[data-tour='nav-item-models']")).ToBeHiddenAsync();
        await Expect(Page.Locator("[data-tour='models-overview']")).ToBeVisibleAsync();
        await Expect(Page.Locator("#react-joyride-step-0")).ToBeVisibleAsync();
        await Page.Locator("#react-joyride-step-0 [data-action='primary']").ClickAsync();

        var progress = await Page.EvaluateAsync<string?>($"() => globalThis.localStorage.getItem('{XENodeE2EWebApplicationFactory.TourProgressStorageKey}')");
        await Assert.That(progress).IsNotNull();
        await Assert.That(progress ?? string.Empty).Contains("\"format\":1");
        await Assert.That(progress ?? string.Empty).Contains("\"stepId\":");
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_ReadyModel_ShowsEveryCompactStep_AndCompletesAfterFakeOllamaReply()
    {
        await PrepareReadyQuickStartAsync();
        var replyGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Factory.FakeOllamaState.ChatScript = _ => GatedTutorialReplyAsync(replyGate.Task);
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(WelcomeDialog).ToBeVisibleAsync();
        await Expect(ActiveTooltip).ToHaveCountAsync(0);
        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();

        await AssertTourStepAsync("navChat", "[data-tour='chat-overview']", QuickStartProgressKey);
        await PrimaryTourAction.ClickAsync();

        await AssertTourStepAsync("chatInput", "[data-testid='chat-input']", QuickStartProgressKey);
        var chatInput = Page.GetByPlaceholder("Type your message");
        await chatInput.FillAsync("Reply from the compact Quick Start tutorial");
        await PrimaryTourAction.ClickAsync();

        await AssertTourStepAsync("chatSend", "[data-testid='chat-send-button']", QuickStartProgressKey);
        var sendButton = Page.GetByTestId("chat-send-button");
        var replyCountBefore = await Page.GetByText("Node reply").CountAsync();
        try
        {
            await sendButton.ClickAsync();
            await Expect(chatInput).ToHaveValueAsync(string.Empty);
            await PrimaryTourAction.ClickAsync();
            await AssertTourStepAsync("firstResponse", "[data-testid='chat-input-area']", QuickStartProgressKey);

            await Page.RunAndWaitForResponseAsync(() =>
                {
                    replyGate.TrySetResult(true);
                    return Task.CompletedTask;
                },
                response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

            await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 10000
            });
            await Expect(Page.GetByText("Node reply")).ToHaveCountAsync(replyCountBefore + 1,
                new LocatorAssertionsToHaveCountOptions
                {
                    Timeout = 10000
                });
            await Expect(Page.GetByText("Tutorial reply from FakeOllama")).ToBeVisibleAsync();

            await Expect(ActiveTooltip).ToHaveCountAsync(0);
            await AssertTutorialStatusAsync("main-app-v1", "completed");
        }
        finally
        {
            replyGate.TrySetResult(true);
        }
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_MissingModel_ShowsModelAndRecommendationTargets_WithoutDownloading()
    {
        Factory.FakeOllamaState.Models = [];
        await SetDefaultModelAsync(null);
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();
        await AssertTourStepAsync("navModels", "[data-tour='models-overview']", QuickStartProgressKey);
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("recommendationInstall", "[data-tour='recommendation-install']", QuickStartProgressKey);

        await Page.RunAndWaitForResponseAsync(async () => await ActiveTooltip.Locator("[data-action='skip']").ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));
        await AssertTutorialStatusAsync("main-app-v1", "skipped");
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_InstalledUnselected_ShowsSetDefaultTarget_AndAdvancesAfterSelection()
    {
        await SetDefaultModelAsync("e2e-not-installed");
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();
        await AssertTourStepAsync("setDefaultModel", "[data-tour='set-default-model']", QuickStartProgressKey);
        await Page.RunAndWaitForResponseAsync(async () => await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = $"Set {DefaultChatModel} as default"
            }).ClickAsync(),
            response => response.Url.Contains("/api/local/v1/models/select", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase));

        await AssertTourStepAsync("navChat", "[data-tour='chat-overview']", QuickStartProgressKey);
        await Page.RunAndWaitForResponseAsync(async () => await ActiveTooltip.Locator("[data-action='skip']").ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));
        await AssertTutorialStatusAsync("main-app-v1", "skipped");
    }

    [Test]
    [Category("Onboarding")]
    public async Task QuickStart_FastReplyBeforeNext_CompletesImmediatelyAfterEnteringFirstResponse()
    {
        await PrepareReadyQuickStartAsync();
        await Page.SetViewportSizeAsync(390, 844);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Page.GetByTestId("onboarding-welcome-start").ClickAsync();
        await AssertTourStepAsync("navChat", "[data-tour='chat-overview']", QuickStartProgressKey);
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("chatInput", "[data-testid='chat-input']", QuickStartProgressKey);

        var chatInput = Page.GetByPlaceholder("Type your message");
        await chatInput.FillAsync("Complete after this fast FakeOllama reply");
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("chatSend", "[data-testid='chat-send-button']", QuickStartProgressKey);

        var sendButton = Page.GetByTestId("chat-send-button");
        var replyCountBefore = await Page.GetByText("Node reply").CountAsync();
        await sendButton.ClickAsync();
        await Expect(sendButton).ToHaveTextAsync("Send", new LocatorAssertionsToHaveTextOptions
        {
            Timeout = 10000
        });
        await Expect(Page.GetByText("Node reply")).ToHaveCountAsync(replyCountBefore + 1,
            new LocatorAssertionsToHaveCountOptions
            {
                Timeout = 10000
            });
        await Expect(Page.GetByText("[fake-ollama] Complete after this fast FakeOllama reply")).ToBeVisibleAsync();

        await Page.RunAndWaitForResponseAsync(async () => await PrimaryTourAction.ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Expect(ActiveTooltip).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("chat-input-area")).ToBeVisibleAsync();
    }

    [Test]
    [Category("Onboarding")]
    public async Task AgentsBasics_FromTutorialCatalog_ShowsEveryTarget_WithoutMutatingDefinitions()
    {
        await StartCatalogTutorialAsync("agents-basics");

        var definitionRows = Page.Locator("[data-testid^='agent-definition-row-']");
        await AssertTourStepAsync("agentsOverview", "[data-tour='agents-overview']", AgentsProgressKey);
        var definitionCountBefore = await definitionRows.CountAsync();

        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("agentsTemplates", "[data-tour='agents-templates']", AgentsProgressKey);
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("agentsCreate", "[data-tour='agents-create']", AgentsProgressKey);
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("agentsList", "[data-tour='agents-list']", AgentsProgressKey);
        await Page.RunAndWaitForResponseAsync(async () => await PrimaryTourAction.ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Expect(ActiveTooltip).ToHaveCountAsync(0);
        await Expect(definitionRows).ToHaveCountAsync(definitionCountBefore);
        await Expect(Page.GetByTestId("agent-editor-card")).ToBeHiddenAsync();
        await AssertTutorialStatusAsync("agents-v1", "completed");
    }

    [Test]
    [Category("Onboarding")]
    public async Task KnowledgeBaseBasics_FromTutorialCatalog_ShowsEveryTarget_WithoutMutatingDocuments()
    {
        await StartCatalogTutorialAsync("knowledge-base-basics");

        var documentRows = Page.Locator("[data-testid^='knowledge-row-']");
        await AssertTourStepAsync("knowledgeOverview", "[data-tour='knowledge-overview']", KnowledgeBaseProgressKey);
        var documentCountBefore = await documentRows.CountAsync();

        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("knowledgeUpload", "[data-tour='knowledge-upload']", KnowledgeBaseProgressKey);
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("knowledgeDocuments", "[data-tour='knowledge-documents']", KnowledgeBaseProgressKey);
        await PrimaryTourAction.ClickAsync();
        await AssertTourStepAsync("knowledgeSearch", "[data-tour='knowledge-search']", KnowledgeBaseProgressKey);
        await Page.RunAndWaitForResponseAsync(async () => await PrimaryTourAction.ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Expect(ActiveTooltip).ToHaveCountAsync(0);
        await Expect(documentRows).ToHaveCountAsync(documentCountBefore);
        await Expect(Page.GetByTestId("knowledge-upload-progress")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("knowledge-search-results")).ToHaveCountAsync(0);
        await AssertTutorialStatusAsync("knowledge-base-v1", "completed");
    }

    [Test]
    [Category("Onboarding")]
    public async Task AgentsInvitation_NotNowPersistsSkipped_WithoutStartingTutorial()
    {
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("onboarding-welcome-skip").ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Page.GotoAsync($"{NodeAppUrl}/agents", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        var invitation = Page.GetByTestId("tutorial-invitation-agents-basics");
        await Expect(invitation).ToBeVisibleAsync();
        await Expect(Page.Locator("[id^='react-joyride-step-']")).ToHaveCountAsync(0);

        await Page.RunAndWaitForResponseAsync(async () => await invitation.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
            {
                Name = "Not now"
            }).ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        var persisted = await Factory.GetAdminTutorialStateAsync();
        await Assert.That(persisted ?? string.Empty).Contains("\"key\":\"agents-v1\"");
        await Assert.That(persisted ?? string.Empty).Contains("\"status\":\"skipped\"");
    }

    private async Task StartCatalogTutorialAsync(string tutorialId)
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GotoAsync($"{NodeAppUrl}/", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
        await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("onboarding-welcome-skip").ClickAsync(),
            response => response.Url.Contains("tutorial-state", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase));

        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
        {
            Name = "About"
        }).ClickAsync();
        await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
        {
            Name = "Tutorials"
        }).ClickAsync();
        var card = Page.GetByTestId($"tutorial-card-{tutorialId}");
        await Expect(card).ToBeVisibleAsync();
        await card.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
        {
            Name = "Start"
        }).ClickAsync();
    }

    private async Task AssertTourStepAsync(string stepId, string targetSelector, string progressKey)
    {
        var target = Page.Locator(targetSelector);
        await Expect(target).ToHaveCountAsync(1);
        await Expect(target).ToBeVisibleAsync();
        await Expect(ActiveTooltip).ToHaveCountAsync(1);
        await Expect(ActiveTooltip).ToBeVisibleAsync();
        await Expect(ActiveTooltip).ToHaveCSSAsync("opacity", "1");
        await Expect(PrimaryTourAction).ToBeVisibleAsync();
        await Expect(PrimaryTourAction).ToBeEnabledAsync();

        var targetIntersectsViewport = await target.EvaluateAsync<bool>("""
                                                                        element => {
                                                                            const bounds = element.getBoundingClientRect();
                                                                            return bounds.width > 0 && bounds.height > 0 && bounds.right > 0 && bounds.bottom > 0
                                                                                && bounds.left < globalThis.innerWidth && bounds.top < globalThis.innerHeight;
                                                                        }
                                                                        """);
        await Assert.That(targetIntersectsViewport).IsTrue();

        var tooltipFitsViewport = await ActiveTooltip.EvaluateAsync<bool>("""
                                                                          element => {
                                                                              const bounds = element.getBoundingClientRect();
                                                                              return bounds.width > 0 && bounds.height > 0 && bounds.left >= 0 && bounds.top >= 0
                                                                                  && bounds.right <= globalThis.innerWidth && bounds.bottom <= globalThis.innerHeight;
                                                                          }
                                                                          """);
        await Assert.That(tooltipFitsViewport).IsTrue();

        var hasNoHorizontalOverflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= globalThis.innerWidth");
        await Assert.That(hasNoHorizontalOverflow).IsTrue();

        var progress = await Page.EvaluateAsync<string?>($"() => globalThis.localStorage.getItem('{progressKey}')");
        await Assert.That(progress).IsEqualTo($"{{\"format\":1,\"stepId\":\"{stepId}\"}}");
    }

    private async Task SetDefaultModelAsync(string? modelName)
    {
        var settingsStore = Factory.Services.GetRequiredService<INodeSettingsStore>();
        var settings = await settingsStore.LoadAsync();
        await settingsStore.SaveAsync(settings with
        {
            DefaultModelName = modelName
        });
    }

    private async Task PrepareReadyQuickStartAsync()
    {
        await SetDefaultModelAsync(DefaultChatModel);
        await Page.AddInitScriptAsync($$"""
                                        globalThis.localStorage.setItem('xe-node-chat-selected-model', '{{DefaultChatModel}}');
                                        """);
    }

    private async Task AssertTutorialStatusAsync(string key, string status)
    {
        var persisted = await Factory.GetAdminTutorialStateAsync();
        using var document = JsonDocument.Parse(persisted ?? "[]");
        var matchingEntry = document.RootElement.EnumerateArray().Any(entry =>
            string.Equals(entry.GetProperty("key").GetString(), key, StringComparison.Ordinal)
            && string.Equals(entry.GetProperty("status").GetString(), status, StringComparison.Ordinal));
        await Assert.That(matchingEntry).IsTrue();
    }

    private static async IAsyncEnumerable<string> GatedTutorialReplyAsync(Task replyGate)
    {
        await replyGate.ConfigureAwait(false);
        yield return "Tutorial reply from FakeOllama";
    }
}
