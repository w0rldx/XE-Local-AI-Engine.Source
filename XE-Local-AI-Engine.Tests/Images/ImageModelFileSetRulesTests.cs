namespace XE_Local_AI_Engine.Tests.Images;

using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The file-set rules mirror what the launch-argument builder can emit: exactly one file per role, and a diffusion
///     part is mandatory (without it there is nothing to pass to <c>--diffusion-model</c>).
/// </summary>
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

    private static ImageModelPartRequest Part(ImageModelPartRole role, string fileName = "weights.gguf")
    {
        return new ImageModelPartRequest
        {
            Role = role,
            FileName = fileName
        };
    }
}
