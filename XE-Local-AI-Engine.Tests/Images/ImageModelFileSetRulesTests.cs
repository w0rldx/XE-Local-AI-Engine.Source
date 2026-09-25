namespace XE_Local_AI_Engine.Tests.Images;

using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The file-set rules mirror what the launch-argument builder can emit: exactly one file per role, and a diffusion
///     part is mandatory (without it there is nothing to pass to <c>--diffusion-model</c>).
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ImageModelFileSetRulesTests
{
    [Test]
    public void OneFilePerRole_WithADiffusionPart_IsAccepted()
    {
        var error = ImageModelFileSetRules.Validate([Part(ImageModelPartRole.Diffusion), Part(ImageModelPartRole.Vae)]);

        AssertEx.Null(error);
    }

    [Test]
    public void MissingDiffusionPart_IsRejected()
    {
        var error = ImageModelFileSetRules.Validate([Part(ImageModelPartRole.Vae)]);

        AssertEx.Equal("The file-set must include a diffusion part.", error);
    }

    [Test]
    public void DuplicateRole_IsRejected_NamingTheRole()
    {
        var error = ImageModelFileSetRules.Validate([
            Part(ImageModelPartRole.Diffusion),
            Part(ImageModelPartRole.Vae, "a.safetensors"),
            Part(ImageModelPartRole.Vae, "b.safetensors")
        ]);

        AssertEx.Equal($"The file-set declares the '{ImageModelPartRole.Vae}' part more than once.", error);
    }

    [Test]
    public void EmptySet_IsRejectedAsMissingDiffusion()
    {
        var error = ImageModelFileSetRules.Validate([]);

        AssertEx.Equal("The file-set must include a diffusion part.", error);
    }

    [Test]
    [Arguments("transformer/diffusion_pytorch_model.safetensors")]
    [Arguments("diffusion_pytorch_model.fp16.safetensors")]
    [Arguments("transformer/diffusion_pytorch_model-00001-of-00009.safetensors")]
    [Arguments("qwen_image-00001-of-00003.gguf")]
    public void DiffusersLayoutOrShardDiffusionPart_IsRejected(string fileName)
    {
        // The tester's qwen-image-2.1 import: a Diffusers component downloaded fine, then sd-server exited with
        // "get sd version from file failed". Refused before the download instead.
        var error = ImageModelFileSetRules.Validate([Part(ImageModelPartRole.Diffusion, fileName)]);

        AssertEx.Equal(ImageWeightLayout.DiffusersLayoutUnsupportedMessage, error);
    }

    [Test]
    [Arguments("qwen_image_2.1-Q4_K.gguf")]
    [Arguments("flux1-schnell-fp8.safetensors")]
    [Arguments("model.safetensors")]
    public void SingleFileCheckpointDiffusionPart_IsAccepted(string fileName)
    {
        // A bare model.safetensors is only Diffusers-shaped next to a config.json, which the request cannot show; the
        // inspect listing flags that case instead.
        AssertEx.Null(ImageModelFileSetRules.Validate([Part(ImageModelPartRole.Diffusion, fileName)]));
    }

    [Test]
    public void DiffusersNamedFileInANonDiffusionRole_IsNotRejectedByTheGuard()
    {
        var error = ImageModelFileSetRules.Validate([
            Part(ImageModelPartRole.Diffusion),
            Part(ImageModelPartRole.Vae, "vae/diffusion_pytorch_model.safetensors")
        ]);

        AssertEx.Null(error);
    }

    private static ImageModelPartRequest Part(ImageModelPartRole role, string fileName = "weights.gguf")
    {
        return new ImageModelPartRequest
        {
            Role = role,
            FileName = fileName
        };
    }
}
