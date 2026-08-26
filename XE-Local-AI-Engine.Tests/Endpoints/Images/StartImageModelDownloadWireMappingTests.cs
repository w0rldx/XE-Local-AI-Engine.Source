namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using XE_Local_AI_Engine.Client.Endpoints.Images.V1;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.Abstractions.Image;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class StartImageModelDownloadWireMappingTests
{
    [Test]
    public void Validate_NormalizesWireValuesBeforeMappingToTheServiceRequest()
    {
        var result = StartImageModelDownloadWireValidator.Validate(new StartImageModelDownloadRequest
        {
            ModelName = "  model  ",
            RepoId = "  owner/repo  ",
            Family = "sD15",
            Kind = "   ",
            Revision = "  main  ",
            Parts =
            [
                new ImageModelPartDownloadRequest
                {
                    Role = "dIfFuSiOn",
                    FileName = "  weights.gguf  ",
                    Sha256 = "  digest  ",
                    RepoId = "  override/repo  ",
                    SizeBytes = 0
                }
            ]
        });

        AssertEx.True(result.IsValid);
        var request = StartImageModelDownloadRequestMapper.ToServiceRequest(result.Values!);
        AssertEx.Equal("model", request.ModelName);
        AssertEx.Equal("owner/repo", request.RepoId);
        AssertEx.Equal(ImageModelFamily.Sd15, request.Family);
        AssertEx.Equal(ImageModelKind.Txt2Img, request.Kind);
        AssertEx.Equal("main", request.Revision);
        AssertEx.Equal(ImageModelPartRole.Diffusion, request.Parts[0].Role);
        AssertEx.Equal("weights.gguf", request.Parts[0].FileName);
        AssertEx.Equal("digest", request.Parts[0].Sha256);
        AssertEx.Equal("override/repo", request.Parts[0].RepoId);
        AssertEx.Null(request.Parts[0].SizeBytes);
    }

    [Test]
    public void Validate_LeavesSemanticFileSetRulesBelowTheWireBoundary()
    {
        var result = StartImageModelDownloadWireValidator.Validate(new StartImageModelDownloadRequest
        {
            ModelName = "model",
            RepoId = "owner/repo",
            Family = "Sd15",
            Parts =
            [
                new ImageModelPartDownloadRequest
                {
                    Role = "Vae",
                    FileName = "vae.safetensors"
                }
            ]
        });

        AssertEx.True(result.IsValid, "A syntactically valid role belongs past the V1 wire validator.");
        var request = StartImageModelDownloadRequestMapper.ToServiceRequest(result.Values!);
        AssertEx.Equal("The file-set must include a diffusion part.", ImageModelFileSetRules.Validate(request.Parts));
    }

    [Test]
    public void Validate_ReturnsTheExistingWireErrorWithoutProducingValues()
    {
        var result = StartImageModelDownloadWireValidator.Validate(new StartImageModelDownloadRequest
        {
            ModelName = "model",
            RepoId = "owner/repo",
            Family = "Sd15",
            Parts =
            [
                new ImageModelPartDownloadRequest
                {
                    Role = "other",
                    FileName = "weights.gguf"
                }
            ]
        });

        AssertEx.False(result.IsValid);
        AssertEx.Null(result.Values);
        AssertEx.Equal("The part role 'other' is not recognized.", result.Error);
    }
}
