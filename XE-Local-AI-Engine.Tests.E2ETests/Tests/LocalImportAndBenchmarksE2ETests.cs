namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

/// <summary>
///     Browser-level smoke coverage for the two desktop surfaces introduced together: local GGUF import and
///     benchmark model comparison. Provider-heavy copy and generation behavior remains covered by the backend
///     transaction suites; these tests prove the authenticated desktop SPA exposes the shipped routes and dialogs.
/// </summary>
[Category("Page")]
public sealed class LocalImportAndBenchmarksE2ETests : XEE2ETestBase
{
    [Test]
    public async Task Models_HeadlessImportCapability_HidesDesktopMutation()
    {
        await Page.GotoAsync($"{NodeAppUrl}/models", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        }).ConfigureAwait(false);

        await Expect(Page.GetByTestId("installed-models-table")).ToBeVisibleAsync().ConfigureAwait(false);
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
              {
                  Name = "Import model"
              }))
              .ToHaveCountAsync(0)
              .ConfigureAwait(false);
    }

    [Test]
    public async Task Benchmarks_Route_RendersProjectWorkspace()
    {
        await Page.GotoAsync($"{NodeAppUrl}/benchmarks", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        }).ConfigureAwait(false);

        await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
              {
                  Name = "Local model benchmarks"
              }))
              .ToBeVisibleAsync()
              .ConfigureAwait(false);
        await Expect(Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions
              {
                  Name = "New project"
              }))
              .ToBeVisibleAsync()
              .ConfigureAwait(false);
        await Expect(Page.GetByText("Create a project to freeze one task and compare models."))
              .ToBeVisibleAsync()
              .ConfigureAwait(false);
    }
}
