namespace XE_Local_AI_Engine.Client.Services.Training.Datasets;

public interface ISampleValidationPipeline
{
    Task<SampleValidationOutcome> ValidateAsync(string rawCompletion, SampleValidationContext context, CancellationToken cancellationToken = default);
}
