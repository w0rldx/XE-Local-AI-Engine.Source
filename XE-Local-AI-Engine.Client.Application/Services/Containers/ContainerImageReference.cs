namespace XE_Local_AI_Engine.Client.Services.Containers;

using System.Text.RegularExpressions;

/// <summary>
///     The one definition of "this reference names bytes rather than a name", asked by every container runtime
///     before it creates anything.
///     <para>
///         It exists as a shared helper rather than as a copy in each runtime because there are two runtimes — the
///         production Docker client and the in-memory fake the tests stand on — and a fake that accepted what the
///         daemon path refuses, or refused what it accepts, would let a suite prove a guard that does not exist.
///     </para>
/// </summary>
internal static partial class ContainerImageReference
{
    /// <summary>
    ///     Whether <paramref name="image" /> is pinned as <c>&lt;reference&gt;@sha256:&lt;64 lowercase hex&gt;</c> —
    ///     the form the catalog ships and the only one a registry can serve reproducibly.
    ///     <para>
    ///         This is the repository's one digest-pin pattern. <c>ExternalAppCatalogValidator.IsDigestPinnedImage</c>
    ///         delegates to it rather than carrying a second copy, and the direction is that way round because the
    ///         container layer must not depend on External Apps.
    ///     </para>
    /// </summary>
    internal static bool IsDigestPinned(string? image)
    {
        return image is not null && DigestPinnedImageRegex().IsMatch(image);
    }

    /// <summary>
    ///     Whether <paramref name="image" /> names its own content. Two forms qualify: a digest-pinned reference
    ///     (<see cref="IsDigestPinned" />), and a bare image id (<c>sha256:&lt;64 lowercase hex&gt;</c>), which is
    ///     what a daemon reports for an image built locally on a store that records no <c>RepoDigests</c>. A tag
    ///     qualifies as neither: it names whatever the registry last pushed rather than the bytes anyone approved.
    ///     <para>
    ///         Both halves are whole-string matches. A <c>Contains("@sha256:")</c> test would admit
    ///         <c>x@sha256:</c> and <c>x@sha256:zz</c> — references that name no bytes at all, which is the failure
    ///         the guard exists to prevent rather than a looser spelling of it.
    ///     </para>
    /// </summary>
    internal static bool IsContentAddressed(string image)
    {
        return !string.IsNullOrEmpty(image) && (IsDigestPinned(image) || BareImageIdRegex().IsMatch(image));
    }

    // \A…\z rather than ^…$ on both: '$' also matches immediately before a trailing line feed, so the
    // anchored-looking "^…$" would accept "<ref>@sha256:<64 hex>\n" as a whole-string match.
    [GeneratedRegex(@"\A[^\s@]+@sha256:[0-9a-f]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigestPinnedImageRegex();

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex BareImageIdRegex();
}
