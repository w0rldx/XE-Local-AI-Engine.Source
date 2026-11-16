namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Builders for the nodes, edges and template records an architecture descriptor assembles.</summary>
internal static class ModelLabTemplateParts
{
    public static void Node(List<ModelLabArchitectureTemplateNode> nodes, string id, string label, string? component) =>
        nodes.Add(new ModelLabArchitectureTemplateNode
        {
            Id = id,
            Label = label,
            Component = component
        });

    public static ModelLabArchitectureTemplateEdge E(string from, string to) =>
        new()
        {
            From = from,
            To = to
        };

    public static ModelLabArchitectureTemplate Template(string kind, string source, List<ModelLabArchitectureTemplateNode> nodes, List<ModelLabArchitectureTemplateEdge> edges) =>
        new()
        {
            Kind = kind,
            Provenance = "inferred",
            Source = source,
            Nodes = nodes,
            Edges = edges
        };
}
