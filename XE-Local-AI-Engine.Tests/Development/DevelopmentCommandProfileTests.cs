namespace XE_Local_AI_Engine.Tests.Development;

using System.Text;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the per-project command profile: the protected-path policy the write gate enforces, the canonical
///     digest the artifacts are stamped with, the reuse rule that rejects a drifted stored profile, and the detector
///     that proposes one at registration.
/// </summary>
public sealed class DevelopmentCommandProfileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-command-profile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }

    /// <summary>
    ///     The protected set is asserted against real paths from the three layouts it was measured on, because the
    ///     patterns are not interchangeable. <c>**&#47;*.Tests&#47;**</c> alone protects nothing in XE-Framework
    ///     (whose projects are <c>XeFramework.Tests.UnitTests</c> and siblings) and nothing in this repository's own
    ///     <c>XE-Local-AI-Engine.Tests.E2ETests</c>, so a change that collapsed the set to that one pattern would
    ///     leave the write gate wide open while still looking like it protects tests.
    /// </summary>
    [Test]
    public void ProtectedPaths_CoverEveryMeasuredTestLayout()
    {
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        string[] protectedPaths =
        [
            "XE-Local-AI-Engine.Tests/Development/Foo Tests.cs",
            "XE-Local-AI-Engine.Tests/Development/DevelopmentWorkspaceAndCoderTests.cs",
            "XE-Local-AI-Engine.Tests.E2ETests/Development/DevelopmentWorkflowE2ETests.cs",
            "XeFramework.Tests.UnitTests/SomethingTests.cs",
            "XeFramework.Tests.UnitTests/Helpers/AssertEx.cs",
            "tests/Probe/FeatureTests.cs",
            "src/features/chat/ChatMessage.test.tsx",
            "src/features/chat/useChat.test.ts"
        ];
        foreach (var path in protectedPaths)
        {
            AssertEx.True(profile.IsProtectedTestPath(path), $"'{path}' must be protected from agent rewrites.");
        }
    }

    /// <summary>
    ///     The other half of the policy, and the more fragile one. Freezing too much is not "safe by default": a
    ///     pattern that swallowed test <c>.csproj</c> files would stop the agent adding a package reference to an
    ///     existing test project, and one that matched <c>*Spec.cs</c> would freeze three production files that are
    ///     not tests at all.
    /// </summary>
    [Test]
    public void ProtectedPaths_DoNotFreezeProductionSourceProjectFilesOrTestingHelpers()
    {
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        string[] writablePaths =
        [
            "XE-Local-AI-Engine.Client.Application/Models/OrchestrationSpec.cs",
            "XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj",
            "XE-Local-AI-Engine.Client.Testing/Foo.cs"
        ];
        foreach (var path in writablePaths)
        {
            AssertEx.False(profile.IsProtectedTestPath(path), $"'{path}' is not a test and must stay writable.");
        }
    }

    /// <summary>
    ///     Matching is case-insensitive and the incoming path is normalized, because the paths arrive from
    ///     <c>git diff --name-status</c> and a rename to <c>featuretests.cs</c> on a case-sensitive filesystem must
    ///     not escape the policy.
    /// </summary>
    [Test]
    public void ProtectedPaths_IgnoreCaseAndSeparatorAndLeadingSlash()
    {
        var profile = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        AssertEx.True(profile.IsProtectedTestPath("tests/probe/featuretests.cs"));
        AssertEx.True(profile.IsProtectedTestPath(@"XE-Local-AI-Engine.Tests\Development\FooTests.cs"));
        AssertEx.True(profile.IsProtectedTestPath("/tests/Probe/FeatureTests.cs"));
        AssertEx.False(profile.IsProtectedTestPath("   "));
    }

    /// <summary>
    ///     <c>**&#47;</c> spans zero or more whole segments, so a root-level test file is protected too. Without the
    ///     zero-segment case a repository that keeps its tests beside its sources would be unprotected.
    /// </summary>
    [Test]
    public void Glob_SpansZeroSegmentsAndDoesNotLetSingleStarCrossADirectory()
    {
        AssertEx.True(DevelopmentGlob.IsMatch("**/*Tests.cs", "FeatureTests.cs"));
        AssertEx.True(DevelopmentGlob.IsMatch("**/*Tests.cs", "a/b/c/FeatureTests.cs"));
        AssertEx.False(DevelopmentGlob.IsMatch("*Tests.cs", "a/FeatureTests.cs"));
        AssertEx.False(DevelopmentGlob.IsMatch("tests/*.cs", "tests/Probe/FeatureTests.cs"));
        AssertEx.True(DevelopmentGlob.IsMatch("tests/**/*.cs", "tests/Probe/FeatureTests.cs"));
        AssertEx.True(DevelopmentGlob.IsMatch("a/?/b.cs", "a/x/b.cs"));
        AssertEx.False(DevelopmentGlob.IsMatch("a/?/b.cs", "a/xy/b.cs"));
        AssertEx.False(DevelopmentGlob.IsMatch("", "anything.cs"));
    }

    /// <summary>
    ///     The digest is what every validation and review artifact is stamped with, and what apply compares against
    ///     before writing to the trusted repository. It therefore has to be a function of the profile's whole
    ///     content: any field that changes what will execute must move it, or a swapped command could be applied
    ///     under a digest that still claims the operator-confirmed one ran.
    /// </summary>
    [Test]
    public void Digest_IsStableForAValueAndMovesForEveryFieldThatChangesWhatRuns()
    {
        var baseline = Probe();
        AssertEx.Equal(baseline.ComputeDigest(), Probe().ComputeDigest());
        AssertEx.Equal(expected: 64, baseline.ComputeDigest().Length);
        AssertEx.True(baseline.ComputeDigest().All(static character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f'),
            "the digest must be lowercase hex, because it is compared as an opaque string across the artifact chain.");

        (string Field, DevelopmentCommandProfile Profile)[] mutations =
        [
            ("executable", baseline with { Commands = [baseline.Commands[0] with { Executable = "hg" }, baseline.Commands[1]] }),
            ("arguments", baseline with { Commands = [baseline.Commands[0], baseline.Commands[1] with { Arguments = ["diff", "--check", "HEAD"] }] }),
            ("timeout", baseline with { Commands = [baseline.Commands[0] with { TimeoutSeconds = 121 }, baseline.Commands[1]] }),
            ("validation list", baseline with { ValidationCommandIds = [DevelopmentCommandIds.GitStatus, DevelopmentCommandIds.GitDiffCheck] }),
            ("protected paths", baseline with { ProtectedPaths = ["**/*Tests.cs", "tests/**/*.cs"] }),
            ("build target", baseline with { BuildTarget = "Other.slnx" }),
            ("import digest", baseline with { ImportDigest = new string('a', 64) }),
            ("custom flag", baseline with { IsCustom = true })
        ];
        foreach (var (field, mutated) in mutations)
        {
            AssertEx.NotEqual(baseline.ComputeDigest(), mutated.ComputeDigest(), $"changing the {field} must move the digest.");
        }
    }

    /// <summary>
    ///     Command order is part of the digest as well: a profile that runs test before build is a different profile,
    ///     even though it holds the same set of commands.
    /// </summary>
    [Test]
    public void Digest_DistinguishesCommandOrderFromCommandSet()
    {
        var baseline = Probe();
        var reordered = baseline with { Commands = [baseline.Commands[1], baseline.Commands[0]] };
        AssertEx.NotEqual(baseline.ComputeDigest(), reordered.ComputeDigest());
    }

    /// <summary>
    ///     The S1.6 reuse rule. A stored profile is the operator-confirmed agreement; if the code-owned definition
    ///     drifted underneath it without a <c>CurrentVersion</c> bump, every already-created project would silently
    ///     start running different commands under a version string claiming otherwise. Rejecting is the only safe
    ///     answer, so this asserts the tampered-content case explicitly rather than only the happy path.
    /// </summary>
    [Test]
    public void ResolveStored_AcceptsAnUntouchedSnapshotAndRejectsOneWhoseContentDrifted()
    {
        var stored = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Fixture.slnx");
        var json = Encoding.UTF8.GetString(stored.ToCanonicalUtf8());
        AssertEx.Equal(stored.ComputeDigest(), DevelopmentCommandProfileCatalog.ResolveStored(json).ComputeDigest());

        // One argument, on the build command. The id and version still resolve, so only the content comparison can
        // catch this — which is exactly the property being pinned.
        var tampered = json.Replace("\"--no-restore\"", "\"--no-dependencies\"", StringComparison.Ordinal);
        AssertEx.NotEqual(json, tampered);
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(tampered));
    }

    [Test]
    public void ResolveStored_RejectsCustomProfilesAndUnresolvableOrMissingSnapshots()
    {
        var generic = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        // Custom profiles need the container sandbox that does not exist yet: a repository that could describe its
        // own build and test commands could describe `true` as its test command.
        var custom = Encoding.UTF8.GetString((generic with { IsCustom = true }).ToCanonicalUtf8());
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(custom));

        var wrongVersion = Encoding.UTF8.GetString((generic with { ProfileVersion = "v0" }).ToCanonicalUtf8());
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(wrongVersion));

        var unknownId = Encoding.UTF8.GetString((generic with { ProfileId = "cargo" }).ToCanonicalUtf8());
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(unknownId));

        // A project with no snapshotted profile must fail closed rather than fall back to a default, because the
        // fallback would be an unconfirmed command set running against a trusted repository.
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(null));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored("   "));
    }

    /// <summary>
    ///     Every profile must define both baseline git commands, whatever else it carries, and the reason is not
    ///     symmetry: <c>GetStatusAsync</c> — the coder's own status tool — routes through
    ///     <c>ExecuteCatalogAsync</c>, so a profile without <c>git_status</c> would materialize and resolve without
    ///     complaint and then fail deep inside a coder attempt, after the workspace was already prepared. Requiring
    ///     it at resolution turns that into a rejection the operator sees at confirmation.
    ///     <para>
    ///         Both cases below are built so the baseline rule is the ONLY one that can reject them. Dropping
    ///         <c>git_diff_check</c> is paired with a validation list of <c>[git_status]</c> on purpose: leaving the
    ///         default list would trip "validates a command it does not define" first, and the test would pass for
    ///         the wrong reason while proving nothing about the baseline rule.
    ///     </para>
    /// </summary>
    [Test]
    public void ResolveStored_RejectsAProfileMissingEitherBaselineGitCommand()
    {
        var generic = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        // Every code-owned profile carries both, so the rule below never rejects a shipped profile.
        foreach (var profileId in new[] { DevelopmentCommandProfileCatalog.GenericGit, DevelopmentCommandProfileCatalog.DotnetSlnx })
        {
            var profile = DevelopmentCommandProfileCatalog.Materialize(profileId,
                string.Equals(profileId, DevelopmentCommandProfileCatalog.GenericGit, StringComparison.Ordinal)
                    ? null
                    : "Fixture.slnx");
            AssertEx.Equal(DevelopmentCommandIds.GitStatus, profile.ResolveCommand(DevelopmentCommandIds.GitStatus).CommandId);
            AssertEx.Equal(DevelopmentCommandIds.GitDiffCheck, profile.ResolveCommand(DevelopmentCommandIds.GitDiffCheck).CommandId);
        }

        var withoutStatus = generic with { Commands = Without(generic, DevelopmentCommandIds.GitStatus) };
        AssertEx.Equal(DevelopmentCommandIds.GitDiffCheck, string.Join(',', withoutStatus.ValidationCommandIds));
        var statusRejection = Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.ResolveStored(Encoding.UTF8.GetString(withoutStatus.ToCanonicalUtf8())));

        var withoutDiffCheck = generic with
        {
            Commands = Without(generic, DevelopmentCommandIds.GitDiffCheck),
            ValidationCommandIds = [DevelopmentCommandIds.GitStatus]
        };
        var diffCheckRejection = Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.ResolveStored(Encoding.UTF8.GetString(withoutDiffCheck.ToCanonicalUtf8())));

        // Assert WHICH rule rejected these, not merely that something did. Every other structural rule throws the
        // same exception type, so without this the test would still pass if the baseline check were deleted and a
        // neighbouring rule happened to catch the same input.
        AssertEx.Contains(AssertEx.NotNull(statusRejection).Message, "baseline git", StringComparison.OrdinalIgnoreCase);
        AssertEx.Contains(AssertEx.NotNull(diffCheckRejection).Message, "baseline git", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     A profile whose validation list names a command it does not define, or that omits the baseline git
    ///     commands the engine itself routes through, would fail deep inside an attempt instead of at resolution.
    /// </summary>
    [Test]
    public void ResolveStored_RejectsStructurallyBrokenProfiles()
    {
        var generic = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);

        var undefinedValidation = Encoding.UTF8.GetString((generic with
        {
            ValidationCommandIds = [DevelopmentCommandIds.DotnetTestRelease]
        }).ToCanonicalUtf8());
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(undefinedValidation));

        var emptyValidation = Encoding.UTF8.GetString((generic with { ValidationCommandIds = [] }).ToCanonicalUtf8());
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => DevelopmentCommandProfileCatalog.ResolveStored(emptyValidation));
    }

    /// <summary>
    ///     The build target becomes a literal process argument, so it is confined exactly like any other
    ///     agent-reachable workspace path, and it must match the profile that will consume it — <c>dotnet build</c>
    ///     against a <c>.csproj</c> under a solution profile is a different command than the operator confirmed.
    /// </summary>
    [Test]
    public void Materialize_RejectsTraversingAbsoluteAndMismatchedBuildTargets()
    {
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "../x.slnx"));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "src/../../x.slnx"));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "/etc/x.slnx"));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "src/Lib/Lib.csproj"));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetCsproj, "Fixture.slnx"));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, buildTarget: null));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, "Fixture.slnx"));
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentCommandProfileCatalog.Materialize("cargo", buildTarget: null));
    }

    [Test]
    public void Materialize_NormalizesAcceptedBuildTargetsAndCarriesThemIntoEveryDotnetCommand()
    {
        var solution = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "./Fixture.slnx");
        AssertEx.Equal("Fixture.slnx", solution.BuildTarget);
        foreach (var commandId in new[]
                 {
                     DevelopmentCommandIds.DotnetRestore,
                     DevelopmentCommandIds.DotnetBuildRelease,
                     DevelopmentCommandIds.DotnetTestRelease
                 })
        {
            var command = solution.ResolveCommand(commandId);
            AssertEx.Equal("dotnet", command.Executable);
            AssertEx.Contains(command.Arguments, "Fixture.slnx");
        }

        // The legacy .sln extension is accepted under the solution profile on purpose; a repository that has not
        // migrated to .slnx is still a solution repository.
        AssertEx.Equal("Fixture.sln",
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetSlnx, "Fixture.sln").BuildTarget);
        AssertEx.Equal("src/Lib/Lib.csproj",
            DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.DotnetCsproj, "src/Lib/Lib.csproj").BuildTarget);
        AssertEx.Null(DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null).BuildTarget);
    }

    [Test]
    public void ResolveCommand_RejectsAnIdTheProfileDoesNotDefine()
    {
        var generic = DevelopmentCommandProfileCatalog.Materialize(DevelopmentCommandProfileCatalog.GenericGit, buildTarget: null);
        Assert.Throws<DevelopmentWorkspaceSecurityException>(() => generic.ResolveCommand(DevelopmentCommandIds.DotnetTestRelease));
    }

    /// <summary>
    ///     Detection is a proposal shown to the operator, not an authority — but it still has to propose the right
    ///     thing, and a repository with no recognizable .NET build system has to land on <c>generic-git</c> rather
    ///     than on a .NET profile that would fail with "command not found" on the first build.
    /// </summary>
    [Test]
    public void Detector_ProposesSolutionOverProjectAndFallsBackToGenericGit()
    {
        var detector = new DevelopmentCommandProfileDetector();

        var solutionRoot = CreateTree("solution",
            "Thing.slnx",
            "src/Lib/Lib.csproj");
        var solution = detector.Detect(solutionRoot);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetSlnx, solution.ProfileId);
        AssertEx.Equal("Thing.slnx", solution.BuildTarget);
        AssertEx.Contains(solution.Candidates, "Thing.slnx");

        var projectRoot = CreateTree("project", "src/Lib/Lib.csproj");
        var project = detector.Detect(projectRoot);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetCsproj, project.ProfileId);
        AssertEx.Equal("src/Lib/Lib.csproj", project.BuildTarget);

        var plainRoot = CreateTree("plain", "README.md");
        var plain = detector.Detect(plainRoot);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.GenericGit, plain.ProfileId);
        AssertEx.Null(plain.BuildTarget);
        AssertEx.Empty(plain.Candidates);

        // Whatever detection proposes has to be materializable, or the confirmation step would offer the operator a
        // profile that cannot be created.
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetSlnx,
            DevelopmentCommandProfileCatalog.Materialize(solution.ProfileId, solution.BuildTarget).ProfileId);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.DotnetCsproj,
            DevelopmentCommandProfileCatalog.Materialize(project.ProfileId, project.BuildTarget).ProfileId);
        AssertEx.Equal(DevelopmentCommandProfileCatalog.GenericGit,
            DevelopmentCommandProfileCatalog.Materialize(plain.ProfileId, plain.BuildTarget).ProfileId);
    }

    /// <summary>A minimal but structurally valid profile to mutate, so digest coverage does not depend on the catalog.</summary>
    private static DevelopmentCommandProfile Probe() =>
        new("probe",
            "v1",
            TemplateId: null,
            "Fixture.slnx",
            ImportDigest: null,
            [
                new DevelopmentProfileCommand(DevelopmentCommandIds.GitStatus, "git", ["status", "--short"], 120),
                new DevelopmentProfileCommand(DevelopmentCommandIds.GitDiffCheck, "git", ["diff", "--check"], 120)
            ],
            [DevelopmentCommandIds.GitDiffCheck],
            ["**/*Tests.cs"],
            IsCustom: false);

    private static DevelopmentProfileCommand[] Without(DevelopmentCommandProfile profile, string commandId) =>
        profile.Commands
               .Where(command => !string.Equals(command.CommandId, commandId, StringComparison.Ordinal))
               .ToArray();

    private string CreateTree(string name, params string[] relativeFiles)
    {
        var root = Path.Combine(_root, name);
        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }

        return root;
    }
}
