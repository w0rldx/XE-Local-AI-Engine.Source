namespace XE_Local_AI_Engine.Tests.Hubs;

using System.Buffers;
using System.Text;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Knowledge;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Pins the wire text hubs actually emit, taken from the host's own configured <see cref="IHubProtocol" /> rather
///     than from options rebuilt here.
/// </summary>
/// <remarks>
///     Without <c>AddJsonProtocol</c> in the composition root, SignalR serializes with its own defaults: a CLR enum
///     member leaves as a NUMBER while the OpenAPI-generated type for the same field is a string union, so a browser
///     handler comparing against the name matches nothing and fails silently — no error, no log, no red test.
/// </remarks>
[Category(TestCategories.Integration)]
public sealed class HubJsonProtocolTests
{
    [ClassDataSource<TestServerWebAppFactory>(Shared = SharedType.PerClass)]
    public required TestServerWebAppFactory Factory { get; init; }

    [Test]
    public void KnowledgeDocumentChanged_SerializesTheStatusEnumAsItsName_AndPropertiesInCamelCase()
    {
        var payload = new KnowledgeDocumentChangedHubEvent
        {
            EventType = KnowledgeBaseHubEvents.DocumentChanged,
            DocumentId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Status = KnowledgeDocumentStatus.Indexed,
            OccurredAtUtc = 1758000000000
        };

        var frame = WriteFrame(KnowledgeBaseHubEvents.DocumentChanged, payload);

        AssertEx.Contains(frame, "\"status\":\"Indexed\"");
        AssertEx.Contains(frame, "\"documentId\":\"11111111-2222-3333-4444-555555555555\"");
        AssertEx.Contains(frame, "\"occurredAtUtc\":1758000000000");
    }

    [Test]
    public void SchedulerRun_SerializesBothOfItsEnumsAsNames()
    {
        var payload = new SchedulerRunHubEvent
        {
            EventType = SchedulerHubEvents.RunCompleted,
            RunId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            ScheduledJobId = Guid.Parse("66666666-7777-8888-9999-000000000000"),
            TemplateId = "model-fit-refresh",
            Status = ScheduledRunStatus.Succeeded,
            TriggeredBy = ScheduledRunTrigger.Schedule,
            ScheduledFireTimeUtc = null,
            ActualFireTimeUtc = null,
            CompletedAtUtc = null,
            DurationMs = null,
            Summary = null,
            ErrorMessage = null,
            OccurredAtUtc = 1758000000000
        };

        var frame = WriteFrame(SchedulerHubEvents.RunCompleted, payload);

        AssertEx.Contains(frame, "\"status\":\"Succeeded\"");
        AssertEx.Contains(frame, "\"triggeredBy\":\"Schedule\"");
    }

    // The host's registered JSON hub protocol — the same instance a live connection writes through, so what it
    // produces here is what a browser receives.
    private string WriteFrame(string method, object payload)
    {
        var protocols = Factory.Services.GetServices<IHubProtocol>().ToArray();
        AssertEx.ContainsSingle(protocols, candidate => string.Equals(candidate.Name, "json", StringComparison.Ordinal));
        var protocol = protocols.Single(candidate => string.Equals(candidate.Name, "json", StringComparison.Ordinal));
        var buffer = new ArrayBufferWriter<byte>();
        protocol.WriteMessage(new InvocationMessage(method, [payload]), buffer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
