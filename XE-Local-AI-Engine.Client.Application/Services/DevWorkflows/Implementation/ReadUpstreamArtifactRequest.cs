namespace XE_Local_AI_Engine.Client.Services.DevWorkflows.Implementation;

/// <summary>The arguments of <c>read_upstream_artifact</c>, as the model sends them.</summary>
internal sealed class ReadUpstreamArtifactRequest
{
    public string? Name { get; init; }

    public int? Offset { get; init; }

    public string? Producer { get; init; }
}
