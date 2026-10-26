namespace XE_Local_AI_Engine.Tests.WorkSessions;

using System.Text.Json;
using XE_Local_AI_Engine.AI.Agent.Tools;
using XE_Local_AI_Engine.AI.Agent.Tools.Implementation;
using XE_Local_AI_Engine.Client.Services.WorkSessions;
using XE_Local_AI_Engine.Client.Services.WorkSessions.Tools;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The four state-tool schemas as the model receives them: parseable by the registry, strict about unknown
///     properties, matching the descriptors the offer merges, and inside llama.cpp's grammar repetition ceiling.
/// </summary>
[Category(TestCategories.Unit)]
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

    /// <summary>
    ///     The honesty argument's shape: optional, so a completion recorded before it existed still reads as met, and a
    ///     plain boolean, which is the smallest thing that can be added to an array that compiles into one GBNF grammar.
    /// </summary>
    [Test]
    public void CompleteWorkSession_OffersObjectiveMetAsAnOptionalBoolean()
    {
        using var document = JsonDocument.Parse(WorkSessionToolDefinitions.CompleteWorkSession.ParameterSchema);

        AssertEx.Equal("boolean", document.RootElement.GetProperty("properties").GetProperty("objectiveMet").GetProperty("type").GetString());
        AssertEx.False(document.RootElement.GetProperty("required")
                               .EnumerateArray()
                               .Any(static member => string.Equals(member.GetString(), "objectiveMet", StringComparison.Ordinal)),
            "Absent means met, so requiring it would break every completion the model already knows how to write.");
    }

    /// <summary>
    ///     The output argument's shape is ONE flat scalar map whatever a node declares, so the grammar the offer compiles never
    ///     changes per node; optional, because only a session with an output contract owes one.
    /// </summary>
    [Test]
    public void CompleteWorkSession_OffersOutputAsAnOptionalFlatScalarMap()
    {
        using var document = JsonDocument.Parse(WorkSessionToolDefinitions.CompleteWorkSession.ParameterSchema);

        var output = document.RootElement.GetProperty("properties").GetProperty("output");
        AssertEx.Equal("object", output.GetProperty("type").GetString());
        AssertEx.Equal("string, number, boolean",
            string.Join(", ", output.GetProperty("additionalProperties").GetProperty("type").EnumerateArray().Select(static type => type.GetString())));
        AssertEx.False(output.TryGetProperty("properties", out _), "a declared member list would make the grammar per node.");
        AssertEx.False(document.RootElement.GetProperty("required")
                               .EnumerateArray()
                               .Any(static member => string.Equals(member.GetString(), "output", StringComparison.Ordinal)),
            "Sessions without an output contract never send one.");
    }

    /// <summary>A plain string member still has a bound, the tool's own text cap, and the rendered contract says so.</summary>
    [Test]
    public void OutputContract_RendersAPlainStringMemberWithTheToolsTextBound()
    {
        var contract = new WorkSessionOutputSchema
        {
            Properties =
            [
                new WorkSessionOutputProperty
                {
                    Name = "note",
                    Type = WorkSessionOutputType.String,
                    Required = false,
                    Enum = null,
                    MaxLength = null
                }
            ]
        };

        AssertEx.Contains(contract.Render(), $"- note: string, at most {WorkSessionToolDefinitions.TextMaxLength} characters");
    }

    /// <summary>An integer member is judged by value, not by spelling: JSON Schema counts 1.0 and 1e0 as integers too.</summary>
    [Test]
    [Arguments("3", true)]
    [Arguments("3.0", true)]
    [Arguments("1e0", true)]
    [Arguments("-2E2", true)]
    [Arguments("3.5", false)]
    [Arguments("1e-1", false)]
    [Arguments("\"3\"", false)]
    public void OutputContract_AcceptsAnIntegerMemberByValueWhateverItsSpelling(string count, bool accepted)
    {
        var contract = new WorkSessionOutputSchema
        {
            Properties =
            [
                new WorkSessionOutputProperty
                {
                    Name = "count",
                    Type = WorkSessionOutputType.Integer,
                    Required = true,
                    Enum = null,
                    MaxLength = null
                }
            ]
        };
        using var output = JsonDocument.Parse($$"""{"count":{{count}}}""");

        var violation = contract.Violation(output.RootElement);

        if (accepted)
        {
            AssertEx.Null(violation);
        }
        else
        {
            AssertEx.Contains(AssertEx.NotNull(violation), "'output.count' must be a whole number");
        }
    }

    /// <summary>The read tool's shape: a required name, an optional non-negative character offset, an optional producer, nothing else.</summary>
    [Test]
    public void ReadUpstreamArtifact_TakesANameAndAnOptionalOffsetAndProducer()
    {
        using var document = JsonDocument.Parse(WorkSessionToolDefinitions.ReadUpstreamArtifact.ParameterSchema);

        var properties = document.RootElement.GetProperty("properties");
        AssertEx.Equal("name, offset, producer", string.Join(", ", properties.EnumerateObject().Select(static property => property.Name)));
        AssertEx.Equal("integer", properties.GetProperty("offset").GetProperty("type").GetString());
        AssertEx.Equal(expected: 0, properties.GetProperty("offset").GetProperty("minimum").GetInt32());
        AssertEx.Equal("name", string.Join(", ", document.RootElement.GetProperty("required").EnumerateArray().Select(static member => member.GetString())));
        AssertEx.True(WorkSessionToolDefinitions.ReadUpstreamArtifact.PageCharacters <= WorkSessionToolDefinitions.TextMaxLength,
            "a page is never longer than the text bound every other work-session tool holds itself to.");
    }

    /// <summary>The read tool writes nothing, so it is the one descriptor that is not WriteExecute; the four state tools all are.</summary>
    [Test]
    public void EveryDescriptor_MatchesTheHandlerNameAndCarriesNoApprovalGate()
    {
        AssertEx.Equal(WorkSessionToolDefinitions.ToolNames.Count + 1, WorkSessionToolCatalog.Descriptors.Count);
        var read = AssertEx.NotNull(WorkSessionToolCatalog.Descriptors.SingleOrDefault(static descriptor => descriptor.Name == WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName));
        AssertEx.Equal(ToolCategory.ReadLocal, read.Category);
        AssertEx.False(read.RequiresApproval);
        foreach (var descriptor in WorkSessionToolCatalog.Descriptors.Where(static descriptor => descriptor.Name != WorkSessionToolDefinitions.ReadUpstreamArtifact.ToolName))
        {
            AssertEx.Contains(WorkSessionToolDefinitions.ToolNames, descriptor.Name);
            AssertEx.False(descriptor.RequiresApproval, "A prompt per recorded finding would make an unattended session unusable.");
            AssertEx.Equal(ToolCategory.WriteExecute,
                descriptor.Category,
                "These tools write durable rows, and hiding that from a category-based policy would be worse than the click.");
        }
    }
}
