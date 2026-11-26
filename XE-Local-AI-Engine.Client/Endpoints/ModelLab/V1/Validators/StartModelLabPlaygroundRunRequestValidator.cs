namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Validators;

using System.Globalization;
using FastEndpoints;
using FluentValidation;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class StartModelLabPlaygroundRunRequestValidator : Validator<StartModelLabPlaygroundRunRequest>
{
    public StartModelLabPlaygroundRunRequestValidator()
    {
        RuleFor(static request => request.Dataset)
            .IsInEnum()
            .WithMessage("Choose one of the listed datasets.");

        RuleFor(static request => request.LearningRate)
            .InclusiveBetween(ModelLabPlaygroundSpec.MinLearningRate, ModelLabPlaygroundSpec.MaxLearningRate)
            .WithMessage(string.Create(CultureInfo.InvariantCulture,
                $"The learning rate must be from {ModelLabPlaygroundSpec.MinLearningRate} to {ModelLabPlaygroundSpec.MaxLearningRate}."));

        RuleFor(static request => request.Depth)
            .InclusiveBetween(ModelLabPlaygroundSpec.MinDepth, ModelLabPlaygroundSpec.MaxDepth)
            .WithMessage($"The depth must be from {ModelLabPlaygroundSpec.MinDepth} to {ModelLabPlaygroundSpec.MaxDepth} block visits.");

        RuleFor(static request => request.Hidden)
            .InclusiveBetween(ModelLabPlaygroundSpec.MinHidden, ModelLabPlaygroundSpec.MaxHidden)
            .WithMessage($"The hidden width must be from {ModelLabPlaygroundSpec.MinHidden} to {ModelLabPlaygroundSpec.MaxHidden}.");

        RuleFor(static request => request.Steps)
            .InclusiveBetween(ModelLabPlaygroundSpec.MinSteps, ModelLabPlaygroundSpec.MaxSteps)
            .WithMessage($"A run trains for {ModelLabPlaygroundSpec.MinSteps} to {ModelLabPlaygroundSpec.MaxSteps} steps.");

        RuleFor(static request => request.Seed)
            .InclusiveBetween(ModelLabPlaygroundSpec.MinSeed, ModelLabPlaygroundSpec.MaxSeed)
            .WithMessage($"The seed must be from {ModelLabPlaygroundSpec.MinSeed} to {ModelLabPlaygroundSpec.MaxSeed}.");
    }
}
