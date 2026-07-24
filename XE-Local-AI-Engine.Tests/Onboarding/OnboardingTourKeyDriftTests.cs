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
///     <para>
///         BOTH sides are read from source. This project deliberately does not reference the E2E project (that would
///         drag Playwright and the whole browser-test graph into the unit suite), so the C# constants are text-walked
///         out of <c>XENodeE2EWebApplicationFactory.cs</c> exactly as the TypeScript is text-walked out of
///         <c>useTourState.ts</c> — the technique <c>AppUpdateAuthStateWireTests</c> already uses. Restating the key as
///         a literal in this file instead would compare React against a THIRD copy: editing only the factory constant
///         would leave React and the literal agreeing, the test green, and the fixture seeding a key nobody reads —
///         precisely the drift this exists to catch.
///     </para>
///     <para>
///         Not coupled here: <c>TutorialStateEndpointTests</c> also spells "main-app-v1", but as an arbitrary sample
///         key exercising a generic key/value round-trip. That endpoint is agnostic to the key, so it carries no
///         cross-language contract and pinning it would only add a false constraint.
///     </para>
/// </summary>
public sealed class OnboardingTourKeyDriftTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task ReactTourKey_MatchesTheKeyTheE2EFixtureSeeds()
    {
        var reactKey = Capture(await ReadReactTourStateSourceAsync(),
            """export const MAIN_APP_TOUR_KEY\s*=\s*"(?<value>[^"]+)"\s*;""",
            "the MAIN_APP_TOUR_KEY export in useTourState.ts");

        var factoryKey = Capture(await ReadE2EFactorySourceAsync(),
            """public const string MainAppTourKey\s*=\s*"(?<value>[^"]+)"\s*;""",
            "the MainAppTourKey constant in XENodeE2EWebApplicationFactory.cs");

        AssertEx.NotNullOrEmpty(reactKey);
        AssertEx.Equal(reactKey,
            factoryKey,
            "MAIN_APP_TOUR_KEY (useTourState.ts) and XENodeE2EWebApplicationFactory.MainAppTourKey have drifted. The "
            + "E2E fixture would seed a tour key React never reads, so the first-run welcome modal would open over "
            + "every browser test and fail them on intercepted clicks. Update the C# constant to match.");
    }

    /// <summary>
    ///     The storage key is doubly exposed: React DERIVES it from the tour key while C# spells the prefix out in its
    ///     own interpolation. Rebuilding each side from its own two literals and comparing the results catches a
    ///     changed PREFIX as well as a changed key — the case comparing tour keys alone would miss.
    /// </summary>
    [Test]
    public async Task ReactProgressStorageKey_MatchesTheKeyTheTourTestReads()
    {
        var reactSource = await ReadReactTourStateSourceAsync();
        var reactKey = Capture(reactSource,
            """export const MAIN_APP_TOUR_KEY\s*=\s*"(?<value>[^"]+)"\s*;""",
            "the MAIN_APP_TOUR_KEY export in useTourState.ts");
        var reactTemplate = Capture(reactSource,
            """export const TOUR_PROGRESS_STORAGE_KEY\s*=\s*`(?<value>[^`]+)`\s*;""",
            "the TOUR_PROGRESS_STORAGE_KEY export in useTourState.ts");

        var factorySource = await ReadE2EFactorySourceAsync();
        var factoryKey = Capture(factorySource,
            """public const string MainAppTourKey\s*=\s*"(?<value>[^"]+)"\s*;""",
            "the MainAppTourKey constant in XENodeE2EWebApplicationFactory.cs");
        var factoryTemplate = Capture(factorySource,
            """public const string TourProgressStorageKey\s*=\s*\$"(?<value>[^"]+)"\s*;""",
            "the TourProgressStorageKey constant in XENodeE2EWebApplicationFactory.cs");

        // Each side must still interpolate its own tour key; a flattened copy would silently defeat the comparison
        // below by making both sides constant strings that happen to agree today.
        AssertEx.Contains(reactTemplate,
            "${MAIN_APP_TOUR_KEY}",
            StringComparison.Ordinal,
            "TOUR_PROGRESS_STORAGE_KEY no longer interpolates MAIN_APP_TOUR_KEY; keep it derived so the tour key has one home per language");
        AssertEx.Contains(factoryTemplate,
            "{MainAppTourKey}",
            StringComparison.Ordinal,
            "XENodeE2EWebApplicationFactory.TourProgressStorageKey no longer interpolates MainAppTourKey; keep it derived from the tour key");

        var reactStorageKey = reactTemplate.Replace("${MAIN_APP_TOUR_KEY}", reactKey, StringComparison.Ordinal);
        var factoryStorageKey = factoryTemplate.Replace("{MainAppTourKey}", factoryKey, StringComparison.Ordinal);

        AssertEx.Equal(reactStorageKey,
            factoryStorageKey,
            "the React tour progress storage key and XENodeE2EWebApplicationFactory.TourProgressStorageKey have "
            + "drifted. OnboardingTourE2ETests reads that localStorage entry to prove the tour advanced a step, so it "
            + "would assert against a key the app never writes.");
    }

    /// <summary>
    ///     Returns the <c>value</c> group of the first match, failing with the anchor spelled out when the pattern
    ///     does not match. Without this a restructured declaration would leave the regex matching nothing and the
    ///     test passing green on an assertion it never actually made.
    /// </summary>
    private static string Capture(string source, string pattern, string description)
    {
        var match = Regex.Match(source, pattern, RegexOptions.None, RegexTimeout);

        AssertEx.True(match.Success,
            $"could not find {description} — if it was renamed or restructured, re-point this guard at its new shape "
            + "rather than deleting the test");

        return match.Groups["value"].Value;
    }

    private static Task<string> ReadReactTourStateSourceAsync()
    {
        return File.ReadAllTextAsync(ResolveRepositoryFile("XE-Local-AI-Engine.Client.React",
            "src",
            "features",
            "onboarding",
            "hooks",
            "useTourState.ts"));
    }

    private static Task<string> ReadE2EFactorySourceAsync()
    {
        return File.ReadAllTextAsync(ResolveRepositoryFile("XE-Local-AI-Engine.Tests.E2ETests",
            "Infrastructure",
            "XENodeE2EWebApplicationFactory.cs"));
    }

    // Walks up from the test binary to the repo root, matching AppUpdateAuthStateWireTests / AppUpdateContractTests.
    private static string ResolveRepositoryFile(params string[] relativeSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativeSegments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(relativeSegments)} by walking up from {AppContext.BaseDirectory}.");
    }
}
