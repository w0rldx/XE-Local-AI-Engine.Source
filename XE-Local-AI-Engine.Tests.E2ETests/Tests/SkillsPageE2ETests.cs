namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-driven E2E for the node skill library (<c>/skills</c>) — a shipped route with no browser coverage at
///     all. The flow guarded here is the third-party IMPORT path end to end, because that is where the feature's
///     security posture lives ("we refuse code, we show you everything, you decide"):
///     <list type="bullet">
///         <item>Import via the <b>Paste</b> source (a literal SKILL.md, no file system, no network).</item>
///         <item>Preview writes nothing and returns a report; the candidate is selectable only after the report.</item>
///         <item>Commit is gated on the untrusted-content acknowledgement AND a non-empty selection.</item>
///         <item>The imported skill lands DISABLED and badged <c>Imported</c> — the real execution gate.</item>
///         <item>Enabling it is a separate, deliberate edit that persists (list badge flips to Enabled).</item>
///         <item>Delete removes it.</item>
///     </list>
///     <para>
///         Paste is chosen over Upload/GitHub on purpose: Upload needs a .zip fixture on disk and GitHub needs real
///         network egress (the E2E host's <c>IHttpClientFactory</c> is un-routed), while Paste drives the exact same
///         two-phase preview → commit endpoints with a deterministic, in-test payload.
///     </para>
///     <para>
///         POOLED: the skill name carries a <c>Guid</c>, and every locator is scoped to that name or to the row
///         containing it, so a concurrent sibling's skills cannot satisfy or break any assertion. Nothing here asserts
///         a node-wide empty state.
///     </para>
/// </summary>
[Category("Page")]
public sealed class SkillsPageE2ETests : XEPooledE2ETestBase
{
    private async Task NavigateAndWaitForSkillsPageAsync()
    {
        await Page.GotoAsync($"{NodeAppUrl}/skills", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Skills"
            }))
            .ToBeVisibleAsync();

        await Expect(Page.GetByTestId("skill-create-button")).ToBeEnabledAsync();
        await Expect(Page.GetByTestId("skill-import-button")).ToBeEnabledAsync();
    }

    [Test]
    [Category("Page")]
    public async Task Skills_Page_Renders_Header_Actions_And_A_Settled_List()
    {
        await NavigateAndWaitForSkillsPageAsync();

        // The list settles into one of its two success shapes; "empty" alone would be a node-wide claim a pooled
        // sibling importing its own skill may legitimately falsify.
        await Expect(Page.Locator("[data-testid='skills-empty'], [data-testid='skills-table']").First)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });

        await Expect(Page.GetByTestId("skills-list-error")).ToHaveCountAsync(0);
    }

    [Test]
    [Category("Page")]
    public async Task Skill_Import_By_Paste_Lands_Disabled_Then_Enable_And_Delete()
    {
        var pageErrors = new List<string>();
        Page.PageError += (_, error) => pageErrors.Add(error);

        await NavigateAndWaitForSkillsPageAsync();

        // Skill-name rule: lowercase letters/digits separated by single dashes. A "N"-format Guid is lowercase hex.
        var skillName = $"e2e-skill-{Guid.NewGuid():N}";
        var markdown = string.Join('\n',
            "---",
            $"name: {skillName}",
            "description: E2E imported skill used to prove the paste import round-trip.",
            "---",
            string.Empty,
            "# E2E imported skill",
            string.Empty,
            "This body is third-party content the node stores verbatim.",
            string.Empty);

        await Page.GetByTestId("skill-import-button").ClickAsync();
        await Expect(Page.GetByTestId("skill-import-warning")).ToBeVisibleAsync();
        // The consequence line is the one sentence the posture cannot ship without; pin it explicitly.
        await Expect(Page.GetByTestId("skill-import-warning-consequence")).ToBeVisibleAsync();

        await Page.GetByTestId("skill-import-tab-paste").ClickAsync();
        await Page.GetByTestId("skill-import-markdown").FillAsync(markdown);

        var previewResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("skill-import-preview").ClickAsync(),
            response => response.Url.Contains("/skills/import/preview", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 15_000
            });

        await Assert.That(previewResponse.Status >= 200 && previewResponse.Status < 300).IsTrue();

        await Expect(Page.GetByTestId("skill-import-report")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Expect(Page.GetByTestId($"skill-import-candidate-{skillName}")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("skill-import-preview-error")).ToHaveCountAsync(0);

        var submit = Page.GetByTestId("skill-import-submit");
        // Nothing selected and nothing acknowledged yet.
        await Expect(submit).ToBeDisabledAsync();

        await Page.GetByTestId($"skill-import-select-{skillName}").CheckAsync();
        // Selected but still unacknowledged — the checkbox is a speed bump, but it is a real one.
        await Expect(submit).ToBeDisabledAsync();

        await Page.GetByTestId("skill-import-acknowledge").CheckAsync();
        await Expect(submit).ToBeEnabledAsync();

        var commitResponse = await Page.RunAndWaitForResponseAsync(async () => await submit.ClickAsync(),
            response => response.Url.Contains("/skills/import", StringComparison.OrdinalIgnoreCase)
                        && !response.Url.Contains("/preview", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "POST", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 15_000
            });

        await Assert.That(commitResponse.Status >= 200 && commitResponse.Status < 300).IsTrue();

        await Expect(Page.GetByTestId("skill-import-outcomes")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Expect(Page.GetByTestId($"skill-import-outcome-{skillName}")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("skill-import-commit-error")).ToHaveCountAsync(0);

        await Page.GetByTestId("skill-import-done").ClickAsync();

        var row = Page.Locator("tr").Filter(new LocatorFilterOptions
        {
            HasText = skillName
        });
        await Expect(row).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
        await Expect(row).ToContainTextAsync("Disabled");
        await Expect(row).ToContainTextAsync("Imported");

        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
        {
            Name = $"Edit {skillName}"
        }).ClickAsync();

        await Expect(Page.GetByTestId("skill-form")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("skill-form-name")).ToHaveValueAsync(skillName, new LocatorAssertionsToHaveValueOptions
        {
            Timeout = 10_000
        });
        // The imported-provenance warning must ride along into the editor, not only the import dialog.
        await Expect(Page.GetByTestId("skill-form-imported-note")).ToBeVisibleAsync();

        await Page.GetByTestId("skill-form-enabled").CheckAsync();

        var updateResponse = await Page.RunAndWaitForResponseAsync(async () => await Page.GetByTestId("skill-form-submit").ClickAsync(),
            response => response.Url.Contains("/api/local/v1/skills/", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "PUT", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 15_000
            });

        await Assert.That(updateResponse.Status >= 200 && updateResponse.Status < 300).IsTrue();

        await Expect(row).ToContainTextAsync("Enabled", new LocatorAssertionsToContainTextOptions
        {
            Timeout = 10_000
        });

        var deleteResponse = await Page.RunAndWaitForResponseAsync(async () =>
            {
                await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions
                {
                    Name = $"Delete {skillName}"
                }).ClickAsync();
                await Page.GetByTestId("confirm-accept").ClickAsync();
            },
            response => response.Url.Contains("/api/local/v1/skills/", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(response.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase),
            new PageRunAndWaitForResponseOptions
            {
                Timeout = 15_000
            });

        await Assert.That(deleteResponse.Status >= 200 && deleteResponse.Status < 300).IsTrue();

        await Expect(row).ToHaveCountAsync(count: 0, new LocatorAssertionsToHaveCountOptions
        {
            Timeout = 10_000
        });

        await Assert.That(pageErrors.Count == 0).IsTrue();
    }
}
