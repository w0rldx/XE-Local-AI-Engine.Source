namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Diagnostics;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

[Category("Development")]
public sealed class DevelopmentWorkflowE2ETests : XEE2ETestBase
{
    [Test]
    public async Task LocalWorkflow_AppliesPatchOnlyAfterValidationAndIndependentReview()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "xe-development-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CreateRepositoryAsync(repositoryRoot).ConfigureAwait(false);
            await Page.GotoAsync($"{NodeAppUrl}/development", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            }).ConfigureAwait(false);

            await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Development Mode"
            })).ToBeVisibleAsync().ConfigureAwait(false);

            var skipTour = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
            {
                Name = "Skip",
                Exact = true
            });
            if (await skipTour.IsVisibleAsync().ConfigureAwait(false))
            {
                await skipTour.ClickAsync().ConfigureAwait(false);
            }

            await Page.GetByLabel("Repository root").FillAsync(repositoryRoot).ConfigureAwait(false);
            await Page.GetByLabel("Project objective").FillAsync("Exercise the complete local Development workflow").ConfigureAwait(false);
            await Page.GetByLabel("Initial task title").FillAsync("Add the deterministic feature file").ConfigureAwait(false);
            await Page.GetByLabel("Requirements").FillAsync("Create feature.txt with the approved deterministic content.").ConfigureAwait(false);
            await Page.GetByLabel("Acceptance criteria (JSON)").FillAsync("[\"feature.txt contains the approved content\"]")
                      .ConfigureAwait(false);
            await Page.GetByLabel("Coder model ID").FillAsync("qwen3.5:0.8b").ConfigureAwait(false);
            await Page.GetByLabel("Reviewer model ID").FillAsync("qwen3.5:0.8b").ConfigureAwait(false);
            await Page.GetByLabel("I trust this repository to run the fixed Development command catalog with the configured process sandbox.")
                      .CheckAsync()
                      .ConfigureAwait(false);
            await Page.GetByTestId("development-create-project").ClickAsync().ConfigureAwait(false);

            var detail = Page.GetByTestId("development-project-detail");
            await Expect(detail).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            }).ConfigureAwait(false);

            var nextAction = Page.GetByTestId("development-start-next");
            await nextAction.ClickAsync().ConfigureAwait(false);
            await Expect(detail.GetByText("InProgress", new LocatorGetByTextOptions
            {
                Exact = true
            }).First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 15_000
            }).ConfigureAwait(false);
            await Expect(Page.GetByTestId("development-apply-panel")).ToHaveCountAsync(0).ConfigureAwait(false);

            await Expect(nextAction).ToHaveTextAsync("Run deterministic validation", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 10_000
            }).ConfigureAwait(false);
            await nextAction.ClickAsync().ConfigureAwait(false);
            await Expect(nextAction).ToHaveTextAsync("Start independent review", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 15_000
            }).ConfigureAwait(false);
            await nextAction.ClickAsync().ConfigureAwait(false);

            var applyPanel = Page.GetByTestId("development-apply-panel");
            await Expect(applyPanel).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 15_000
            }).ConfigureAwait(false);
            var applyButton = Page.GetByTestId("development-apply-patch");
            await Expect(applyButton).ToBeDisabledAsync().ConfigureAwait(false);

            await Page.GetByTestId("development-preview-patch").ClickAsync().ConfigureAwait(false);
            await Expect(Page.GetByLabel("Verified patch preview")).ToContainTextAsync("feature.txt", new LocatorAssertionsToContainTextOptions
            {
                Timeout = 10_000
            }).ConfigureAwait(false);
            await Expect(applyButton).ToBeEnabledAsync().ConfigureAwait(false);
            await applyButton.ClickAsync().ConfigureAwait(false);

            await Expect(detail.GetByText("Completed", new LocatorGetByTextOptions
            {
                Exact = true
            }).First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            }).ConfigureAwait(false);
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "feature.txt")).ConfigureAwait(false))
                        .IsEqualTo("implemented by Development E2E\n");

            await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            }).ConfigureAwait(false);
            await Expect(Page.GetByPlaceholder("Type your message")).ToBeVisibleAsync().ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    private static async Task CreateRepositoryAsync(string repositoryRoot)
    {
        Directory.CreateDirectory(repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "README.md"), "Development E2E fixture\n").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot, "init", "--initial-branch=main").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot, "add", "README.md").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot,
                "-c",
                "user.name=Development E2E",
                "-c",
                "user.email=development-e2e@example.test",
                "commit",
                "-m",
                "initial fixture")
            .ConfigureAwait(false);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await standardOutput.ConfigureAwait(false);
        var error = await standardError.ConfigureAwait(false);
        await Assert.That(process.ExitCode)
                    .IsEqualTo(0)
                    .Because($"git {string.Join(' ', arguments)} failed: {output}{error}");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort E2E cleanup; the assertion result remains authoritative.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort E2E cleanup; the assertion result remains authoritative.
        }
    }
}
