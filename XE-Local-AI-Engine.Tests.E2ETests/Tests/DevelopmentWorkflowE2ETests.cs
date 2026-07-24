namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Diagnostics;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

[Category("Development")]
public sealed class DevelopmentWorkflowE2ETests : XEE2ETestBase
{
    /// <summary>
    ///     Registers <paramref name="repositoryRoot" /> through the Development page's "Register repository"
    ///     dialog and leaves it selected in the project form's repository picker.
    ///     <para>
    ///         Registration auto-selects the repository it just created (DevelopmentProjectForm.register sets
    ///         selectedFolderId from the response), so this returns with the form ready to submit. The
    ///         assertion on the picker's displayed alias is what proves the round-trip actually landed —
    ///         without it a failed registration would only surface later as a disabled Create button.
    ///     </para>
    /// </summary>
    private async Task RegisterRepositoryAsync(string repositoryRoot)
    {
        var alias = "e2e-" + Path.GetFileName(repositoryRoot);

        await Page.GetByTestId("development-open-register-repository").ClickAsync().ConfigureAwait(false);

        var aliasInput = Page.GetByTestId("development-register-alias");
        await Expect(aliasInput).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        }).ConfigureAwait(false);

        await aliasInput.FillAsync(alias).ConfigureAwait(false);
        await Page.GetByTestId("development-register-path").FillAsync(repositoryRoot).ConfigureAwait(false);
        await Page.GetByTestId("development-register-repository").ClickAsync().ConfigureAwait(false);

        // The dialog closes and the picker shows the newly registered alias only once the POST succeeded.
        await Expect(Page.GetByTestId("development-repository-select"))
            .ToHaveValueAsync(alias, new LocatorAssertionsToHaveValueOptions
            {
                Timeout = 10_000
            }).ConfigureAwait(false);
    }

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

            // The project form no longer takes a free-text repository root: d88237b8 ("Enable repository-bound
            // Development Mode by default") replaced it with a Select over repositories registered up front, so
            // the repository has to be registered through the dialog before it can be picked.
            await RegisterRepositoryAsync(repositoryRoot).ConfigureAwait(false);

            await Page.GetByLabel("Project objective").FillAsync("Exercise the complete local Development workflow").ConfigureAwait(false);
            await Page.GetByLabel("Initial task title").FillAsync("Add the deterministic feature file").ConfigureAwait(false);
            await Page.GetByLabel("Requirements").FillAsync("Create feature.txt with the approved deterministic content.").ConfigureAwait(false);
            await Page.GetByLabel("Acceptance criteria (JSON)").FillAsync("[\"feature.txt contains the approved content\"]")
                      .ConfigureAwait(false);
            await Page.GetByLabel("Coder model ID").FillAsync("qwen3.5:0.8b").ConfigureAwait(false);
            await Page.GetByLabel("Reviewer model ID").FillAsync("qwen3.5:0.8b").ConfigureAwait(false);
            // Target the checkbox by test id: the acknowledgement copy is reworded with the surface (it now reads
            // "I trust the selected repository to execute Development commands with my host-user permissions."),
            // and a label-text locator turns every such rewording into a 30 s timeout with no useful message.
            await Page.GetByTestId("development-trust-acknowledgement").CheckAsync().ConfigureAwait(false);
            await Page.GetByTestId("development-create-project").ClickAsync().ConfigureAwait(false);

            var detail = Page.GetByTestId("development-project-detail");
            await Expect(detail).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            }).ConfigureAwait(false);

            var nextAction = Page.GetByTestId("development-start-next");
            await nextAction.ClickAsync().ConfigureAwait(false);
            await Expect(Page.GetByTestId("development-live-panel")).ToContainTextAsync("Development E2E live output",
                new LocatorAssertionsToContainTextOptions
                {
                    Timeout = 10_000
                }).ConfigureAwait(false);
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

        using var process = new Process
        {
            StartInfo = startInfo
        };
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
