namespace XE_Local_AI_Engine.Tests.Endpoints.Images;

using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>A failed job's reason reaches the wire as its enum name beside the English text; a job without one sends null.</summary>
[Category(TestCategories.Unit)]
public sealed class ImageJobResponseMappingTests
{
    [Test]
    [Arguments(ImageJobFailureReason.ProcessExited, "ProcessExited")]
    [Arguments(null, null)]
    public void ToResponse_SendsTheFailureReasonName(ImageJobFailureReason? reason, string? expected)
    {
        var response = new ImageJobView
        {
            Id = Guid.NewGuid(),
            ModelName = "sd15",
            Prompt = "a fox",
            Seed = 1,
            Width = 512,
            Height = 512,
            Steps = 20,
            Sampler = "euler_a",
            CfgScale = 7,
            Status = ImageJobStatus.Failed,
            CreatedAtUtc = 1,
            SanitizedError = "The image server stopped unexpectedly.",
            FailureReason = reason
        }.ToResponse();

        AssertEx.Equal<string?>(expected, response.FailureReason);
        AssertEx.Equal("The image server stopped unexpectedly.", response.SanitizedError);
    }
}
