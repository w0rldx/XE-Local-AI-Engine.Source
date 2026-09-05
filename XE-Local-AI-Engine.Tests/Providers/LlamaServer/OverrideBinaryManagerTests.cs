namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Bring-your-own override branch of <see cref="LlamaCppBinaryManager.EnsureBinaryAsync" />: an active, valid override
///     is validated and served WITHOUT any download; the returned variant is the override's own (not the caller's); a
///     missing/relative/non-regular-file/world-writable/smoke-failing/GPU-less override is rejected with a sanitized
///     failure and never falls through to acquisition or a silent CPU run; an unset override acquires as before. The
///     validation spawns a real executable stub, so these are POSIX-only.
/// </summary>
public sealed class OverrideBinaryManagerTests
{
    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_WhenOverrideValid_ReturnsItWithoutDownload()
    {
        using var dir = new TempDir();
        var serverPath = WriteExecutableStub(dir.Path, GpuStub);
        var options = ActiveOverride(serverPath, GpuVariant.Cpu);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal("override", binary.Version);
        AssertEx.Equal(GpuVariant.Cpu, binary.Variant);
        AssertEx.Equal(serverPath, binary.ServerExecutablePath);
        AssertEx.False(binary.IsPinnedFallback);
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_WhenOverrideReturnsOptionsVariant_NotCallerVariant()
    {
        // The caller passes Cpu, but the override is configured as Cuda → the returned variant is Cuda.
        // The GPU stub enumerates a device so the Cuda backend check passes.
        using var dir = new TempDir();
        var serverPath = WriteExecutableStub(dir.Path, GpuStub);
        var options = ActiveOverride(serverPath, GpuVariant.Cuda);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cuda, binary.Variant);
    }

    [Test]
    public async Task EnsureBinary_WhenOverrideMissingFile_ThrowsSanitized()
    {
        using var dir = new TempDir();
        var missing = Path.Combine(dir.Path, "does-not-exist", "llama-server");
        var options = ActiveOverride(missing, GpuVariant.Cpu);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        var exception = await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));
        AssertEx.False(exception.Message.Contains(dir.Path, StringComparison.Ordinal));
    }

    [Test]
    public async Task EnsureBinary_WhenOverrideRelativeOrNonRegularFile_Throws()
    {
        using var dir = new TempDir();

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);

        // Relative path → rejected (no base dir to resolve it against).
        var relative = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null,
            overrideOptions: ActiveOverride("relative/llama-server", GpuVariant.Cpu));
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => relative.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));

        // A directory is not a regular file → rejected (File.Exists is false for a directory).
        var asDir = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null,
            overrideOptions: ActiveOverride(dir.Path, GpuVariant.Cpu));
        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => asDir.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_WhenOverrideWorldWritableOrForeignOwned_Throws()
    {
        // A world-writable binary is rejected (TOCTOU swap hardening). (Foreign-uid ownership cannot be
        // arranged in CI without root, so the world-writable arm is the testable compensating control.)
        using var dir = new TempDir();
        var serverPath = WriteExecutableStub(dir.Path, GpuStub);
        // Make the binary world-writable (o+w) — anyone could swap it.
        File.SetUnixFileMode(serverPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite);

        var options = ActiveOverride(serverPath, GpuVariant.Cpu);
        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_WhenOverrideSmokeFails_Throws()
    {
        using var dir = new TempDir();
        var serverPath = WriteExecutableStub(dir.Path, SmokeFailStub);
        var options = ActiveOverride(serverPath, GpuVariant.Cpu);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_WhenOverrideVariantCudaButNoGpuDevice_Throws()
    {
        // A binary that passes --version but enumerates no GPU device cannot be served as Cuda.
        using var dir = new TempDir();
        var serverPath = WriteExecutableStub(dir.Path, NoGpuStub);
        var options = ActiveOverride(serverPath, GpuVariant.Cuda);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        await AssertEx.ThrowsAsync<LlamaRuntimeException>(() => manager.EnsureBinaryAsync(GpuVariant.Cuda, CancellationToken.None));
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task EnsureBinary_WhenOverrideVariantCpu_SkipsGpuDeviceCheck()
    {
        // variant=cpu skips the GPU-device requirement: the stub FAILS --list-devices, but a Cpu override never runs it.
        using var dir = new TempDir();
        var serverPath = WriteExecutableStub(dir.Path, DeviceCheckWouldFailStub);
        var options = ActiveOverride(serverPath, GpuVariant.Cpu);

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, dir.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null, overrideOptions: options);

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal("override", binary.Version);
        AssertEx.Equal(GpuVariant.Cpu, binary.Variant);
    }

    [Test]
    public async Task EnsureBinary_WhenOverrideUnset_AcquiresAsBefore()
    {
        // An inactive override (no path) leaves the 3-tier acquisition intact: a pre-seeded pinned binary is reused
        // offline with zero downloads, exactly as without the override feature.
        using var cache = new TempDir();
        var pin = LlamaCppReleasePins.Resolve(OSPlatform.Linux, Architecture.X64, GpuVariant.Cpu)!;
        var serverPath = Path.Combine(cache.Path, "llama.cpp", LlamaCppReleasePins.PinnedTag, "cpu", pin.ServerRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        await File.WriteAllTextAsync(serverPath, "pinned-binary");

        using var handler = new ThrowingHandler();
        using var http = new HttpClient(handler, disposeHandler: false);
        var manager = new LlamaCppBinaryManager(http, cache.Path, LlamaCppReleasePins.PinnedTag,
            OSPlatform.Linux, Architecture.X64, catalog: null, installedRuntimeStore: null,
            overrideOptions: new LlamaServerRuntimeOverrideOptions()); // inactive

        var binary = await manager.EnsureBinaryAsync(GpuVariant.Cpu, CancellationToken.None);

        AssertEx.Equal(LlamaCppReleasePins.PinnedTag, binary.Version);
        AssertEx.True(binary.IsPinnedFallback);
    }

    private static LlamaServerRuntimeOverrideOptions ActiveOverride(string serverPath, GpuVariant variant)
    {
        return new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = serverPath,
            Variant = variant
        };
    }

    // A stub that branches on the llama.cpp arg: --version passes; --list-devices enumerates a GPU device.
    private const string GpuStub =
        "#!/bin/sh\ncase \"$1\" in\n  --version) echo 'version: test'; exit 0 ;;\n  --list-devices) echo 'Available devices:'; echo '  CUDA0: Test GPU (24000 MiB, 23000 MiB free)'; exit 0 ;;\n  *) exit 0 ;;\nesac\n";

    // --version passes; --list-devices enumerates NO GPU device.
    private const string NoGpuStub = "#!/bin/sh\ncase \"$1\" in\n  --version) echo 'version: test'; exit 0 ;;\n  --list-devices) echo 'Available devices:'; exit 0 ;;\n  *) exit 0 ;;\nesac\n";

    // --version FAILS (non-zero) → smoke test fails.
    private const string SmokeFailStub = "#!/bin/sh\ncase \"$1\" in\n  --version) exit 1 ;;\n  *) exit 0 ;;\nesac\n";

    // --version passes; --list-devices FAILS — proves a Cpu override never runs the device check.
    private const string DeviceCheckWouldFailStub = "#!/bin/sh\ncase \"$1\" in\n  --version) echo 'version: test'; exit 0 ;;\n  --list-devices) exit 3 ;;\n  *) exit 0 ;;\nesac\n";

    [UnsupportedOSPlatform("windows")]
    private static string WriteExecutableStub(string dir, string script)
    {
        var path = Path.Combine(dir, "llama-server");
        File.WriteAllText(path, script);
        // 0755: executable, owner-writable, NOT world-writable.
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("No network call expected: an active override must never download.");
        }
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-llama-override-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
            // 0755 dir: NOT world-writable, so the parent-dir security check passes regardless of the CI umask.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
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
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
