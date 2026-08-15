namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class ConvertScriptProvisionerTests
{
    [Test]
    public void TryResolve_BeforeProvisioning_ReturnsNull()
    {
        using var temp = new TempDirectory();
        using var provisioner = new ConvertScriptProvisioner(new FakeFetcher(), NullLogger<ConvertScriptProvisioner>.Instance, temp.Path);

        AssertEx.Null(provisioner.TryResolve());
    }

    [Test]
    public async Task EnsureAsync_CopiesTheThreePinnedPaths_AndAdoptsUnderTheCommit()
    {
        using var temp = new TempDirectory();
        var fetcher = new FakeFetcher();
        using var provisioner = new ConvertScriptProvisioner(fetcher, NullLogger<ConvertScriptProvisioner>.Instance, temp.Path);

        var paths = await provisioner.EnsureAsync(CancellationToken.None);

        AssertEx.Equal(LlamaCppReleasePins.PinnedSourceCommitSha, paths.SourceCommit);
        AssertEx.True(File.Exists(paths.HfToGgufScriptPath), "convert_hf_to_gguf.py must be provisioned.");
        AssertEx.True(File.Exists(paths.LoraToGgufScriptPath), "convert_lora_to_gguf.py must be provisioned.");
        AssertEx.True(Directory.Exists(paths.GgufPyDirectory), "gguf-py must be provisioned.");
        AssertEx.True(File.Exists(Path.Combine(paths.GgufPyDirectory, "gguf", "__init__.py")),
            "The gguf-py package must be copied recursively, not just its top directory.");

        var commitRoot = Path.Combine(temp.Path, "llama.cpp", "convert-scripts", LlamaCppReleasePins.PinnedSourceCommitSha);
        AssertEx.Equal(commitRoot, Path.GetDirectoryName(paths.HfToGgufScriptPath));

        // Only the three needed paths — the rest of the fetched tree is discarded with the work directory.
        AssertEx.False(File.Exists(Path.Combine(commitRoot, "unrelated.cpp")), "Unrelated source must not be adopted.");
        AssertEx.False(Directory.Exists(Path.Combine(temp.Path, "llama.cpp", "convert-scripts", ".work")),
            "The work directory must not survive a successful provisioning.");
    }

    [Test]
    public async Task EnsureAsync_WhenAlreadyProvisioned_DoesNotRefetch()
    {
        using var temp = new TempDirectory();
        var fetcher = new FakeFetcher();
        using var provisioner = new ConvertScriptProvisioner(fetcher, NullLogger<ConvertScriptProvisioner>.Instance, temp.Path);

        _ = await provisioner.EnsureAsync(CancellationToken.None);
        _ = await provisioner.EnsureAsync(CancellationToken.None);

        AssertEx.Equal(expected: 1, fetcher.FetchCount);
        AssertEx.NotNull(provisioner.TryResolve());
    }

    [Test]
    public async Task EnsureAsync_WhenFetchedCommitDiffers_RejectsAndAdoptsNothing()
    {
        using var temp = new TempDirectory();
        var fetcher = new FakeFetcher
        {
            ReportedCommit = new string('b', 40)
        };
        using var provisioner = new ConvertScriptProvisioner(fetcher, NullLogger<ConvertScriptProvisioner>.Instance, temp.Path);

        _ = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => provisioner.EnsureAsync(CancellationToken.None));

        AssertEx.Null(provisioner.TryResolve());
    }

    [Test]
    public async Task EnsureAsync_WhenAScriptIsMissing_RejectsAndAdoptsNothing()
    {
        using var temp = new TempDirectory();
        var fetcher = new FakeFetcher
        {
            OmitLoraScript = true
        };
        using var provisioner = new ConvertScriptProvisioner(fetcher, NullLogger<ConvertScriptProvisioner>.Instance, temp.Path);

        _ = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => provisioner.EnsureAsync(CancellationToken.None));

        AssertEx.Null(provisioner.TryResolve());
        AssertEx.False(Directory.Exists(Path.Combine(temp.Path, "llama.cpp", "convert-scripts", ".staging")),
            "A rejected acquisition must leave no staging directory behind.");
    }

    private sealed class FakeFetcher : IConvertScriptSourceFetcher
    {
        public int FetchCount { get; private set; }
        public string? ReportedCommit { get; init; }
        public bool OmitLoraScript { get; init; }

        public async Task<string> FetchAsync(string destinationDirectory, string commitSha, CancellationToken ct)
        {
            FetchCount++;
            _ = Directory.CreateDirectory(destinationDirectory);
            await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "convert_hf_to_gguf.py"), "# hf\n", ct);
            if (!OmitLoraScript)
            {
                await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "convert_lora_to_gguf.py"), "# lora\n", ct);
            }

            await File.WriteAllTextAsync(Path.Combine(destinationDirectory, "unrelated.cpp"), "int main(){}\n", ct);
            var package = Path.Combine(destinationDirectory, "gguf-py", "gguf");
            _ = Directory.CreateDirectory(package);
            await File.WriteAllTextAsync(Path.Combine(package, "__init__.py"), "\n", ct);
            return ReportedCommit ?? commitSha;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leaked temp directory is reclaimed by the OS.
            }
        }
    }
}
