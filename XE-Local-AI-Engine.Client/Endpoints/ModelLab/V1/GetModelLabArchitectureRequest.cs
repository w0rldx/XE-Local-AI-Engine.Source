namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

/// <summary>Request DTO for an installed GGUF's architecture descriptor. <see cref="ModelName" /> is bound from the route.</summary>
public sealed class GetModelLabArchitectureRequest
{
    public string? ModelName { get; init; }
}
