namespace XE_Local_AI_Engine.Tests.Providers.Abstractions;

using System.Runtime.Versioning;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     The fallback that stops the source-build checklists being stricter than the build they gate: <c>nvcc</c> sitting
///     in NVIDIA's conventional <c>/usr/local/cuda/bin</c> without that directory on PATH was reported "Missing", while
///     CMake would have found the same toolkit on its own.
///     <para>
///         Every case pins the environment the locator reads — PATH and an explicit CUDA root — so the answers are a
///         property of the test rather than of whatever the gate machine happens to have installed. Serialized against
///         the other environment-mutating suites for the same reason.
///     </para>
/// </summary>
[NotInParallel]
[Category(TestCategories.Unit)]
[RunOn(OS.Linux)]
[UnsupportedOSPlatform("windows")]
public sealed class CudaToolkitLocatorTests
{
    [Test]
    public void FindNvccOutsidePath_WhenPathAlreadyResolvesNvcc_AnswersNothingToCorrect()
    {
        using var temp = new TempDirectory();
        WriteExecutable(temp.Path, "nvcc");

        // A CUDA root that WOULD answer, to prove PATH is what short-circuits rather than the absence of a candidate.
        var cudaBin = CreateCudaRoot(temp.Path, withNvcc: true);
        using var path = new EnvironmentScope("PATH", temp.Path);
        using var cudaHome = new EnvironmentScope("CUDA_HOME", Path.GetDirectoryName(cudaBin));
        using var cudaPath = new EnvironmentScope("CUDA_PATH", value: null);

        AssertEx.Null(CudaToolkitLocator.FindNvccOutsidePath(),
            "A host whose PATH already resolves nvcc must keep spawning the bare name, exactly as before.");
    }

    [Test]
    public void FindNvccOutsidePath_WhenNvccIsOnlyUnderTheConfiguredRoot_ReturnsThatAbsolutePath()
    {
        using var temp = new TempDirectory();
        var cudaBin = CreateCudaRoot(temp.Path, withNvcc: true);
        using var path = new EnvironmentScope("PATH", temp.Path);
        using var cudaHome = new EnvironmentScope("CUDA_HOME", Path.GetDirectoryName(cudaBin));
        using var cudaPath = new EnvironmentScope("CUDA_PATH", value: null);

        AssertEx.Equal(Path.Combine(cudaBin, "nvcc"), CudaToolkitLocator.FindNvccOutsidePath());
    }

    [Test]
    public void FindNvccOutsidePath_WhenCudaPathIsSetInsteadOfCudaHome_StillFindsTheToolkit()
    {
        using var temp = new TempDirectory();
        var cudaBin = CreateCudaRoot(temp.Path, withNvcc: true);
        using var path = new EnvironmentScope("PATH", temp.Path);
        using var cudaHome = new EnvironmentScope("CUDA_HOME", value: null);
        using var cudaPath = new EnvironmentScope("CUDA_PATH", Path.GetDirectoryName(cudaBin));

        AssertEx.Equal(Path.Combine(cudaBin, "nvcc"), CudaToolkitLocator.FindNvccOutsidePath());
    }

    /// <summary>
    ///     A root the operator named explicitly is authoritative: its emptiness is an answer, and must not be overruled
    ///     by whatever sits in <c>/usr/local/cuda</c>. This is also what lets a gate machine with a real CUDA install
    ///     assert "no nvcc anywhere" deterministically.
    /// </summary>
    [Test]
    public void FindNvccOutsidePath_WhenTheConfiguredRootHasNoNvcc_DoesNotFallBackToTheConventionalRoot()
    {
        using var temp = new TempDirectory();
        _ = CreateCudaRoot(temp.Path, withNvcc: false);
        using var path = new EnvironmentScope("PATH", temp.Path);
        using var cudaHome = new EnvironmentScope("CUDA_HOME", Path.Combine(temp.Path, "cuda"));
        using var cudaPath = new EnvironmentScope("CUDA_PATH", value: null);

        AssertEx.Null(CudaToolkitLocator.FindNvccOutsidePath());
    }

    /// <summary>
    ///     A relative root would make the resolved program depend on the process working directory, which is the one
    ///     thing an absolute-path-only launch policy exists to prevent.
    /// </summary>
    [Test]
    public void FindNvccOutsidePath_IgnoresARelativeCudaRoot()
    {
        using var temp = new TempDirectory();
        var cudaBin = CreateCudaRoot(temp.Path, withNvcc: true);
        using var path = new EnvironmentScope("PATH", temp.Path);
        using var cudaHome = new EnvironmentScope("CUDA_HOME", Path.GetRelativePath(Environment.CurrentDirectory, Path.GetDirectoryName(cudaBin)!));
        using var cudaPath = new EnvironmentScope("CUDA_PATH", value: null);

        AssertEx.Null(CudaToolkitLocator.FindNvccOutsidePath(),
            "A relative CUDA root is not a candidate, and a set-but-unusable root never falls through to the conventional one.");
    }

    // Returns the {temp}/cuda/bin directory, with or without an nvcc in it.
    private static string CreateCudaRoot(string temp, bool withNvcc)
    {
        var cudaBin = Path.Combine(temp, "cuda", "bin");
        Directory.CreateDirectory(cudaBin);
        if (withNvcc)
        {
            WriteExecutable(cudaBin, "nvcc");
        }

        return cudaBin;
    }

    private static void WriteExecutable(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, $"#!/bin/sh\necho '{name} 1.0'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _original;

        public EnvironmentScope(string name, string? value)
        {
            _name = name;
            _original = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() =>
            Environment.SetEnvironmentVariable(_name, _original);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xe-cuda-locator-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception)
            {
                /* Best-effort test cleanup. */
            }
        }
    }
}
