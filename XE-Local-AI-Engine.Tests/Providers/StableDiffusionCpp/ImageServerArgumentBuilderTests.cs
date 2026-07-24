namespace XE_Local_AI_Engine.Tests.Providers.StableDiffusionCpp;

using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Options;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Verifies the sd-server startup argument vector maps a resolved model file-set to the correct flags: a single-file
///     SD1.5 model uses <c>-m</c>; a multi-part FLUX/SD3 set uses <c>--diffusion-model</c> + <c>--vae</c> +
///     <c>--clip_l</c> + <c>--t5xxl</c>. The bind address, backend, and threads are always present. Verified against
///     stable-diffusion.cpp @ <c>master-742-1a13107</c>, including its live <c>--help</c> mapping from
///     <c>--clip-on-cpu</c> to <c>te=cpu</c>.
/// </summary>
public sealed class ImageServerArgumentBuilderTests
{
    private static readonly StableDiffusionRuntimeOptions Options = new();

    [Test]
    public void Build_Sd15SingleFile_UsesSingleFileModelFlag_AndBindsLoopback()
    {
        IReadOnlyList<ImageModelPart> parts =
        [
            Part(ImageModelPartRole.Diffusion, "sd15.gguf", "/models/sd15/sd15.gguf")
        ];

        var spec = ImageServerArgumentBuilder.Build("sd15", "/bin/sd-server", parts, SdGpuBackend.Cpu, 18200, Options, threads: 8);

        AssertEx.Contains(spec.Arguments, "-m");
        AssertEx.Equal("/models/sd15/sd15.gguf", spec.Arguments[IndexOf(spec.Arguments, "-m") + 1]);
        AssertEx.False(spec.Arguments.Contains("--diffusion-model"), "A single-file SD1.5 model must not use the file-set diffusion flag.");
        AssertEx.False(spec.Arguments.Contains("--vae"), "A single-file SD1.5 model has no separate VAE part.");

        AssertEx.Contains(spec.Arguments, "--listen-ip");
        AssertEx.Equal("127.0.0.1", spec.Arguments[IndexOf(spec.Arguments, "--listen-ip") + 1]);
        AssertEx.Contains(spec.Arguments, "--listen-port");
        AssertEx.Equal("18200", spec.Arguments[IndexOf(spec.Arguments, "--listen-port") + 1]);
        AssertEx.Contains(spec.Arguments, "--backend");
        AssertEx.Contains(spec.Arguments, "-t");
        AssertEx.Equal("http://127.0.0.1:18200/", spec.BaseAddress.AbsoluteUri);
    }

    [Test]
    public void Build_FluxFileSet_UsesEveryComponentFlag()
    {
        IReadOnlyList<ImageModelPart> parts =
        [
            Part(ImageModelPartRole.Diffusion, "flux1-schnell.gguf", "/models/flux/flux1-schnell.gguf"),
            Part(ImageModelPartRole.Vae, "ae.safetensors", "/models/flux/ae.safetensors"),
            Part(ImageModelPartRole.ClipL, "clip_l.safetensors", "/models/flux/clip_l.safetensors"),
            Part(ImageModelPartRole.T5, "t5xxl.safetensors", "/models/flux/t5xxl.safetensors")
        ];

        var spec = ImageServerArgumentBuilder.Build("flux-schnell", "/bin/sd-server", parts, SdGpuBackend.Cuda, 18201, Options, threads: 8);

        AssertEx.False(spec.Arguments.Contains("-m"), "A multi-part FLUX set must not use the single-file model flag.");
        AssertEx.Contains(spec.Arguments, "--diffusion-model");
        AssertEx.Equal("/models/flux/flux1-schnell.gguf", spec.Arguments[IndexOf(spec.Arguments, "--diffusion-model") + 1]);
        AssertEx.Contains(spec.Arguments, "--vae");
        AssertEx.Equal("/models/flux/ae.safetensors", spec.Arguments[IndexOf(spec.Arguments, "--vae") + 1]);
        AssertEx.Contains(spec.Arguments, "--clip_l");
        AssertEx.Equal("/models/flux/clip_l.safetensors", spec.Arguments[IndexOf(spec.Arguments, "--clip_l") + 1]);
        AssertEx.Contains(spec.Arguments, "--t5xxl");
        AssertEx.Equal("/models/flux/t5xxl.safetensors", spec.Arguments[IndexOf(spec.Arguments, "--t5xxl") + 1]);
    }

    [Test]
    [Arguments(SdGpuBackend.Cpu, "cpu")]
    [Arguments(SdGpuBackend.Cuda, "diffusion=cuda0,te=cpu,vae=cpu")]
    [Arguments(SdGpuBackend.Vulkan, "diffusion=vulkan0,te=cpu,vae=cpu")]
    public void BuildBackendSpec_MapsBackendToDeviceString(SdGpuBackend backend, string expected)
    {
        AssertEx.Equal(expected, ImageServerArgumentBuilder.BuildBackendSpec(backend));
    }

    private static ImageModelPart Part(ImageModelPartRole role, string fileName, string localPath)
    {
        return new ImageModelPart
        {
            Role = role,
            FileName = fileName,
            LocalPath = localPath,
            SizeBytes = 1024
        };
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (string.Equals(args[i], flag, StringComparison.Ordinal))
            {
                return i;
            }
        }

        throw new AssertionException($"Expected flag '{flag}' in argument vector.");
    }
}
