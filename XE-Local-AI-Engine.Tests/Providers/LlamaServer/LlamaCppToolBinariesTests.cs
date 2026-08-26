namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Covers the by-name resolution of the llama.cpp helper executables that sit beside <c>llama-server</c>. They are
///     located on disk rather than recorded, so the regression to guard is a resolver that reports a helper present when
///     the file is not there (the fidelity/export paths would then spawn a missing binary) or absent when it is.
/// </summary>
public sealed class LlamaCppToolBinariesTests
{
    [Test]
    public void PerplexityFileName_CarriesTheExeSuffixOnWindowsOnly()
    {
        AssertEx.Equal(OperatingSystem.IsWindows() ? "llama-perplexity.exe" : "llama-perplexity",
            LlamaCppToolBinaries.PerplexityFileName);
        AssertEx.Equal("llama-perplexity", LlamaCppToolBinaries.PerplexityName);
    }

    [Test]
    public void TryResolvePerplexity_ReturnsThePath_WhenTheToolIsPresent()
    {
        using var dir = new TempDirectory();
        var expected = Path.Combine(dir.Path, LlamaCppToolBinaries.PerplexityFileName);
        File.WriteAllText(expected, "perplexity");

        AssertEx.Equal(expected, LlamaCppToolBinaries.TryResolvePerplexity(dir.Path));
    }

    [Test]
    public void TryResolvePerplexity_ReturnsNull_ForAnAbsentToolOrABlankDirectory()
    {
        using var dir = new TempDirectory();

        AssertEx.Null(LlamaCppToolBinaries.TryResolvePerplexity(dir.Path));
        AssertEx.Null(LlamaCppToolBinaries.TryResolvePerplexity(null));
        AssertEx.Null(LlamaCppToolBinaries.TryResolvePerplexity("   "));
    }

    [Test]
    public void PerplexityExecutablePath_ResolvesBesideTheServer_AndIsNullWithoutIt()
    {
        using var dir = new TempDirectory();
        var server = Path.Combine(dir.Path, "llama-server");
        File.WriteAllText(server, "server");
        var binary = new LlamaBinary(server, "b10201", GpuVariant.Cuda, IsPinnedFallback: true);

        // Evaluated on read: the same record flips from null to a path the moment the helper lands beside the server.
        AssertEx.Null(binary.PerplexityExecutablePath);
        AssertEx.Null(LlamaCppToolBinaries.TryResolvePerplexityBesideServer(null));

        var expected = Path.Combine(dir.Path, LlamaCppToolBinaries.PerplexityFileName);
        File.WriteAllText(expected, "perplexity");
        AssertEx.Equal(expected, binary.PerplexityExecutablePath);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-tool-binaries-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (Exception)
            {
                /* Best-effort test cleanup. */
            }
        }
    }
}
