namespace XE_Local_AI_Engine.Tests.Development;

using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The dependency-manifest policy: the gate that makes a no-egress agent sandbox workable, because the package
///     cache is warmed from the base commit's manifests and an attempt that changed one would need a resolve the
///     sandbox can no longer perform.
///     <para>
///         The load-bearing assertion in this file is the SHAPE of the failure. It is a verdict, so the task returns to
///         <c>InProgress</c> carrying a reason the agent can act on; the sibling test-write policy throws, because
///         "delete the failing test" has no legitimate reading and "add a package" does.
///     </para>
/// </summary>
public sealed class DevelopmentDependencyManifestPolicyTests
{
    /// <summary>
    ///     Every rule in the set, exercised through the same normalization the policy applies. The set IS the control:
    ///     a packaging system missing from it is a hole, so each entry is asserted individually rather than by
    ///     enumerating the array — an enumeration would pass just as happily against an empty set.
    /// </summary>
    [Test]
    public void Evaluate_MatchesEveryDependencyManifestRuleAndNothingElse()
    {
        foreach (var path in new[]
                 {
                     "src/Lib/Lib.csproj",
                     "Directory.Packages.props",
                     "src/Directory.Packages.props",
                     "Directory.Build.props",
                     "src/Directory.Build.targets",
                     "packages.lock.json",
                     "NuGet.config",
                     "nuget.config",
                     "web/package.json",
                     "web/package-lock.json",
                     "web/pnpm-lock.yaml",
                     "web/yarn.lock",
                     "rust/Cargo.toml",
                     "rust/Cargo.lock",
                     "requirements.txt",
                     "requirements-dev.txt",
                     "pyproject.toml",
                     "uv.lock",
                     "poetry.lock"
                 })
        {
            AssertEx.True(DevelopmentDependencyManifestPolicy.IsDependencyManifest(path), path);
        }

        foreach (var path in new[]
                 {
                     "src/Lib/Feature.cs",
                     "tests/Probe/FeatureTests.cs",
                     "README.md",
                     "web/src/index.ts",
                     "docs/packages.md",
                     "src/Lib/Lib.csproj.user"
                 })
        {
            AssertEx.False(DevelopmentDependencyManifestPolicy.IsDependencyManifest(path), path);
        }

        AssertEx.False(DevelopmentDependencyManifestPolicy.IsDependencyManifest(null));
        AssertEx.False(DevelopmentDependencyManifestPolicy.IsDependencyManifest("   "));
    }

    /// <summary>
    ///     A source-only change passes, and the verdict carries the code and names the offending path — the detail is
    ///     what the operator and the retrying agent both read, so an unnamed path would make the failure unactionable.
    /// </summary>
    [Test]
    public void Evaluate_PassesASourceChangeAndNamesTheManifestItRejects()
    {
        AssertEx.Null(DevelopmentDependencyManifestPolicy.Evaluate(Evidence(new DevelopmentChangedFile("src/Lib/Feature.cs", "modified"),
            new DevelopmentChangedFile("tests/Probe/NewFeatureTests.cs", "added"))));

        var verdict = AssertEx.NotNull(DevelopmentDependencyManifestPolicy.Evaluate(Evidence(new DevelopmentChangedFile("src/Lib/Feature.cs", "modified"),
            new DevelopmentChangedFile("src/Lib/Lib.csproj", "modified"))));
        AssertEx.False(verdict.Passed);
        AssertEx.Equal(DevelopmentValidationFailureCodes.DependencyManifestChanged, verdict.FailureCode);
        AssertEx.Contains(AssertEx.NotNull(verdict.FailureDetail), "src/Lib/Lib.csproj", StringComparison.Ordinal);
    }

    /// <summary>
    ///     ADDED counts. A repository with no <c>Directory.Packages.props</c> gains central package management the
    ///     moment one appears, which changes resolution for the whole tree — so "the file did not exist at the base
    ///     commit" is not an exemption, it is the most disruptive case in the set.
    /// </summary>
    [Test]
    public void Evaluate_RejectsAnAddedManifestAndARenameOutOfTheSet()
    {
        var added = AssertEx.NotNull(DevelopmentDependencyManifestPolicy.Evaluate(Evidence(new DevelopmentChangedFile("Directory.Packages.props", "added"))));
        AssertEx.Equal(DevelopmentValidationFailureCodes.DependencyManifestChanged, added.FailureCode);

        // The new path is innocuous; the PREVIOUS one is a manifest, and moving a manifest aside changes resolution
        // exactly as much as editing it.
        var renamed = AssertEx.NotNull(DevelopmentDependencyManifestPolicy.Evaluate(Evidence(new DevelopmentChangedFile("src/Lib/Lib.csproj.bak",
            "renamed",
            "src/Lib/Lib.csproj"))));
        AssertEx.Equal(DevelopmentValidationFailureCodes.DependencyManifestChanged, renamed.FailureCode);
        AssertEx.Contains(AssertEx.NotNull(renamed.FailureDetail), "src/Lib/Lib.csproj", StringComparison.Ordinal);

        var deleted = AssertEx.NotNull(DevelopmentDependencyManifestPolicy.Evaluate(Evidence(new DevelopmentChangedFile("packages.lock.json", "deleted"))));
        AssertEx.Equal(DevelopmentValidationFailureCodes.DependencyManifestChanged, deleted.FailureCode);
    }

    /// <summary>
    ///     The detail reaches <c>development_tasks.terminal_reason</c>, which is 1024 characters wide, and an attempt
    ///     may legally change up to <c>MaxChangedFiles</c> paths — so the listing has to be bounded rather than
    ///     complete.
    /// </summary>
    [Test]
    public void Evaluate_BoundsTheOffendingPathListing()
    {
        var many = Enumerable.Range(0, 32)
                             .Select(index => new DevelopmentChangedFile($"src/Project{index}/Project{index}.csproj", "modified"))
                             .ToArray();

        var verdict = AssertEx.NotNull(DevelopmentDependencyManifestPolicy.Evaluate(Evidence(many)));
        var detail = AssertEx.NotNull(verdict.FailureDetail);
        AssertEx.Contains(detail, "32 dependency manifests", StringComparison.Ordinal);
        AssertEx.Contains(detail, "…", StringComparison.Ordinal);
        AssertEx.True(detail.Length < 1024, detail);
    }

    /// <summary>
    ///     The set is versioned with the profile catalog rather than by a mechanism of its own, which is only true
    ///     while it actually lives there. Asserting the reference pins that: moving the array into the policy class
    ///     would silently detach the control from the version a project was created under.
    /// </summary>
    [Test]
    public void DependencyManifestPaths_AreOwnedByTheVersionedProfileCatalog()
    {
        AssertEx.NotEmpty(DevelopmentCommandProfileCatalog.DependencyManifestPaths);
        AssertEx.Contains(DevelopmentCommandProfileCatalog.DependencyManifestPaths, "**/Directory.Build.props");
        AssertEx.Contains(DevelopmentCommandProfileCatalog.DependencyManifestPaths, "**/Directory.Build.targets");
    }

    private static DevelopmentPatchEvidence Evidence(params DevelopmentChangedFile[] changedFiles) =>
        new("0000000000000000000000000000000000000000",
            PatchHash: "patch",
            ManifestHash: "manifest",
            SubjectHash: "subject",
            ExpectedResultHash: "expected",
            PatchBytes: [],
            ManifestBytes: [],
            changedFiles);
}
