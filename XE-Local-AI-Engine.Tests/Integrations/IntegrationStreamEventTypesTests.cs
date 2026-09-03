namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class IntegrationStreamEventTypesTests
{
    [Test]
    public void Persisted_IsExactlyTheNineNonAssistantTypes()
    {
        // The one constant the coordinator, the stream writer and the output tool all branch on. A wrong entry copies
        // transcript content into integration_execution_events, which is the leak that table was designed to avoid —
        // and an "all except" formulation would silently persist any type a later slice adds.
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
