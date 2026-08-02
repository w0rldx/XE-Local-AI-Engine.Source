namespace XE_Local_AI_Engine.Tests.Development;

using System.Globalization;
using System.Text;
using XE_Local_AI_Engine.Client.Services.Development;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The two workspace surveys, exercised directly rather than through a sandbox.
///     <para>
///         These tests exist because the previous implementation shelled out to <c>find</c> and <c>grep</c> with
///         POSIX-only argument vectors and every test covering it skipped off Linux — so the code path a Windows tester
///         would run had no coverage anywhere, on any machine. The scanner is one implementation on every platform, so
///         a green run here IS the Windows evidence; nothing below is OS-conditional except the two symlink guards,
///         which need the privilege to plant a link in the first place.
///     </para>
/// </summary>
public sealed class DevelopmentWorkspaceFileScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xe-development-scanner-" + Guid.NewGuid().ToString("N"));

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
    public void ListFiles_EmitsWorkspaceRelativePathsInTheShapeTheFiltersAndThePromptExpect()
    {
        var workspace = CreateWorkspace();
        Write(workspace, "src/feature.cs", "// code");
        Write(workspace, "readme.md", "hello");

        var listing = DevelopmentWorkspaceFileScanner.ListFiles(workspace, maxEntries: 100, NeverSuppressed, CancellationToken.None);

        AssertEx.Contains(listing, "./readme.md");
        AssertEx.Contains(listing, "./src/feature.cs");
        AssertEx.Equal(2, listing.Count);
    }

    /// <summary>
    ///     <c>find</c> emitted entries in <c>readdir</c> order — creation order on tmpfs, hash order on ext4 — which made
    ///     the same repository list in a different order (and therefore truncate at a different place) depending on the
    ///     filesystem underneath it. A listing must be a function of the tree alone.
    /// </summary>
    [Test]
    public void ListFiles_OrdersTheTreeDeterministicallyRatherThanInFilesystemOrder()
    {
        var workspace = CreateWorkspace();
        foreach (var name in new[] { "zebra.txt", "alpha.txt", "middle.txt" })
        {
            Write(workspace, name, "x");
        }

        Write(workspace, "b-dir/inner.txt", "x");
        Write(workspace, "a-dir/inner.txt", "x");

        var listing = DevelopmentWorkspaceFileScanner.ListFiles(workspace, maxEntries: 100, NeverSuppressed, CancellationToken.None);

        // Files of the scanned directory first, name-sorted, then each subdirectory in name order.
        AssertEx.Equal("./alpha.txt", listing[0]);
        AssertEx.Equal("./middle.txt", listing[1]);
        AssertEx.Equal("./zebra.txt", listing[2]);
        AssertEx.Equal("./a-dir/inner.txt", listing[3]);
        AssertEx.Equal("./b-dir/inner.txt", listing[4]);
    }

    /// <summary>
    ///     The suppression rule has to PRUNE, not post-filter. A managed workspace is a standalone clone whose
    ///     <c>.git</c> alone holds far more entries than the listing budget allows, so a post-filter spends the whole
    ///     budget on entries it is about to discard and answers with nothing while the workspace is full of actionable
    ///     files. The suppressed tree is named so it sorts FIRST, which is exactly the ordering that used to lose.
    /// </summary>
    [Test]
    public void ListFiles_PrunesASuppressedTreeRatherThanSpendingTheBudgetOnIt()
    {
        var workspace = CreateWorkspace();
        for (var index = 0; index < 200; index++)
        {
            Write(workspace, ".git/objects/" + index.ToString("D4", CultureInfo.InvariantCulture), "x");
        }

        Write(workspace, "zz-src/feature.cs", "// code");

        var listing = DevelopmentWorkspaceFileScanner.ListFiles(workspace,
            maxEntries: 8,
            relative => relative.StartsWith(".git", StringComparison.Ordinal),
            CancellationToken.None);

        AssertEx.Equal(1, listing.Count);
        AssertEx.Equal("./zz-src/feature.cs", listing[0]);
    }

    [Test]
    public void ListFiles_StopsAtTheEntryCeiling()
    {
        var workspace = CreateWorkspace();
        for (var index = 0; index < 20; index++)
        {
            Write(workspace, "f" + index.ToString("D2", CultureInfo.InvariantCulture), "x");
        }

        var listing = DevelopmentWorkspaceFileScanner.ListFiles(workspace, maxEntries: 5, NeverSuppressed, CancellationToken.None);

        AssertEx.Equal(5, listing.Count);
    }

    [Test]
    public void ListFiles_StopsAtTheDepthCeilingInsteadOfDescendingForever()
    {
        var workspace = CreateWorkspace();
        var deep = string.Join('/', Enumerable.Repeat("d", DevelopmentWorkspaceFileScanner.MaxDepth + 2));
        Write(workspace, deep + "/too-deep.txt", "x");
        Write(workspace, "shallow.txt", "x");

        var listing = DevelopmentWorkspaceFileScanner.ListFiles(workspace, maxEntries: 100, NeverSuppressed, CancellationToken.None);

        AssertEx.Contains(listing, "./shallow.txt");
        AssertEx.False(listing.Any(entry => entry.EndsWith("too-deep.txt", StringComparison.Ordinal)),
            "an entry past the depth ceiling must not be listed");
    }

    /// <summary>
    ///     Parity with <c>find -P … -type f</c>, which printed neither a symbolic link nor anything reached through one.
    ///     A link the agent plants inside the workspace can name a target outside it, so following one would turn a
    ///     confined listing into an unconfined one.
    /// </summary>
    [Test]
    public void ListFiles_NeitherFollowsNorEmitsASymbolicLink()
    {
        SymlinkSupport.EnsureSupported();

        var workspace = CreateWorkspace();
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "leak");
        Write(workspace, "real.txt", "x");
        Directory.CreateSymbolicLink(Path.Combine(workspace, "escape"), outside);
        File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), Path.Combine(outside, "secret.txt"));

        var listing = DevelopmentWorkspaceFileScanner.ListFiles(workspace, maxEntries: 100, NeverSuppressed, CancellationToken.None);

        AssertEx.Equal(1, listing.Count);
        AssertEx.Equal("./real.txt", listing[0]);
    }

    [Test]
    public void ListFiles_WhenTheScanRootIsItselfALink_Refuses()
    {
        SymlinkSupport.EnsureSupported();

        var workspace = CreateWorkspace();
        var outside = Path.Combine(_root, "outside-root");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "leak");
        var linkedRoot = Path.Combine(workspace, "linked");
        Directory.CreateSymbolicLink(linkedRoot, outside);

        _ = Assert.Throws<DevelopmentWorkspaceSecurityException>(() =>
            DevelopmentWorkspaceFileScanner.ListFiles(linkedRoot, maxEntries: 100, NeverSuppressed, CancellationToken.None));
    }

    [Test]
    public void ListFiles_WhenTheDirectoryIsAbsent_ThrowsSoTheToolReportsAFailureRatherThanAnEmptyWorkspace()
    {
        var workspace = CreateWorkspace();

        _ = Assert.Throws<DirectoryNotFoundException>(() =>
            DevelopmentWorkspaceFileScanner.ListFiles(Path.Combine(workspace, "nope"), maxEntries: 100, NeverSuppressed, CancellationToken.None));
    }

    [Test]
    public void SearchText_EmitsPathLineAndTextTheWayTheMatchRendererExpects()
    {
        var workspace = CreateWorkspace();
        Write(workspace, "src/feature.cs", "one\ntwo needle two\nthree\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, "needle", maxOutputBytes: 65536, NeverSuppressed, CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./src/feature.cs:2:two needle two", matches[0]);
    }

    /// <summary>
    ///     The shell-out passed <c>grep -F</c>: the pattern is model-supplied, so it must never be compiled as a regular
    ///     expression. Managed code gets the property structurally, and this pins it.
    /// </summary>
    [Test]
    public void SearchText_MatchesTheLiteralPatternRatherThanCompilingItAsAnExpression()
    {
        var workspace = CreateWorkspace();
        Write(workspace, "a.txt", "abc\n");
        Write(workspace, "b.txt", "a.c\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, "a.c", maxOutputBytes: 65536, NeverSuppressed, CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./b.txt:1:a.c", matches[0]);
    }

    [Test]
    public void SearchText_NeverEntersASuppressedTreeSoTheContentCannotReachTheOutput()
    {
        var workspace = CreateWorkspace();
        Write(workspace, ".env", "AWS_SECRET_ACCESS_KEY=needle\n");
        Write(workspace, "src/feature.cs", "// needle\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace,
            "needle",
            maxOutputBytes: 65536,
            relative => relative.EndsWith(".env", StringComparison.Ordinal),
            CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./src/feature.cs:1:// needle", matches[0]);
    }

    /// <summary>Parity with <c>grep -I</c>: a file carrying a NUL byte is not searched at all.</summary>
    [Test]
    public void SearchText_SkipsABinaryFile()
    {
        var workspace = CreateWorkspace();
        File.WriteAllBytes(Path.Combine(workspace, "blob.bin"), [.. "needle"u8.ToArray(), 0x00, .. "needle"u8.ToArray()]);
        Write(workspace, "text.txt", "needle\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, "needle", maxOutputBytes: 65536, NeverSuppressed, CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./text.txt:1:needle", matches[0]);
    }

    [Test]
    public void SearchText_StopsAtTheOutputByteCeiling()
    {
        var workspace = CreateWorkspace();
        var builder = new StringBuilder();
        for (var index = 0; index < 500; index++)
        {
            _ = builder.Append("needle line ").Append(index.ToString("D4", CultureInfo.InvariantCulture)).Append('\n');
        }

        Write(workspace, "big.txt", builder.ToString());

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, "needle", maxOutputBytes: 256, NeverSuppressed, CancellationToken.None);

        AssertEx.NotEmpty(matches);
        AssertEx.True(matches.Sum(match => Encoding.UTF8.GetByteCount(match) + 1) < 256 + 128,
            "the search must stop once its output budget is reached rather than rendering every match");
    }

    /// <summary>
    ///     A CRLF repository is the case this whole Windows pass exists for: the record separator there is two bytes,
    ///     and leaving the CR on the emitted line would put a stray control character into every match the model reads.
    /// </summary>
    [Test]
    public void SearchText_StripsTheCarriageReturnOnACrlfFile()
    {
        var workspace = CreateWorkspace();
        Write(workspace, "crlf.txt", "alpha\r\nneedle here\r\ngamma\r\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, "needle", maxOutputBytes: 65536, NeverSuppressed, CancellationToken.None);

        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./crlf.txt:2:needle here", matches[0]);
    }

    /// <summary>
    ///     A generated or hostile repository can hold a single line of arbitrary length, and buffering it whole would
    ///     let one file allocate its full size inside the engine. The line is bounded — and it still counts as ONE
    ///     line, so every later line number stays truthful.
    /// </summary>
    [Test]
    public void SearchText_BoundsAnOverlongLineAndKeepsLaterLineNumbersCorrect()
    {
        var workspace = CreateWorkspace();
        var overlong = new string('a', DevelopmentWorkspaceFileScanner.MaxSearchLineChars + 4096) + "needle";
        Write(workspace, "long.txt", overlong + "\nsecond needle\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, "needle", maxOutputBytes: 1024 * 1024, NeverSuppressed, CancellationToken.None);

        // The match past the bound is not reported — the tail of the line was never buffered.
        AssertEx.Equal(1, matches.Count);
        AssertEx.Equal("./long.txt:2:second needle", matches[0]);
    }

    [Test]
    public void SearchText_WithAnEmptyPatternReturnsNothingRatherThanEveryLineInTheWorkspace()
    {
        var workspace = CreateWorkspace();
        Write(workspace, "a.txt", "alpha\nbeta\n");

        var matches = DevelopmentWorkspaceFileScanner.SearchText(workspace, string.Empty, maxOutputBytes: 65536, NeverSuppressed, CancellationToken.None);

        AssertEx.Empty(matches);
    }

    private static bool NeverSuppressed(string relativePath) => false;

    private string CreateWorkspace()
    {
        var workspace = Path.Combine(_root, "workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static void Write(string workspace, string relativePath, string content)
    {
        var full = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
