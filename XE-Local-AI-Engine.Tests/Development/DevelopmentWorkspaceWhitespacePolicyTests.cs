namespace XE_Local_AI_Engine.Tests.Development;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Services.AgentHome.Implementation;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The whitespace policy the managed workspace runs its validation gate under.
///     <para>
///         Two layers, deliberately. <see cref="DevelopmentWorkspaceWhitespacePolicy.Render" /> is pure, so every
///         classification is provable without a repository. Behind that, the two end-to-end assertions run real
///         <c>git diff --check</c> against real repositories, because the whole point of this policy is what GIT
///         concludes — the rendered file is only the means, and a test that asserted the file's text alone could pass
///         while git ignored it.
///     </para>
///     <para>
///         Both directions are pinned on purpose. Fixing the false failure on a CRLF repository is easy; doing it
///         without silently retiring the check on an LF repository is the actual requirement, and the second
///         end-to-end test is the one that fails if someone later "simplifies" this to
///         <c>core.whitespace=cr-at-eol</c>.
///     </para>
/// </summary>
public sealed class DevelopmentWorkspaceWhitespacePolicyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-whitespace-" + Guid.NewGuid().ToString("N"));

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

    [Test]
    public void Render_WhenNothingIsStoredAsCrlf_WritesNoPolicyAtAllSoTheStrictDefaultStands()
    {
        var rendered = DevelopmentWorkspaceWhitespacePolicy.Render(Listing(("lf", "src/a.cs"), ("lf", "src/b.cs"), ("-text", "logo.png")));

        AssertEx.Null(rendered, "an LF repository must keep every default whitespace rule, CR included");
    }

    /// <summary>
    ///     The mixed repository is the common case, not the exotic one: this engine's own tree stores 4243 files as LF
    ///     and exactly one as CRLF. Granting the whole repository <c>cr-at-eol</c> because of that one file would
    ///     retire the check on the other 4243.
    /// </summary>
    [Test]
    public void Render_GrantsCrAtEolToTheCrlfStoredPathsAndToNothingElse()
    {
        var rendered = AssertEx.NotNull(DevelopmentWorkspaceWhitespacePolicy.Render(
            Listing(("lf", "src/a.cs"), ("crlf", "tools/setup.bat"), ("mixed", "docs/notes.txt"), ("lf", "src/b.cs"))));

        AssertEx.Contains(rendered, "/tools/setup.bat whitespace=cr-at-eol");
        AssertEx.Contains(rendered, "/docs/notes.txt whitespace=cr-at-eol");
        AssertEx.False(rendered.Contains("src/a.cs", StringComparison.Ordinal), "an LF-stored path must not be granted the policy");
        AssertEx.False(rendered.Contains("* whitespace", StringComparison.Ordinal), "a mixed repository must not be widened to a wildcard");
    }

    /// <summary>
    ///     A pattern with no slash is a glob matched at ANY depth, so a bare <c>setup.bat</c> would hand the policy to
    ///     every same-named file in the tree instead of to the one that earned it.
    /// </summary>
    [Test]
    public void Render_AnchorsEveryPatternAtTheRepositoryRoot()
    {
        var rendered = AssertEx.NotNull(DevelopmentWorkspaceWhitespacePolicy.Render(Listing(("crlf", "setup.bat"))));

        AssertEx.Contains(rendered, "/setup.bat whitespace=cr-at-eol");
    }

    [Test]
    public void Render_QuotesAPathCarryingWhitespaceRatherThanEmittingTwoBrokenFields()
    {
        var rendered = AssertEx.NotNull(DevelopmentWorkspaceWhitespacePolicy.Render(Listing(("crlf", "tools/sp ace.bat"))));

        AssertEx.Contains(rendered, "\"/tools/sp ace.bat\" whitespace=cr-at-eol");
    }

    /// <summary>
    ///     A path that git had to quote, or one carrying a glob metacharacter, cannot be turned into a pattern that is
    ///     unambiguously about that one file. Widening to the whole repository is the safe direction here: the failure
    ///     this policy exists to prevent is a correct change being rejected.
    /// </summary>
    [Test]
    public void Render_WhenACrlfPathCannotBeExpressedAsAPattern_WidensToTheWholeRepository()
    {
        foreach (var awkward in new[] { "\"tools/qu\\\"ote.bat\"", "tools/star[1].bat", "tools/back\\slash.bat" })
        {
            var rendered = AssertEx.NotNull(DevelopmentWorkspaceWhitespacePolicy.Render(Listing(("crlf", awkward))));
            AssertEx.Contains(rendered, "* whitespace=cr-at-eol");
        }
    }

    /// <summary>
    ///     Git walks the whole pattern list for every path it looks up, so an exhaustive list on a large all-CRLF
    ///     repository is quadratic work on every diff. A repository with this many CRLF blobs is a CRLF repository, and
    ///     the wildcard says the same thing the list would.
    /// </summary>
    [Test]
    public void Render_BeyondTheExplicitPathCeiling_CollapsesToTheWildcard()
    {
        var listing = new StringBuilder();
        for (var index = 0; index <= DevelopmentWorkspaceWhitespacePolicy.MaxExplicitPaths; index++)
        {
            _ = listing.Append(Listing(("crlf", "f" + index.ToString("D5", CultureInfo.InvariantCulture) + ".bat")));
        }

        var rendered = AssertEx.NotNull(DevelopmentWorkspaceWhitespacePolicy.Render(listing.ToString()));

        AssertEx.Contains(rendered, "* whitespace=cr-at-eol");
    }

    /// <summary>
    ///     <c>ls-files --eol</c> reports the worktree too, and with <c>core.autocrlf=true</c> — which Git for Windows'
    ///     system config commonly sets — the worktree is CRLF while the blob is LF and <c>diff --check</c> compares
    ///     against the blob. Reading the <c>w/</c> column would hand the policy to every ordinary LF repository on
    ///     Windows.
    /// </summary>
    [Test]
    public void Render_ReadsTheIndexColumnAndIgnoresTheWorktreeColumn()
    {
        const string autocrlfCheckout = "i/lf    w/crlf  attr/                 \tsrc/a.cs\n";

        AssertEx.Null(DevelopmentWorkspaceWhitespacePolicy.Render(autocrlfCheckout));
    }

    /// <summary>
    ///     The defect, end to end: on a repository whose blobs are CRLF, the validation gate's FIRST command reported
    ///     trailing whitespace on every changed line and exited 2 — so a perfectly correct change failed at command
    ///     one and the operator was told their patch had whitespace errors.
    /// </summary>
    [Test]
    public async Task ApplyAsync_OnACrlfRepository_LetsTheGatesFirstCommandPass()
    {
        // The repository stores CRLF and the added line matches it — correct work, nothing to report.
        var repository = await CreateRepositoryAsync("crlf", "alpha\r\nbeta\r\ngamma\r\n", "delta\r\n").ConfigureAwait(false);

        var before = await DiffCheckAsync(repository).ConfigureAwait(false);
        AssertEx.NotEqual(0, before.ExitCode, "this test is vacuous unless the unfixed behaviour still fails");
        AssertEx.Contains(before.StandardOutput, "trailing whitespace");

        await DevelopmentWorkspaceWhitespacePolicy.ApplyAsync(NewGit(), repository, CancellationToken.None).ConfigureAwait(false);

        var after = await DiffCheckAsync(repository).ConfigureAwait(false);
        AssertEx.Equal(0, after.ExitCode, "a CRLF repository's own line endings are not a whitespace error: " + after.StandardOutput);
    }

    /// <summary>
    ///     The other half, and the reason this is per path rather than <c>core.whitespace=cr-at-eol</c>: on a repository
    ///     that stores LF, a change that introduces a CRLF line IS a defect and the gate must still catch it.
    /// </summary>
    [Test]
    public async Task ApplyAsync_OnAnLfRepository_StillCatchesAnIntroducedCarriageReturn()
    {
        // The repository stores LF and the added line carries a CR — a real defect, and the whole reason this policy
        // is per path instead of a repository-wide core.whitespace setting.
        var repository = await CreateRepositoryAsync("lf", "alpha\nbeta\ngamma\n", "delta\r\n").ConfigureAwait(false);

        await DevelopmentWorkspaceWhitespacePolicy.ApplyAsync(NewGit(), repository, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(Path.Combine(repository, ".git", "info", "attributes")),
            "an LF repository must be granted no policy at all");

        var after = await DiffCheckAsync(repository).ConfigureAwait(false);
        AssertEx.NotEqual(0, after.ExitCode, "an introduced CR on an LF repository must still fail the check");
        AssertEx.Contains(after.StandardOutput, "trailing whitespace");
    }

    /// <summary>
    ///     A file left by a previous attempt is a policy nobody derived from the current index, and a command inside the
    ///     workspace can replace it with a link that an ordinary write would follow out of the workspace.
    /// </summary>
    [Test]
    public async Task ApplyAsync_ReplacesAStalePolicyRatherThanLeavingItInPlace()
    {
        var repository = await CreateRepositoryAsync("lf", "alpha\nbeta\ngamma\n", "delta\n").ConfigureAwait(false);
        var info = Path.Combine(repository, ".git", "info");
        _ = Directory.CreateDirectory(info);
        await File.WriteAllTextAsync(Path.Combine(info, "attributes"), "* whitespace=cr-at-eol\n").ConfigureAwait(false);

        await DevelopmentWorkspaceWhitespacePolicy.ApplyAsync(NewGit(), repository, CancellationToken.None).ConfigureAwait(false);

        AssertEx.False(File.Exists(Path.Combine(info, "attributes")),
            "a policy the current index does not justify must be removed, not preserved");
    }

    private static HostGitRunner NewGit() => new(timeoutSeconds: 120);

    // One `git ls-files --eol` row: "i/<eol>  w/<eol>  attr/<attr>  \t<path>".
    private static string Listing(params (string IndexEol, string Path)[] entries)
    {
        var builder = new StringBuilder();
        foreach (var (indexEol, path) in entries)
        {
            _ = builder.Append(CultureInfo.InvariantCulture, $"i/{indexEol,-6}w/{indexEol,-6}attr/                 \t{path}\n");
        }

        return builder.ToString();
    }

    /// <summary>
    ///     A repository whose committed blob carries <paramref name="content" />'s line endings verbatim, with
    ///     <paramref name="addedLine" /> appended in the worktree — the shape the gate sees after the coder has edited
    ///     a file.
    /// </summary>
    private async Task<string> CreateRepositoryAsync(string name, string content, string addedLine)
    {
        var repository = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(repository);
        await RunGitAsync(repository, "init", "--initial-branch=main", ".").ConfigureAwait(false);

        // Pinned so the host's own autocrlf/eol settings cannot decide what this repository stores.
        await RunGitAsync(repository, "config", "core.autocrlf", "false").ConfigureAwait(false);
        await RunGitAsync(repository, "config", "user.email", "whitespace-policy@example.invalid").ConfigureAwait(false);
        await RunGitAsync(repository, "config", "user.name", "Whitespace Policy Test").ConfigureAwait(false);

        var file = Path.Combine(repository, "a.txt");
        await File.WriteAllTextAsync(file, content).ConfigureAwait(false);
        await RunGitAsync(repository, "add", "a.txt").ConfigureAwait(false);
        await RunGitAsync(repository, "commit", "-m", "base").ConfigureAwait(false);

        await File.WriteAllTextAsync(file, content + addedLine).ConfigureAwait(false);
        return repository;
    }

    private static async Task<HostGitResult> DiffCheckAsync(string repository)
    {
        return await NewGit().RunAsync(repository,
                                 AgentHomeGit.Arguments("diff", "--check", "HEAD", "--", "."),
                                 CancellationToken.None)
                             .ConfigureAwait(false);
    }

    private static async Task RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        _ = process.Start();
        var standardError = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
        _ = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        await process.WaitForExitAsync().ConfigureAwait(false);
        AssertEx.Equal(0, process.ExitCode, $"git {string.Join(' ', arguments)} failed: {standardError}");
    }
}
