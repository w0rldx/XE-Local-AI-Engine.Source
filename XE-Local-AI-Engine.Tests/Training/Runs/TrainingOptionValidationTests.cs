namespace XE_Local_AI_Engine.Tests.Training.Runs;

using XE_Local_AI_Engine.Client.Services.Training.Runs;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>An out-of-range option is refused by name and bound, so the operator knows which field to change.</summary>
[Category(TestCategories.Unit)]
public sealed class TrainingOptionValidationTests
{
    [Test]
    public void Validate_WhenEpochsExceedTheCap_NamesTheOptionAndItsBounds()
    {
        var rejection = AssertEx.Throws<TrainingRunRejectedException>(() => TrainingOptionDefaultsCalculator.Validate(new TrainingRunOptionsV1
        {
            Epochs = 60
        }));

        AssertEx.Equal("The training option epochs is 60; it must be between 1 and 50.", rejection.Message);
    }

    [Test]
    public void Validate_WhenTheLearningRateIsZero_StatesTheExclusiveLowerBound()
    {
        var rejection = AssertEx.Throws<TrainingRunRejectedException>(() => TrainingOptionDefaultsCalculator.Validate(new TrainingRunOptionsV1
        {
            LearningRate = 0
        }));

        AssertEx.Equal("The training option learningRate is 0; it must be greater than 0 and at most 1.", rejection.Message);
    }

    [Test]
    public void Validate_WhenTheDefaultsAreUsed_Accepts() =>
        AssertEx.DoesNotThrow(() => TrainingOptionDefaultsCalculator.Validate(new TrainingRunOptionsV1()), "The record defaults must be in range.");
}
