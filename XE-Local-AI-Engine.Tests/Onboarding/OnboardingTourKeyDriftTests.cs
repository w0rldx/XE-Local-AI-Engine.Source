namespace XE_Local_AI_Engine.Tests.Onboarding;

using System.Text.RegularExpressions;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Cross-language drift guard for the onboarding tour key.
///     <para>
///         The E2E fixture suppresses the first-run welcome prompt by seeding tutorial state under a tour key it
///         hard-codes in C#, while the only real definition lives in React. If the two drift, the fixture seeds a key
///         nobody reads, the welcome modal returns, and it intercepts pointer events across most of the browser suite —
///         producing dozens of "element intercepts pointer events" click timeouts that look nothing like a renamed
///         constant. That exact failure cost a full remediation lane once; this test is what stops it recurring.
///     </para>
///     <para>
///         Lives here rather than in the E2E project on purpose: the E2E csproj demotes itself to a plain library
///         unless <c>-p:RunE2ETests=true</c> is passed, so a guard placed there would only run in the ask-gated lane —
///         the one lane a drifting key would break.
///     </para>
/// </summary>
public sealed class OnboardingTourKeyDriftTests
{
    // Mirrors XENodeE2EWebApplicationFactory.MainAppTourKey. Duplicated rather than referenced because this project
    // does not reference the E2E project; the test below pins BOTH copies to the React source, so neither can drift.
    private const string ExpectedTourKey = "main-app-v1";

    private const string ExpectedProgressStorageKey = "xe-onboarding-main-app-v1-step";

    [Test]
    public async Task ReactTourKey_MatchesTheKeyTheE2EFixtureSeeds()
    {
        var source = await File.ReadAllTextAsync(GetTourStateSourcePath());

        var match = Regex.Match(source,
            """export const MAIN_APP_TOUR_KEY\s*=\s*"(?<key>[^"]+)"\s*;""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        AssertEx.True(match.Success,
            "could not find the MAIN_APP_TOUR_KEY export in useTourState.ts — if it was renamed or restructured, update this guard rather than deleting it");

        AssertEx.Equal(ExpectedTourKey,
            match.Groups["key"].Value,
            "the React tour key changed; update XENodeE2EWebApplicationFactory.MainAppTourKey (and TutorialStateEndpointTests) to match, or the E2E fixture will stop suppressing the welcome prompt");
    }

    [Test]
    public async Task ReactProgressStorageKey_MatchesTheKeyTheTourTestReads()
    {
        var source = await File.ReadAllTextAsync(GetTourStateSourcePath());

        // Declared in TypeScript as a template literal derived from MAIN_APP_TOUR_KEY, but the E2E test reads it out of
        // localStorage as a flat string — so the derivation has to be re-applied here to compare them.
        var match = Regex.Match(source,
            """export const TOUR_PROGRESS_STORAGE_KEY\s*=\s*`(?<template>[^`]+)`\s*;""",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        AssertEx.True(match.Success,
            "could not find the TOUR_PROGRESS_STORAGE_KEY export in useTourState.ts — if it was renamed or restructured, update this guard rather than deleting it");

        var resolved = match.Groups["template"].Value.Replace("${MAIN_APP_TOUR_KEY}", ExpectedTourKey, StringComparison.Ordinal);

        AssertEx.Equal(ExpectedProgressStorageKey,
            resolved,
            "the React tour progress storage key changed; update OnboardingTourE2ETests to match, or its step-advance assertion will read an key that is never written");
    }

    // Walks up from the test binary to the repo root, matching AppUpdateAuthStateWireTests / AppUpdateContractTests.
    private static string GetTourStateSourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "XE-Local-AI-Engine.Client.React",
                "src",
                "features",
                "onboarding",
                "hooks",
                "useTourState.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate useTourState.ts.");
    }
}
