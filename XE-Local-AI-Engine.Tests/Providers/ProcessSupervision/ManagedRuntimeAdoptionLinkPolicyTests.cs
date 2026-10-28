namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Adoption's symlink policy and staging hardening, proven for both managed source-built runtimes.
/// </summary>
/// <remarks>
///     A source build's output legitimately contains SONAME chains, so rejecting links outright would fail every
///     honest build. Accepting them outright is worse: adoption hardens the tree by setting permissions on what it
///     walks, so a link escaping the staging root would have it chmod a file outside. The rule is therefore
///     relative-and-inside is accepted, anything else fails the adoption BEFORE anything is modified.
/// </remarks>
[RunOn(OS.Linux)]
[UnsupportedOSPlatform("windows")]
[Category(TestCategories.Unit)]
public sealed class ManagedRuntimeAdoptionLinkPolicyTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public enum Runtime
    {
        Transcription,
        Image
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public async Task Adoption_RelativeSonameChainInsideTheTree_IsAccepted(Runtime runtime)
    {
        // The real shape of a build output: libggml.so → libggml.so.1 → libggml.so.1.9.4. Adoption must complete and
        // the chain must still resolve afterwards, at its new location.
        SymlinkSupport.EnsureSupported();
        using var cache = new TempDirectory();
        using var harness = new Harness(runtime, cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        File.CreateSymbolicLink(Path.Combine(bin, "libggml.so.1"), "libggml.so.1.9.4");
        File.CreateSymbolicLink(Path.Combine(bin, "libggml.so"), "libggml.so.1");

        await harness.AdoptAsync(buildDir);

        var installedBin = Path.Combine(harness.InstallRoot, "bin");
        AssertEx.True(File.Exists(Path.Combine(installedBin, harness.ServerName)), "The adopted runtime must be in place.");

        // Reading through the two-hop chain is the honest check: it proves both links survived the move AND still
        // point at real bytes, which is what the runtime needs at spawn.
        var throughTheChain = await File.ReadAllTextAsync(Path.Combine(installedBin, "libggml.so"));
        AssertEx.Equal("library", throughTheChain);
        AssertEx.NotNull(new FileInfo(Path.Combine(installedBin, "libggml.so")).LinkTarget);
        AssertEx.Equal(ManagedRuntimeValidity.Active, await harness.ReadValidityAsync());
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public async Task Adoption_LinkEscapingTheStagingTree_IsRejected_AndTheOutsideTargetIsUntouched(Runtime runtime)
    {
        // The attack the policy exists for. The link is relative but climbs out of the tree; if hardening ran first it
        // would chmod the outside file through it.
        SymlinkSupport.EnsureSupported();
        using var cache = new TempDirectory();
        using var outside = new TempDirectory();
        var outsideFile = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "do not touch");
        File.SetUnixFileMode(outsideFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var modeBefore = File.GetUnixFileMode(outsideFile);

        using var harness = new Harness(runtime, cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        File.CreateSymbolicLink(Path.Combine(bin, "escape.so"), Path.GetRelativePath(bin, outsideFile));

        await harness.AssertUnsafeLinkRejectedAsync(buildDir);

        AssertEx.Equal("do not touch", await File.ReadAllTextAsync(outsideFile));
        AssertEx.Equal(modeBefore, File.GetUnixFileMode(outsideFile), "Hardening must never have run: the outside file's mode is unchanged.");
        AssertEx.False(Directory.Exists(harness.InstallRoot), "A rejected build must not be adopted.");
        AssertEx.Null(await harness.ReadValidityAsync(), "A rejected build must not publish a runtime record.");
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public async Task Adoption_DirectoryLinkEscapingTheStagingTree_IsRejected_AndNotWalked(Runtime runtime)
    {
        // A directory link is judged as a link, never descended into: walking through it would harden the outside
        // directory and every file under it.
        SymlinkSupport.EnsureSupported();
        using var cache = new TempDirectory();
        using var outside = new TempDirectory();
        var outsideFile = Path.Combine(outside.Path, "secret.txt");
        await File.WriteAllTextAsync(outsideFile, "do not touch");
        var permissive = OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(outside.Path, permissive);
        File.SetUnixFileMode(outsideFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var fileModeBefore = File.GetUnixFileMode(outsideFile);

        using var harness = new Harness(runtime, cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        Directory.CreateSymbolicLink(Path.Combine(bin, "plugins"), Path.GetRelativePath(bin, outside.Path));

        await harness.AssertUnsafeLinkRejectedAsync(buildDir);

        AssertEx.Equal(permissive, File.GetUnixFileMode(outside.Path), "The outside directory must not have been hardened.");
        AssertEx.Equal(fileModeBefore, File.GetUnixFileMode(outsideFile), "No file under the outside directory may have been hardened.");
        AssertEx.False(Directory.Exists(harness.InstallRoot), "A rejected build must not be adopted.");
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public async Task Adoption_AbsoluteLinkInsideTheTree_IsStillRejected(Runtime runtime)
    {
        // An absolute link may happen to point inside today and somewhere else after the tree is moved, so it is
        // rejected on its form rather than on where it currently lands.
        SymlinkSupport.EnsureSupported();
        using var cache = new TempDirectory();
        using var harness = new Harness(runtime, cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        File.CreateSymbolicLink(Path.Combine(bin, "libggml.so"), Path.Combine(bin, "libggml.so.1.9.4"));

        await harness.AssertUnsafeLinkRejectedAsync(buildDir);

        AssertEx.False(Directory.Exists(harness.InstallRoot));
    }

    [Test]
    [Arguments(Runtime.Transcription)]
    [Arguments(Runtime.Image)]
    public async Task Adoption_HardensEveryDirectoryToOwnerOnly_AndStripsGroupAndOtherFromFiles(Runtime runtime)
    {
        // A build tree arrives with the umask's group and other bits; the adopted runtime keeps none of them, and the
        // managed roots above it are owner-only too.
        using var cache = new TempDirectory();
        using var harness = new Harness(runtime, cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        var permissive = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;
        File.SetUnixFileMode(buildDir, OwnerOnly | permissive | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(bin, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.SetUnixFileMode(Path.Combine(bin, "libggml.so.1.9.4"), UnixFileMode.UserRead | UnixFileMode.UserWrite | permissive);

        await harness.AdoptAsync(buildDir);

        var installedBin = Path.Combine(harness.InstallRoot, "bin");
        foreach (var directory in new[] { harness.ManagedRoot, Path.GetDirectoryName(harness.InstallRoot)!, harness.InstallRoot, installedBin })
        {
            AssertEx.Equal(OwnerOnly, File.GetUnixFileMode(directory), $"{Path.GetFileName(directory)} must be owner-only.");
        }

        AssertEx.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(installedBin, "libggml.so.1.9.4")));
        AssertEx.Equal(OwnerOnly, File.GetUnixFileMode(Path.Combine(installedBin, harness.ServerName)), "The server keeps only its owner execute bit.");
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _cacheRoot;
        private readonly string _runtimeName;
        private readonly string _backendSlug;
        private readonly string _commit;
        private readonly Func<string, Task> _adopt;
        private readonly Func<Task<ManagedRuntimeValidity?>> _readValidity;
        private readonly Type _exceptionType;
        private readonly IDisposable _store;

        public Harness(Runtime runtime, string cacheRoot)
        {
            _cacheRoot = cacheRoot;
            // Custom + ExplicitCommit: the runtime store requires an engine-pinned record to carry the pinned
            // release commit exactly, and this fixture's commit is arbitrary on purpose.
            _commit = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("commit")))[..40];
            if (runtime == Runtime.Transcription)
            {
                var store = new WhisperInstalledRuntimeStore(cacheRoot);
                var adoption = new WhisperCppRuntimeAdoption(cacheRoot, store, new ManagedSourceBuildSignal<WhisperBackend>(), TimeProvider.System, NullLogger.Instance);
                var descriptor = new WhisperCppSourceBuildDescriptor
                {
                    Backend = WhisperBackend.Cuda,
                    Source = ManagedSourceSelection.Custom,
                    Repository = WhisperCppSourceBuildRequestValidation.OfficialRepository,
                    RevisionMode = ManagedSourceRevisionMode.ExplicitCommit,
                    RequestedCommit = _commit,
                    ResolvedCommit = _commit,
                    BuildId = Guid.NewGuid()
                };
                _store = store;
                _runtimeName = "whisper.cpp";
                _backendSlug = "cuda";
                ServerName = "whisper-server";
                _exceptionType = typeof(WhisperRuntimeException);
                _adopt = buildDir => adoption.AdoptAsync(buildDir, Path.Combine(buildDir, "bin", ServerName), descriptor, CancellationToken.None);
                _readValidity = async () => (await store.ReadAsync(CancellationToken.None))?.Validity;
            }
            else
            {
                var store = new StableDiffusionInstalledRuntimeStore(cacheRoot);
                var adoption = new StableDiffusionCppRuntimeAdoption(cacheRoot, store, new ManagedSourceBuildSignal<SdGpuBackend>(), TimeProvider.System, NullLogger.Instance);
                var descriptor = new StableDiffusionCppSourceBuildDescriptor
                {
                    Backend = SdGpuBackend.Vulkan,
                    Source = ManagedSourceSelection.Custom,
                    Repository = StableDiffusionCppSourceBuildRequestValidation.OfficialRepository,
                    RevisionMode = ManagedSourceRevisionMode.ExplicitCommit,
                    RequestedCommit = _commit,
                    ResolvedCommit = _commit,
                    BuildId = Guid.NewGuid()
                };
                _store = store;
                _runtimeName = "stable-diffusion.cpp";
                _backendSlug = "vulkan";
                ServerName = "sd-server";
                _exceptionType = typeof(StableDiffusionRuntimeException);
                _adopt = buildDir => adoption.AdoptAsync(buildDir, Path.Combine(buildDir, "bin", ServerName), descriptor, CancellationToken.None);
                _readValidity = async () => (await store.ReadAsync(CancellationToken.None))?.Validity;
            }
        }

        public string ServerName { get; }

        public string ManagedRoot => Path.Combine(_cacheRoot, _runtimeName, "managed");

        public string InstallRoot => Path.Combine(ManagedRoot, _backendSlug, _commit);

        public Task AdoptAsync(string buildDir) => _adopt(buildDir);

        public Task<ManagedRuntimeValidity?> ReadValidityAsync() => _readValidity();

        public async Task AssertUnsafeLinkRejectedAsync(string buildDir)
        {
            var exception = await AssertEx.ThrowsAsync<Exception>(() => _adopt(buildDir));
            AssertEx.Equal(_exceptionType, exception.GetType());
            AssertEx.Equal("The source build produced a runtime containing an unsafe link.", exception.Message);
        }

        public string BuildTree()
        {
            var buildDir = Path.Combine(_cacheRoot, "work", "build-" + Guid.NewGuid().ToString("N"));
            var bin = Path.Combine(buildDir, "bin");
            Directory.CreateDirectory(bin);
            var server = Path.Combine(bin, ServerName);
            File.WriteAllText(server, "server");
            File.WriteAllText(Path.Combine(bin, "libggml.so.1.9.4"), "library");
            File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return buildDir;
        }

        public void Dispose() => _store.Dispose();
    }
}
