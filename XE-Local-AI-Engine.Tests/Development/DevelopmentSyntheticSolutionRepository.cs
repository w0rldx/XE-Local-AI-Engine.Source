namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Reflection;

/// <summary>
///     Builds a throwaway Git repository that is a real, buildable .NET solution.
///     <para>
///         It exists so the deterministic validation gate can be exercised under a PRODUCTION command profile
///         (<c>dotnet-slnx</c>: git_diff_check + dotnet restore/build/test) without running the gate against this
///         repository's own solution, which takes minutes. The fixture solution restores, builds and tests in roughly
///         two seconds.
///     </para>
///     <para>
///         The solution used to be named <c>XE-Local-AI-Engine.slnx</c> exactly, because the command catalog hardcoded
///         that literal file name in a <c>Solution</c> constant and would not build anything else. That constraint is
///         gone: the build target is now carried by the project's <c>DevelopmentCommandProfile</c>, so a test binds
///         <see cref="SolutionPath" /> into a <c>dotnet-slnx</c> profile via
///         <c>DevelopmentCommandProfileCatalog.Materialize</c>. The name is deliberately synthetic now, so a fixture
///         that only passes because it impersonates this repository cannot go unnoticed.
///     </para>
///     <para>
///         The layout is deliberately minimal and offline: a class library whose single method returns a known
///         string, and a TUnit test asserting that string. A coder attempt that rewrites <see cref="LibrarySourcePath" />
///         can therefore produce exactly three outcomes on demand — compiles and passes, fails to compile, or
///         compiles and fails its test — which is what the validation gate has to be able to tell apart.
///     </para>
/// </summary>
internal static class DevelopmentSyntheticSolutionRepository
{
    /// <summary>
    ///     The repository-relative solution file. Pass it as the build target when materializing the
    ///     <c>dotnet-slnx</c> profile this fixture is meant to be validated under.
    /// </summary>
    public const string SolutionPath = "SyntheticFixture.slnx";

    /// <summary>
    ///     The repository-relative test project, and the build target for the <c>dotnet-csproj</c> profile. It is a
    ///     genuine target for that profile rather than a convenient stand-in: it is an <c>OutputType=Exe</c> TUnit
    ///     project with a <c>ProjectReference</c> to the library, so restoring, building and testing it pulls in
    ///     <see cref="LibrarySourcePath" /> and produces the same three outcomes the solution profile does.
    /// </summary>
    public const string ProbeProjectPath = "tests/Probe/Probe.csproj";

    /// <summary>The library source file a coder attempt overwrites to steer the outcome of the gate.</summary>
    public const string LibrarySourcePath = "src/Lib/Feature.cs";

    /// <summary>The value <see cref="PassingLibrarySource" /> returns and the fixture's test asserts.</summary>
    private const string ApprovedValue = "base";

    /// <summary>
    ///     The committed baseline: the feature is not implemented yet, so the fixture's test fails at HEAD. Every
    ///     coder attempt below therefore produces a non-empty patch — a coder that "writes" the file back unchanged
    ///     exports an empty diff, which the patch evidence service rejects before validation is ever reached.
    /// </summary>
    private static string BaselineLibrarySource { get; } = LibrarySource("unimplemented");

    /// <summary>Library source that compiles and satisfies the fixture's test.</summary>
    public static string PassingLibrarySource { get; } = LibrarySource(ApprovedValue);

    /// <summary>Library source that does not compile — the closing semicolon is missing.</summary>
    public static string BuildBreakingLibrarySource { get; } = $$"""
                                                                 namespace Lib;

                                                                 public static class Feature
                                                                 {
                                                                     public static string Value() => "{{ApprovedValue}}"
                                                                 }

                                                                 """;

    /// <summary>Library source that compiles cleanly but makes the fixture's test assertion fail.</summary>
    public static string TestFailingLibrarySource { get; } = LibrarySource("regressed");

    /// <summary>
    ///     Creates the repository at <paramref name="repositoryRoot" /> and commits it on <c>main</c>, leaving a
    ///     clean worktree at a single base commit.
    /// </summary>
    /// <param name="repositoryRoot">Where to create the repository.</param>
    /// <param name="includeTests">
    ///     When false, the solution contains the library alone and no test project at all. This is the shape of a
    ///     registered repository that simply has no tests, and it is a distinct case from a suite that ran and failed:
    ///     <c>dotnet test</c> answers <c>No test projects were found.</c> on stderr and exits 1, so the gate has no
    ///     result to read rather than a bad one. Slice 4's policy for that case is a specific, actionable failure —
    ///     never a pass, and never indistinguishable from a build break.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task CreateAsync(string repositoryRoot,
        bool includeTests = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "src", "Lib"));
        Directory.CreateDirectory(Path.Combine(repositoryRoot, "tests", "Probe"));

        var (packagesRoot, testFrameworkVersion) = ResolveTestFrameworkPackage();

        // Pin the same SDK band and MTP test runner this repository uses, so `dotnet test` speaks Microsoft.Testing
        // .Platform here too and accepts the profile's --max-parallel-test-modules argument (a VSTest-mode run
        // rejects it outright).
        await WriteAsync(repositoryRoot,
            "global.json",
            """
            {
              "sdk": {
                "version": "10.0.100",
                "rollForward": "latestFeature",
                "allowPrerelease": false
              },
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }

            """,
            cancellationToken).ConfigureAwait(false);

        // Restore has to succeed with no network. DevelopmentWorkspaceTools.BuildEnvironment points NUGET_PACKAGES at
        // a fresh per-session directory, so the ambient package cache is not visible; clearing the package sources and
        // declaring the host cache as a FALLBACK folder resolves the graph from disk instead. The host cache is
        // guaranteed to hold this exact version — it is the version this very test assembly was restored with.
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

             """,
            cancellationToken).ConfigureAwait(false);

        // Build output must be ignored. DevelopmentPatchEvidenceService exports the subject with `git add -A`, so an
        // un-ignored bin/ or obj/ produced by validation would land in the patch and change the subject hash between
        // validation and review — invalidating the evidence and blocking apply for a reason that has nothing to do
        // with the change under test. (This repository's own .gitignore already covers all three.)
        await WriteAsync(repositoryRoot,
            ".gitignore",
            """
            bin/
            obj/
            TestResults/

            """,
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(repositoryRoot,
            SolutionPath,
            includeTests
                ? """
                  <Solution>
                      <Project Path="src/Lib/Lib.csproj"/>
                      <Project Path="tests/Probe/Probe.csproj"/>
                  </Solution>

                  """
                : """
                  <Solution>
                      <Project Path="src/Lib/Lib.csproj"/>
                  </Solution>

                  """,
            cancellationToken).ConfigureAwait(false);

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

            """,
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(repositoryRoot, LibrarySourcePath, BaselineLibrarySource, cancellationToken).ConfigureAwait(false);

        if (includeTests)
        {
            await WriteTestProjectAsync(repositoryRoot, testFrameworkVersion, cancellationToken).ConfigureAwait(false);
        }

        await RunGitAsync(repositoryRoot, cancellationToken, "init", "--initial-branch=main", ".").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot, cancellationToken, "config", "user.email", "development-validation@example.invalid").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot, cancellationToken, "config", "user.name", "Development Validation Test").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot, cancellationToken, "add", "-A", "--", ".").ConfigureAwait(false);
        await RunGitAsync(repositoryRoot, cancellationToken, "commit", "-m", "synthetic solution fixture").ConfigureAwait(false);
    }

    private static async Task WriteTestProjectAsync(string repositoryRoot,
        string testFrameworkVersion,
        CancellationToken cancellationToken)
    {
        await WriteAsync(repositoryRoot,
            ProbeProjectPath,
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

             """,
            cancellationToken).ConfigureAwait(false);

        await WriteAsync(repositoryRoot,
            "tests/Probe/FeatureTests.cs",
            $$"""
              namespace Probe;

              public sealed class FeatureTests
              {
                  [Test]
                  public async Task Value_ReturnsTheApprovedContent() =>
                      await Assert.That(Lib.Feature.Value()).IsEqualTo("{{ApprovedValue}}");
              }

              """,
            cancellationToken).ConfigureAwait(false);
    }

    private static string LibrarySource(string value) =>
        $$"""
          namespace Lib;

          public static class Feature
          {
              public static string Value() => "{{value}}";
          }

          """;

    /// <summary>
    ///     Resolves the NuGet package cache and the TUnit package version to restore the fixture's test project from.
    ///     Both are taken from this test host: the version is the one this assembly is running on, and the cache is
    ///     the one it was restored into, so the pair is always present on any machine that could build this project.
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
            throw new InvalidOperationException(
                $"The NuGet package cache '{packagesRoot}' has no restored TUnit package, so the synthetic Development solution cannot be restored offline. Run a restore of this repository first, or set NUGET_PACKAGES to the cache it was restored into.");
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

    private static async Task WriteAsync(string repositoryRoot,
        string relativePath,
        string content,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunGitAsync(string workingDirectory,
        CancellationToken cancellationToken,
        params string[] arguments)
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
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {await standardOutput.ConfigureAwait(false)}{await standardError.ConfigureAwait(false)}");
        }
    }
}
