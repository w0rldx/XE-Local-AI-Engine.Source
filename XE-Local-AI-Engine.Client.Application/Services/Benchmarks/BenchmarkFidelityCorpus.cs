namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using System.Globalization;
using System.Security.Cryptography;

/// <summary>
///     The wikitext-2-raw test split shipped with the app, and the identity two perplexity numbers must share before
///     they may be compared. Resolved once per process: the file is 1.3 MB and its hash never changes for a build.
/// </summary>
public static class BenchmarkFidelityCorpus
{
    /// <summary>The name the corpus is linked under in the publish output (see the Client csproj).</summary>
    private const string PublishedDirectoryName = "benchmark-corpus";

    private const string RepositoryRelativePath = "tools/benchmark/corpus";
    private const string FileName = "wikitext2-raw-test.txt";
    private const string CorpusName = "wikitext2-raw-test";

    private static readonly Lazy<BenchmarkFidelityCorpusFile> Resolved = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Throws when the corpus did not ship — a fidelity measurement with no corpus is not a measurement.</summary>
    public static BenchmarkFidelityCorpusFile Require() =>
        Resolved.Value;

    /// <summary>Test seam: hashes an arbitrary file the same way, so a fixture corpus carries a real identity too.</summary>
    public static BenchmarkFidelityCorpusFile Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new BenchmarkFidelityCorpusFile
        {
            Path = path,
            Sha256 = sha256,
            CorpusId = string.Create(CultureInfo.InvariantCulture, $"{CorpusName}@{sha256[..12]}")
        };
    }

    private static BenchmarkFidelityCorpusFile Load()
    {
        var published = Path.Combine(AppContext.BaseDirectory, PublishedDirectoryName, FileName);
        if (File.Exists(published))
        {
            return Read(published);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, RepositoryRelativePath, FileName);
            if (File.Exists(candidate))
            {
                return Read(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("The benchmark perplexity corpus did not ship with this build.", published);
    }
}
