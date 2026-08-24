namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The four state-tool schemas as the model receives them: parseable by the registry, strict about unknown
///     properties, matching the descriptors the offer merges, and inside llama.cpp's grammar repetition ceiling.
/// </summary>
public sealed class WorkSessionToolSchemaTests
{
    [Test]
    public void EverySchema_ParsesThroughTheRegistrySeam()
    {
        foreach (var descriptor in WorkSessionToolCatalog.Descriptors)
        {
            var schema = MetadataToolFunction.ParseSchema(descriptor.ParameterSchema!);
            AssertEx.Equal("object", schema.GetProperty("type").GetString(), $"{descriptor.Name} must describe an object.");
        }
    }

    [Test]
    public void EverySchema_RejectsUnknownProperties()
    {
        // The argument-repair wrapper rejects unknown properties strictly for the app's own tools, and that is only
        // meaningful when each schema actually enumerates its inputs.
        foreach (var descriptor in WorkSessionToolCatalog.Descriptors)
        {
            using var document = JsonDocument.Parse(descriptor.ParameterSchema!);
            AssertEx.True(document.RootElement.TryGetProperty("additionalProperties", out var additional) && !additional.GetBoolean(),
                $"{descriptor.Name} must set additionalProperties:false.");
        }
    }

    [Test]
    public void EverySchema_StaysInsideTheLlamaGrammarRepetitionBound()
    {
        // A Research session offers these four on top of ask_user, the clock and three knowledge tools, and llama-server
        // compiles the WHOLE array into one GBNF grammar with a shared ceiling. A bound over the limit here would kill
        // the turn before inference with a sampler error that reads nothing like a schema problem.
        foreach (var descriptor in WorkSessionToolCatalog.Descriptors)
        {
            using var document = JsonDocument.Parse(descriptor.ParameterSchema!);
            AssertEx.False(LlamaGrammarToolSchemaCompatibility.RequiresSanitizing(document.RootElement),
                $"{descriptor.Name} carries a repetition bound llama.cpp would have to strip.");
        }
    }

    [Test]
    public void EveryDescriptor_MatchesTheHandlerNameAndCarriesNoApprovalGate()
    {
        AssertEx.Equal(WorkSessionToolDefinitions.ToolNames.Count, WorkSessionToolCatalog.Descriptors.Count);
        foreach (var descriptor in WorkSessionToolCatalog.Descriptors)
        {
            AssertEx.Contains(WorkSessionToolDefinitions.ToolNames, descriptor.Name);
            AssertEx.False(descriptor.RequiresApproval, "A prompt per recorded finding would make an unattended session unusable.");
            AssertEx.Equal(XE_Local_AI_Engine.AI.Agent.Tools.ToolCategory.WriteExecute,
                descriptor.Category,
                "These tools write durable rows, and hiding that from a category-based policy would be worse than the click.");
        }
    }
}
