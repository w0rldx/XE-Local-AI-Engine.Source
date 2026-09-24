namespace XE_Local_AI_Engine.Tests.E2ETests.Tests;

using System.Diagnostics;
using System.Reflection;
using Microsoft.Playwright;
using XE_Local_AI_Engine.Tests.E2ETests.Common;

[Category("Development")]
public sealed class DevelopmentWorkflowE2ETests : XEPooledE2ETestBase
{
    /// <summary>
    ///     Passes the one-time Development Mode disclosure that gates this route, the way an operator does:
    ///     tick the understanding checkbox, then continue.
    ///     <para>
    ///         DevelopmentConsentGate renders the dialog INSTEAD of the page while unacknowledged, so nothing
    ///         behind it exists in the DOM until this runs. Acknowledgement lives in localStorage, and
    ///         Playwright gives every test a fresh browser context, so the gate is present on every test —
    ///         this is therefore unconditional on purpose. A "dismiss it if it happens to be there" helper
    ///         would silently stop exercising the gate the day it regressed.
    ///     </para>
    ///     <para>
    ///         Clicking through rather than seeding the localStorage key is deliberate: the gate is a blocking
    ///         disclosure an operator cannot skip, so a suite that bypassed it would no longer cover the path a
    ///         real operator takes — and would not notice if acknowledging ever stopped unblocking the page.
    ///     </para>
    /// </summary>
    private async Task AcknowledgeDevelopmentConsentAsync()
    {
        // Keyed on the accept button, NOT on the gate's own `development-consent-dialog` id: that attribute is
        // passed to DialogShell, which does not forward unknown props to the DOM, so it never reaches the page.
        // (The component's unit test locates the dialog by its title text for the same reason.)
        var accept = Page.GetByTestId("development-consent-accept");
        await Expect(accept).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });

        // Continue stays disabled until the checkbox is ticked; checking it first is what makes the click land.
        await Page.GetByTestId("development-consent-checkbox").CheckAsync();
        await accept.ClickAsync();

        // The gate is gone AND the page behind it rendered — the two halves of "acknowledging unblocks".
        await Expect(accept).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions
        {
            Timeout = 10_000
        });
        await Expect(Page.GetByTestId("development-open-register-repository")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });
    }

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

        await Page.GetByTestId("development-open-register-repository").ClickAsync();

        var aliasInput = Page.GetByTestId("development-register-alias");
        await Expect(aliasInput).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
        {
            Timeout = 10_000
        });

        await aliasInput.FillAsync(alias);
        await Page.GetByTestId("development-register-path").FillAsync(repositoryRoot);
        await Page.GetByTestId("development-register-repository").ClickAsync();

        // The dialog closes and the picker shows the newly registered alias only once the POST succeeded.
        await Expect(Page.GetByTestId("development-repository-select"))
            .ToHaveValueAsync(alias, new LocatorAssertionsToHaveValueOptions
            {
                Timeout = 10_000
            });
    }

    [Test]
    public async Task LocalWorkflow_AppliesPatchOnlyAfterValidationAndIndependentReview()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "xe-development-e2e-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CreateRepositoryAsync(repositoryRoot);
            await Page.GotoAsync($"{NodeAppUrl}/development", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            await AcknowledgeDevelopmentConsentAsync();

            await Expect(Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions
            {
                Name = "Development Mode"
            })).ToBeVisibleAsync();

            // The project form no longer takes a free-text repository root: d88237b8 ("Enable repository-bound
            // Development Mode by default") replaced it with a Select over repositories registered up front, so
            // the repository has to be registered through the dialog before it can be picked.
            await RegisterRepositoryAsync(repositoryRoot);

            await Page.GetByLabel("Project objective").FillAsync("Exercise the complete local Development workflow");
            await Page.GetByLabel("Initial task title").FillAsync("Add the deterministic feature file");
            await Page.GetByLabel("Requirements").FillAsync("Create feature.txt with the approved deterministic content.");
            await Page.GetByLabel("Acceptance criteria (JSON)").FillAsync("[\"feature.txt contains the approved content\"]");
            await Page.GetByLabel("Coder model ID").FillAsync("qwen3.5:0.8b");
            await Page.GetByLabel("Reviewer model ID").FillAsync("qwen3.5:0.8b");
            // Target the checkbox by test id: the acknowledgement copy is reworded with the surface (it now reads
            // "I trust the selected repository to execute Development commands with my host-user permissions."),
            // and a label-text locator turns every such rewording into a 30 s timeout with no useful message.
            await Page.GetByTestId("development-trust-acknowledgement").CheckAsync();

            // Wait for the command-profile confirmation and go through it, rather than racing it.
            //
            // Create is gated on "(!detection || profileConfirmed)", so clicking before the detection query resolves
            // takes the !detection branch and succeeds without ever touching this panel. That is how this test passed
            // when it was first written against the profile work, and it made the pass timing-dependent: on a run
            // where detection lands first, Create is disabled and the click times out instead.
            //
            // Waiting on the panel also turns the fixture's expected profile into a positive assertion. This repository
            // is a README and nothing else, so detection must resolve it to the code-owned "generic-git" profile, whose
            // validation list is the whitespace check alone. If that ever silently became a dotnet profile, the run
            // below would fail against a repository that cannot build — but it would fail deep in validation with a
            // confusing message, rather than here with the actual reason.
            var profileConfirmation = Page.GetByTestId("development-profile-confirmation");
            await Expect(profileConfirmation).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 30_000
            });
            await Expect(Page.GetByTestId("development-profile-id")).ToContainTextAsync("generic-git");
            await Page.GetByTestId("development-profile-confirm").CheckAsync();

            await Page.GetByTestId("development-create-project").ClickAsync();

            var detail = Page.GetByTestId("development-project-detail");
            await Expect(detail).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });

            var nextAction = Page.GetByTestId("development-start-next");
            await nextAction.ClickAsync();
            await Expect(Page.GetByTestId("development-live-panel")).ToContainTextAsync("Development E2E live output",
                new LocatorAssertionsToContainTextOptions
                {
                    Timeout = 10_000
                });
            await Expect(detail.GetByText("InProgress", new LocatorGetByTextOptions
            {
                Exact = true
            }).First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 15_000
            });
            await Expect(Page.GetByTestId("development-apply-panel")).ToHaveCountAsync(0);

            await Expect(nextAction).ToHaveTextAsync("Run deterministic validation", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 10_000
            });
            await nextAction.ClickAsync();
            await Expect(nextAction).ToHaveTextAsync("Start independent review", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 15_000
            });
            await nextAction.ClickAsync();

            var applyPanel = Page.GetByTestId("development-apply-panel");
            await Expect(applyPanel).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 15_000
            });
            var applyButton = Page.GetByTestId("development-apply-patch");
            await Expect(applyButton).ToBeDisabledAsync();

            await Page.GetByTestId("development-preview-patch").ClickAsync();
            // Monaco applies the label to its empty native edit context, not the rendered diff.
            await Expect(Page.GetByTestId("development-patch-preview")).ToContainTextAsync("feature.txt", new LocatorAssertionsToContainTextOptions
            {
                Timeout = 10_000
            });
            await Expect(applyButton).ToBeEnabledAsync();
            await applyButton.ClickAsync();

            await Expect(detail.GetByText("Completed", new LocatorGetByTextOptions
            {
                Exact = true
            }).First).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });
            await Assert.That(await File.ReadAllTextAsync(Path.Combine(repositoryRoot, "feature.txt")))
                        .IsEqualTo("implemented by Development E2E\n");

            await Page.GotoAsync($"{NodeAppUrl}/chat", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });
            await Expect(Page.GetByPlaceholder("Type your message")).ToBeVisibleAsync();
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    /// <summary>
    ///     Verifies the reachability criterion through the browser: the executed/passed/failed counts must be
    ///     <em>visible to the operator in the attempt view</em>, not merely persisted.
    ///     <para>
    ///         The other test in this file drives a README-only repository, which detection resolves to
    ///         <c>generic-git</c> — a profile whose validation list is the whitespace check alone and which therefore
    ///         runs no test command and produces no counts to show. So it can never exercise this, and a criterion that
    ///         no browser test reaches is exactly the shape of the defect this test guards against — a panel that never rendered.
    ///     </para>
    ///     <para>
    ///         This one uses a real buildable .NET solution with a passing test, so detection resolves
    ///         <c>dotnet-slnx</c> and the gate runs restore, build and test for real. The coder writes
    ///         <c>feature.txt</c> exactly as it does for the other test — a file that is deliberately irrelevant to the
    ///         build, so the counts under assertion come from the repository's own suite rather than from anything the
    ///         fake model contrived.
    ///     </para>
    /// </summary>
    [Test]
    public async Task DotnetProfile_RendersExecutedPassedAndFailedCountsInTheAttemptView()
    {
        var repositoryRoot = Path.Combine(Path.GetTempPath(), "xe-development-e2e-dotnet-" + Guid.NewGuid().ToString("N"));
        try
        {
            await CreateDotnetRepositoryAsync(repositoryRoot);
            await Page.GotoAsync($"{NodeAppUrl}/development", new PageGotoOptions
            {
                WaitUntil = WaitUntilState.NetworkIdle
            });

            await AcknowledgeDevelopmentConsentAsync();

            await RegisterRepositoryAsync(repositoryRoot);

            await Page.GetByLabel("Project objective").FillAsync("Surface deterministic test counts in the attempt view");
            await Page.GetByLabel("Initial task title").FillAsync("Add the deterministic feature file");
            await Page.GetByLabel("Requirements").FillAsync("Create feature.txt without disturbing the existing suite.");
            await Page.GetByLabel("Acceptance criteria (JSON)").FillAsync("[\"the existing suite still passes\"]");
            await Page.GetByLabel("Coder model ID").FillAsync("qwen3.5:0.8b");
            await Page.GetByLabel("Reviewer model ID").FillAsync("qwen3.5:0.8b");
            await Page.GetByTestId("development-trust-acknowledgement").CheckAsync();

            // A positive assertion, not a wait: if detection ever stopped resolving a .slnx repository to the .NET
            // profile, this test would still go green against a gate that ran only the whitespace check — proving
            // nothing about counts while looking like it had.
            await Expect(Page.GetByTestId("development-profile-confirmation")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 30_000
            });
            await Expect(Page.GetByTestId("development-profile-id")).ToContainTextAsync("dotnet-slnx");
            await Page.GetByTestId("development-profile-confirm").CheckAsync();
            await Page.GetByTestId("development-create-project").ClickAsync();

            await Expect(Page.GetByTestId("development-project-detail")).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 10_000
            });

            var nextAction = Page.GetByTestId("development-start-next");
            await nextAction.ClickAsync();
            await Expect(nextAction).ToHaveTextAsync("Run deterministic validation", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = 30_000
            });
            await nextAction.ClickAsync();

            // Generous, because this validation genuinely restores, builds and tests a .NET solution inside the
            // sandbox rather than running one git command. Reaching the review action is the signal that all four
            // profile commands finished and the gate passed.
            await Expect(nextAction).ToHaveTextAsync("Start independent review", new LocatorAssertionsToHaveTextOptions
            {
                Timeout = ValidationTimeoutMilliseconds
            });

            await Page.GetByRole(AriaRole.Tab, new PageGetByRoleOptions
            {
                Name = "Validation"
            }).ClickAsync();

            // The criterion itself. The counts come from the fixture's single passing test, so they are exact:
            // one discovered, one executed, one passed, none failed.
            var counts = Page.GetByTestId("development-validation-test-counts");
            await Expect(counts).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions
            {
                Timeout = 30_000
            });
            // The VALUES, not just the labels. Asserting only that a counts grid rendered would pass against four
            // zeroes — the false green this assertion prevents. The fixture has exactly one
            // test and it passes, so every number here is exact.
            await Expect(Page.GetByTestId("development-validation-test-discovered")).ToHaveTextAsync("1");
            await Expect(Page.GetByTestId("development-validation-test-executed")).ToHaveTextAsync("1");
            await Expect(Page.GetByTestId("development-validation-test-passed")).ToHaveTextAsync("1");
            await Expect(Page.GetByTestId("development-validation-test-failed")).ToHaveTextAsync("0");

            // A parse failure renders instead of the counts, so its absence is part of the evidence that these
            // numbers were actually read rather than defaulted.
            await Expect(Page.GetByTestId("development-validation-test-parse-failure")).ToHaveCountAsync(0);
            await Expect(Page.GetByTestId("development-validation-no-tests")).ToHaveCountAsync(0);
            await Expect(Page.GetByTestId("development-validation-failure")).ToHaveCountAsync(0);
            await Expect(Page.GetByTestId("development-validation-result")).ToContainTextAsync("Validation passed");
        }
        finally
        {
            TryDeleteDirectory(repositoryRoot);
        }
    }

    /// <summary>
    ///     Restore, build and test of a small solution inside the sandbox, on a cold per-session NuGet directory.
    ///     Measured at roughly two seconds for the equivalent fixture in the backend suite; the headroom is for a
    ///     loaded machine and a cold SDK start, not for an expected duration.
    /// </summary>
    private const int ValidationTimeoutMilliseconds = 300_000;

    private static async Task CreateRepositoryAsync(string repositoryRoot)
    {
        Directory.CreateDirectory(repositoryRoot);
        await File.WriteAllTextAsync(Path.Combine(repositoryRoot, "README.md"), "Development E2E fixture\n");
        await RunGitAsync(repositoryRoot, "init", "--initial-branch=main");
        await RunGitAsync(repositoryRoot, "add", "README.md");
        await RunGitAsync(repositoryRoot,
            "-c",
            "user.name=Development E2E",
            "-c",
            "user.email=development-e2e@example.test",
            "commit",
            "-m",
            "initial fixture");
    }

    /// <summary>
    ///     Builds a throwaway Git repository that is a real, buildable .NET solution with one passing test, so
    ///     profile detection resolves <c>dotnet-slnx</c> and the gate has a genuine suite to count.
    ///     <para>
    ///         Deliberately a near-twin of <c>DevelopmentSyntheticSolutionRepository</c> in the backend suite rather
    ///         than a shared type: that class is <c>internal</c> to another test assembly, and the two fixtures want
    ///         opposite baselines. The backend one starts RED on purpose so a coder attempt can steer the outcome
    ///         three ways; this one starts GREEN, because the E2E coder writes an inert <c>feature.txt</c> and the
    ///         point here is the counts reaching the screen, not the gate's verdict logic.
    ///     </para>
    /// </summary>
    private static async Task CreateDotnetRepositoryAsync(string repositoryRoot)
    {
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests", "Probe"));

        var (packagesRoot, testFrameworkVersion) = ResolveTestFrameworkPackage();

        // Pin the same SDK band and MTP runner this repository uses: the code-owned .NET profiles pass
        // --max-parallel-test-modules 1, which a VSTest-mode `dotnet test` rejects outright.
        await WriteAsync(repositoryRoot,
            "global.json",
            """
            {
              "sdk": { "version": "10.0.100", "rollForward": "latestFeature", "allowPrerelease": false },
              "test": { "runner": "Microsoft.Testing.Platform" }
            }

            """);

        // Restore must succeed with no network: DevelopmentWorkspaceTools points NUGET_PACKAGES at a fresh
        // per-session directory, so the ambient cache is invisible. Clearing the sources and declaring the host
        // cache as a fallback folder resolves the graph from disk instead.
        await WriteAsync(repositoryRoot,
            "NuGet.config",
            $"""
             <?xml version="1.0" encoding="utf-8"?>
             <configuration>
               <packageSources>
                 <clear />
               </packageSources>
               <fallbackPackageFolders>
                 <clear />
                 <add key="host" value="{packagesRoot}" />
               </fallbackPackageFolders>
             </configuration>

             """);

        // Build output must be ignored: the patch is exported with `git add -A`, so an un-ignored bin/ or obj/
        // produced by validation would change the subject hash between validation and review and block apply for a
        // reason unrelated to the change.
        await WriteAsync(repositoryRoot,
            ".gitignore",
            """
            bin/
            obj/
            TestResults/

            """);

        await WriteAsync(repositoryRoot,
            "SyntheticFixture.slnx",
            """
            <Solution>
                <Project Path="src/Lib/Lib.csproj"/>
                <Project Path="tests/Probe/Probe.csproj"/>
            </Solution>

            """);

        await WriteAsync(repositoryRoot,
            "src/Lib/Lib.csproj",
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>

            """);

        await WriteAsync(repositoryRoot,
            "src/Lib/Feature.cs",
            """
            namespace Lib;

            public static class Feature
            {
                public static string Value() => "base";
            }

            """);

        await WriteAsync(repositoryRoot,
            "tests/Probe/Probe.csproj",
            $"""
             <Project Sdk="Microsoft.NET.Sdk">
               <PropertyGroup>
                 <TargetFramework>net10.0</TargetFramework>
                 <OutputType>Exe</OutputType>
                 <Nullable>enable</Nullable>
                 <ImplicitUsings>enable</ImplicitUsings>
               </PropertyGroup>
               <ItemGroup>
                 <PackageReference Include="TUnit" Version="{testFrameworkVersion}" />
                 <ProjectReference Include="../../src/Lib/Lib.csproj"/>
               </ItemGroup>
             </Project>

             """);

        // Exactly one test, and it passes at HEAD. The assertion in the test above depends on that count.
        await WriteAsync(repositoryRoot,
            "tests/Probe/FeatureTests.cs",
            """
            namespace Probe;

            public sealed class FeatureTests
            {
                [Test]
                public async Task Value_ReturnsTheApprovedContent() =>
                    await Assert.That(Lib.Feature.Value()).IsEqualTo("base");
            }

            """);

        await RunGitAsync(repositoryRoot, "init", "--initial-branch=main");
        await RunGitAsync(repositoryRoot, "add", "-A", "--", ".");
        await RunGitAsync(repositoryRoot,
            "-c",
            "user.name=Development E2E",
            "-c",
            "user.email=development-e2e@example.test",
            "commit",
            "-m",
            "synthetic dotnet fixture");
    }

    /// <summary>
    ///     The NuGet cache and TUnit version to restore the fixture from, both taken from this test host — the version
    ///     is the one this assembly runs on and the cache is the one it was restored into, so the pair is present on
    ///     any machine that could have built this project.
    /// </summary>
    private static (string PackagesRoot, string Version) ResolveTestFrameworkPackage()
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrWhiteSpace(packagesRoot))
        {
            packagesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        }

        packagesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packagesRoot));
        var packageDirectory = Path.Combine(packagesRoot, "tunit");
        if (!Directory.Exists(packageDirectory))
        {
            throw new InvalidOperationException($"The NuGet package cache '{packagesRoot}' has no restored TUnit package, so the synthetic Development solution cannot be restored offline.");
        }

        var loaded = typeof(TestAttribute).Assembly
                                          .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                                          .InformationalVersion?
                                          .Split('+')[0];
        if (!string.IsNullOrWhiteSpace(loaded) && Directory.Exists(Path.Combine(packageDirectory, loaded)))
        {
            return (packagesRoot, loaded);
        }

        var newest = Directory.EnumerateDirectories(packageDirectory)
                              .Select(Path.GetFileName)
                              .OfType<string>()
                              .OrderByDescending(static name => name, StringComparer.OrdinalIgnoreCase)
                              .FirstOrDefault()
                     ?? throw new InvalidOperationException($"The NuGet package cache '{packagesRoot}' has a TUnit package directory with no restored version in it.");
        return (packagesRoot, newest);
    }

    private static async Task WriteAsync(string repositoryRoot, string relativePath, string content)
    {
        var path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
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
        await process.WaitForExitAsync();
        var output = await standardOutput;
        var error = await standardError;
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
