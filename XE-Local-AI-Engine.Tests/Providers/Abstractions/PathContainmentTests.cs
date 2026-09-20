namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The containment rule every destructive path gate shares: the stale server reapers KILL on it, the
///     sandbox orphan reaper DELETES. A false positive destroys something the node does not own, so each
///     case below must stay impossible.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class PathContainmentTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "xe-path-containment", "llama.cpp");

    [Test]
    public void IsUnderRoot_ForADescendant_IsTrue()
    {
        AssertEx.True(PathContainment.IsUnderRoot(Path.Combine(Root, "build", "bin", "llama-server"), Root));
    }

    /// <summary>
    ///     The trailing-separator guard, and the reason it exists: without it the root's own name is merely a string
    ///     prefix of every sibling that starts with it, and the reaper would kill a process out of <c>llama.cpp-other</c>.
    /// </summary>
    [Test]
    public void IsUnderRoot_ForASiblingWhoseNameStartsWithTheRoots_IsFalse()
    {
        AssertEx.False(PathContainment.IsUnderRoot(Root + "-other" + Path.DirectorySeparatorChar + "llama-server", Root));
    }

    [Test]
    public void IsUnderRoot_ForTheRootItself_IsFalse()
    {
        AssertEx.False(PathContainment.IsUnderRoot(Root, Root), "The root is not a descendant of itself; deleting it is never what a caller asked for.");
        AssertEx.False(PathContainment.IsUnderRoot(Root + Path.DirectorySeparatorChar, Root));
    }

    /// <summary>A root spelled with or without its trailing separator is the same root.</summary>
    [Test]
    public void IsUnderRoot_IgnoresATrailingSeparatorOnTheRoot()
    {
        var child = Path.Combine(Root, "llama-server");

        AssertEx.Equal(PathContainment.IsUnderRoot(child, Root), PathContainment.IsUnderRoot(child, Root + Path.DirectorySeparatorChar));
        AssertEx.True(PathContainment.IsUnderRoot(child, Root + Path.DirectorySeparatorChar));
    }

    /// <summary>
    ///     Normalization happens before the comparison, so <c>..</c> cannot smuggle a path out of the root behind a
    ///     prefix that still reads as contained.
    /// </summary>
    [Test]
    public void IsUnderRoot_ResolvesRelativeSegmentsBeforeComparing()
    {
        AssertEx.False(PathContainment.IsUnderRoot(Path.Combine(Root, "..", "elsewhere", "llama-server"), Root),
            "A path that walks out of the root with '..' is outside it, however it is spelled.");
        AssertEx.True(PathContainment.IsUnderRoot(Path.Combine(Root, "build", "..", "bin", "llama-server"), Root),
            "A path that walks back inside the root is inside it.");
    }

    [Test]
    [Arguments("")]
    [Arguments("\0invalid")]
    public void IsUnderRoot_ForAPathThePlatformCannotParse_IsFalseRatherThanThrowing(string path)
    {
        AssertEx.False(PathContainment.IsUnderRoot(path, Root), "A path that cannot be parsed can never be one we own, and must not throw out of the sweep that asked.");
    }

    /// <summary>
    ///     Whitespace-only input is NOT a parse failure, and this test exists so nobody files it as one: on Unix
    ///     it is a legal file name, so the answer is about LOCATION, not refusal. Windows trims it and refuses,
    ///     which the case above already covers.
    /// </summary>
    [Test]
    [ExcludeOn(OS.Windows)]
    public void IsUnderRoot_ForAWhitespaceOnlyPath_IsJudgedLikeAnyOtherRelativePath()
    {
        AssertEx.True(PathContainment.IsUnderRoot("   ", Directory.GetCurrentDirectory()),
            "GetFullPath resolved the whitespace name against the working directory rather than refusing it, so it IS under that directory.");
        AssertEx.False(PathContainment.IsUnderRoot("   ", Root),
            "…and the working directory is not under the root, which is the only reason the answer is false.");
    }

    [Test]
    public void IsUnderRoot_ForARootThePlatformCannotParse_IsFalseRatherThanThrowing()
    {
        AssertEx.False(PathContainment.IsUnderRoot(Path.Combine(Root, "llama-server"), "\0invalid"));
    }

    /// <summary>
    ///     The ROOT is normalized too, not just the candidate. Every call site hands in a full path today, so this
    ///     guards the future one that does not: a relative root names the directory it resolves to, not its
    ///     spelling.
    /// </summary>
    [Test]
    public void IsUnderRoot_NormalizesTheRootBeforeComparing()
    {
        var relativeRoot = Path.GetRelativePath(Directory.GetCurrentDirectory(), Root);
        AssertEx.True(PathContainment.IsUnderRoot(Path.Combine(Root, "llama-server"), relativeRoot),
            "A relative root names the same directory once resolved, so a child of it is still contained.");

        var rootViaParent = Path.Combine(Root, "build", "..");
        AssertEx.True(PathContainment.IsUnderRoot(Path.Combine(Root, "llama-server"), rootViaParent),
            "A root spelled with a '..' segment resolves back to the same directory.");
        AssertEx.False(PathContainment.IsUnderRoot(Root, rootViaParent),
            "…and it is still not its own descendant after that resolution.");
    }

    /// <summary>
    ///     Case is compared the way the platform's paths work: insensitively on Windows, ordinally everywhere else.
    ///     Both answers are asserted here rather than skipped, because each is correct on the host that produces it.
    /// </summary>
    [Test]
    public void IsUnderRoot_ComparesCaseTheWayThePlatformDoes()
    {
        var child = Path.Combine(Root.ToUpperInvariant(), "llama-server");

        AssertEx.Equal(OperatingSystem.IsWindows(), PathContainment.IsUnderRoot(child, Root));
    }
}
