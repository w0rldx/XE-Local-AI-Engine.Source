namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

[Category(TestCategories.Unit)]
public sealed class IntegrationStreamEventTypesTests
{
    [Test]
    public void Persisted_IsExactlyTheNineNonAssistantTypes()
    {
        // The constant the coordinator, stream writer and output tool branch on: a wrong entry leaks transcript content
        // into integration_execution_events; an "all except" form would silently persist any type added later.
        string[] expected =
        [
            IntegrationStreamEventTypes.ExecutionAccepted,
            IntegrationStreamEventTypes.ExecutionQueued,
            IntegrationStreamEventTypes.ExecutionStarted,
            IntegrationStreamEventTypes.ToolStarted,
            IntegrationStreamEventTypes.ToolCompleted,
            IntegrationStreamEventTypes.ExternalOutput,
            IntegrationStreamEventTypes.ExecutionCompleted,
            IntegrationStreamEventTypes.ExecutionFailed,
            IntegrationStreamEventTypes.ExecutionCancelled
        ];

        AssertEx.Equal(expected: 9, IntegrationStreamEventTypes.Persisted.Count);
        AssertEx.True(IntegrationStreamEventTypes.Persisted.SetEquals(expected));

        AssertEx.False(IntegrationStreamEventTypes.Persisted.Contains(IntegrationStreamEventTypes.AssistantDelta),
            "Per-token deltas are streamed only.");
        AssertEx.False(IntegrationStreamEventTypes.Persisted.Contains(IntegrationStreamEventTypes.AssistantCompleted),
            "The final assistant text already lands in the owned conversation as an assistant message.");
    }
}
