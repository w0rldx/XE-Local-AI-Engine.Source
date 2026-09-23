namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Validators;

using FastEndpoints;
using FluentValidation;

/// <summary>Shape only. The byte cap, the node's kind and the run's state are the run service's answers.</summary>
public sealed class SteerGraphWorkflowNodeRunRequestValidator : Validator<SteerGraphWorkflowNodeRunRequest>
{
    public SteerGraphWorkflowNodeRunRequestValidator()
    {
        RuleFor(static request => request.RunId).NotEmpty();
        RuleFor(static request => request.NodeKey).NotEmpty().WithMessage("A steer names the node it steers by node key.");

        // The empty Guid is not an idempotency key: every caller that forgot to mint one would send the same one.
        RuleFor(static request => request.OperationId).NotEmpty().WithMessage("A steer needs a caller-minted operation id.");
        RuleFor(static request => request.Message).NotEmpty().WithMessage("A steer needs a message.");
    }
}
