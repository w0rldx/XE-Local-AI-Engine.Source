namespace XE_Local_AI_Engine.Tests.AppUpdate;

using XE_Local_AI_Engine.Client.Endpoints.AppUpdate.V1;
using XE_Local_AI_Engine.Client.Services.AppUpdate;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the <c>authState</c> wire vocabulary. This contract is unusually exposed: <c>authState</c> is a plain
///     <see cref="string" /> on the DTO rather than an OpenAPI enum, which is exactly why a new state needs no client
///     regeneration — and equally why NOTHING generated will notice if the two sides drift. These tests are that check.
/// </summary>
public sealed class AppUpdateAuthStateWireTests
{
    // The literal TypeScript union React narrows on. Kept here so a backend state added without teaching React about it
    // fails a test instead of reaching a user as an unhandled state that silently renders nothing.
    private const string ReactUnionDeclaration = "export type AuthState =";

    [Test]
    [Arguments(AppUpdateAuthState.SignedOut, "signedOut")]
    [Arguments(AppUpdateAuthState.SignedIn, "signedIn")]
    [Arguments(AppUpdateAuthState.ReauthRequired, "reauthRequired")]
    [Arguments(AppUpdateAuthState.NoAccess, "noAccess")]
    [Arguments(AppUpdateAuthState.NotConfigured, "notConfigured")]
    public void Of_MapsEveryStateToItsDocumentedWireString(AppUpdateAuthState state, string expected)
    {
        AssertEx.Equal(expected, AppUpdateAuthStateWire.Of(state));
    }

    /// <summary>
    ///     The mapping falls through to <c>signedOut</c> on an unrecognized value, so a state added to the enum but not
    ///     to the switch would silently masquerade as signed-out — which for <c>NotConfigured</c> is precisely the bug
    ///     the state exists to fix. Distinctness across all declared values is what catches that.
    /// </summary>
    [Test]
    public void Of_ProducesADistinctWireStringForEveryDeclaredState()
    {
        var states = Enum.GetValues<AppUpdateAuthState>();
        var wireStrings = states.Select(AppUpdateAuthStateWire.Of).ToArray();

        AssertEx.Equal(states.Length,
            wireStrings.Distinct(StringComparer.Ordinal).Count(),
            $"every AppUpdateAuthState needs its own wire string; got [{string.Join(", ", wireStrings)}]");
    }

    /// <summary>
    ///     Cross-language drift guard: every wire string the backend can emit must appear in the React
    ///     <c>AuthState</c> union. Nothing else enforces this — the generated client types <c>authState</c> as a bare
    ///     string, so an unknown value would sail through hey-api and zod and simply fail to match any branch.
    /// </summary>
    [Test]
    public async Task EveryWireString_IsDeclaredInTheReactAuthStateUnion()
    {
        var source = await File.ReadAllTextAsync(GetReactQuerySourcePath());
        var unionIndex = source.IndexOf(ReactUnionDeclaration, StringComparison.Ordinal);
        AssertEx.True(unionIndex >= 0, $"could not find '{ReactUnionDeclaration}' in useAppUpdate.ts");

        var unionEnd = source.IndexOf(';', unionIndex);
        AssertEx.True(unionEnd > unionIndex, "the AuthState union declaration is not terminated");
        var union = source[unionIndex..unionEnd];

        foreach (var state in Enum.GetValues<AppUpdateAuthState>())
        {
            var wire = AppUpdateAuthStateWire.Of(state);
            AssertEx.Contains(union,
                $"\"{wire}\"",
                StringComparison.Ordinal,
                $"React's AuthState union is missing '{wire}' ({state}); add it to useAppUpdate.ts and handle it in AppUpdateSection.tsx");
        }
    }

    // Walks up from the test binary to the repo root, matching the source-locating approach in AppUpdateContractTests.
    private static string GetReactQuerySourcePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName,
                "XE-Local-AI-Engine.Client.React",
                "src",
                "features",
                "app-update",
                "queries",
                "useAppUpdate.ts");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate useAppUpdate.ts.");
    }
}
