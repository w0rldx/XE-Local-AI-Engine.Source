namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The cutover guard for D14: frozen launch intent is immutable, so work frozen under a different launch-identity
///     scheme is failed before it launches rather than executed and compared across schemes.
/// </summary>
/// <remarks>
///     A run freezes its intended identity at enqueue and writes its effective identity at execution, so an upgrade
///     that changes the identity scheme leaves queued work with two hashes that cannot be compared — which the compare
///     UI would otherwise render as launch drift on a launch that in fact matched. Failing the row removes that at its
///     root: it never runs, so it never writes an effective identity.
/// </remarks>
internal static class BenchmarkLaunchIdentityScheme
{
    /// <summary>The stable token a drained row records. Matched by tests and support; never localized.</summary>
    public const string SupersededReason = "launch-identity-scheme-superseded";

    public const string SupersededMessage =
        "This work was frozen under an older launch-identity scheme and cannot be compared against a launch from "
        + "this build. Re-queue it. (" + SupersededReason + ")";

    /// <summary>
    ///     Throws <see cref="BenchmarkExecutionException" /> when <paramref name="intent" /> was frozen under any
    ///     scheme other than this build's. The comparison is <c>!=</c>, not "older than": a build that finds work from
    ///     the FUTURE — a downgrade that left scheme-2 rows queued — must refuse it just as firmly.
    /// </summary>
    public static void RequireCurrent(BenchmarkRunLaunchIntent? intent)
    {
        // No recorded intent at all (a row created before launch evidence existed) has nothing to compare, so nothing
        // straddles. A recorded intent with a NULL scheme is a pre-slice freeze, i.e. scheme 1.
        if (intent is null)
        {
            return;
        }

        if ((intent.LaunchIdentityScheme ?? 1) != LlamaServerLaunchProjection.IdentitySchemeVersion)
        {
            throw new BenchmarkExecutionException(SupersededMessage);
        }
    }
}
