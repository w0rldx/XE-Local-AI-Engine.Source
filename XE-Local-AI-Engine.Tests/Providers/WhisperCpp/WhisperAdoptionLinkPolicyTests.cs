namespace XE_Local_AI_Engine.Tests.Providers.WhisperCpp;

using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Adoption's symlink policy, which cannot be inherited from the image-runtime precedent and had to be written.
/// </summary>
/// <remarks>
///     A whisper.cpp build's output legitimately contains SONAME chains, so rejecting links outright would fail every
///     honest build. Accepting them outright is worse: adoption hardens the tree by setting permissions on what it
///     walks, so a link escaping the staging root would have it chmod a file outside. The rule is therefore
///     relative-and-inside is accepted, anything else fails the adoption BEFORE anything is modified.
/// </remarks>
[RunOn(OS.Linux)]
[UnsupportedOSPlatform("windows")]
public sealed class WhisperAdoptionLinkPolicyTests
{
    [Test]
    public async Task Adoption_RelativeSonameChainInsideTheTree_IsAccepted()
    {
        // The real shape of a whisper.cpp build output: libwhisper.so → libwhisper.so.1 → libwhisper.so.1.9.4.
        // Adoption must complete and the chain must still resolve afterwards, at its new location.
        SymlinkSupport.EnsureSupported();
        using var cache = new TempDirectory();
        var harness = new Harness(cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        File.CreateSymbolicLink(Path.Combine(bin, "libwhisper.so.1"), "libwhisper.so.1.9.4");
        File.CreateSymbolicLink(Path.Combine(bin, "libwhisper.so"), "libwhisper.so.1");

        await harness.Adoption.AdoptAsync(buildDir, Path.Combine(bin, "whisper-server"), harness.Descriptor, CancellationToken.None);

        var installedBin = Path.Combine(harness.InstallRoot, "bin");
        AssertEx.True(File.Exists(Path.Combine(installedBin, "whisper-server")), "The adopted runtime must be in place.");

        // Reading through the two-hop chain is the honest check: it proves both links survived the move AND still
        // point at real bytes, which is what the runtime needs at spawn.
        var throughTheChain = await File.ReadAllTextAsync(Path.Combine(installedBin, "libwhisper.so"));
        AssertEx.Equal("library", throughTheChain);
        AssertEx.NotNull(new FileInfo(Path.Combine(installedBin, "libwhisper.so")).LinkTarget);

        var record = AssertEx.NotNull(await harness.Store.ReadAsync(CancellationToken.None));
        AssertEx.Equal(WhisperInstalledRuntimeValidity.Active, record.Validity);
    }

    [Test]
    public async Task Adoption_LinkEscapingTheStagingTree_IsRejected_AndTheOutsideTargetIsUntouched()
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

        var harness = new Harness(cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        File.CreateSymbolicLink(Path.Combine(bin, "escape.so"), Path.GetRelativePath(bin, outsideFile));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() =>
            harness.Adoption.AdoptAsync(buildDir, Path.Combine(bin, "whisper-server"), harness.Descriptor, CancellationToken.None));

        AssertEx.Equal("The source build produced a runtime containing an unsafe link.", exception.Message);
        AssertEx.Equal("do not touch", await File.ReadAllTextAsync(outsideFile));
        AssertEx.Equal(modeBefore, File.GetUnixFileMode(outsideFile), "Hardening must never have run: the outside file's mode is unchanged.");
        AssertEx.False(Directory.Exists(harness.InstallRoot), "A rejected build must not be adopted.");
        AssertEx.Null(await harness.Store.ReadAsync(CancellationToken.None), "A rejected build must not publish a runtime record.");
    }

    [Test]
    public async Task Adoption_AbsoluteLinkInsideTheTree_IsStillRejected()
    {
        // An absolute link may happen to point inside today and somewhere else after the tree is moved, so it is
        // rejected on its form rather than on where it currently lands.
        SymlinkSupport.EnsureSupported();
        using var cache = new TempDirectory();
        var harness = new Harness(cache.Path);
        var buildDir = harness.BuildTree();
        var bin = Path.Combine(buildDir, "bin");
        File.CreateSymbolicLink(Path.Combine(bin, "libwhisper.so"), Path.Combine(bin, "libwhisper.so.1.9.4"));

        var exception = await AssertEx.ThrowsAsync<WhisperRuntimeException>(() =>
            harness.Adoption.AdoptAsync(buildDir, Path.Combine(bin, "whisper-server"), harness.Descriptor, CancellationToken.None));

        AssertEx.Equal("The source build produced a runtime containing an unsafe link.", exception.Message);
        AssertEx.False(Directory.Exists(harness.InstallRoot));
    }

    private sealed class Harness
    {
        private readonly string _cacheRoot;

        public Harness(string cacheRoot)
        {
            _cacheRoot = cacheRoot;
            Store = new WhisperInstalledRuntimeStore(cacheRoot);
            Adoption = new WhisperCppRuntimeAdoption(cacheRoot, Store, new WhisperManagedSourceBuildSignal(), NullLogger.Instance);
            // Custom + ExplicitCommit: the runtime store requires an engine-pinned record to carry the pinned
            // release commit exactly, and this fixture's commit is arbitrary on purpose.
            var commit = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("commit")))[..40];
            Descriptor = new WhisperCppSourceBuildDescriptor(WhisperBackend.Cuda,
                WhisperCppSourceSelection.Custom,
                WhisperCppSourceBuildRequestValidation.OfficialRepository,
                WhisperCppSourceRevisionMode.ExplicitCommit,
                commit,
                commit)
            {
                BuildId = Guid.NewGuid()
            };
        }

        public WhisperInstalledRuntimeStore Store { get; }

        public WhisperCppRuntimeAdoption Adoption { get; }

        public WhisperCppSourceBuildDescriptor Descriptor { get; }

        public string InstallRoot => Path.Combine(_cacheRoot, "whisper.cpp", "managed", "cuda", Descriptor.ResolvedCommit!);

        public string BuildTree()
        {
            var buildDir = Path.Combine(_cacheRoot, "work", "build-" + Guid.NewGuid().ToString("N"));
            var bin = Path.Combine(buildDir, "bin");
            Directory.CreateDirectory(bin);
            var server = Path.Combine(bin, "whisper-server");
            File.WriteAllText(server, "server");
            File.WriteAllText(Path.Combine(bin, "libwhisper.so.1.9.4"), "library");
            File.SetUnixFileMode(server, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return buildDir;
        }
    }
}
