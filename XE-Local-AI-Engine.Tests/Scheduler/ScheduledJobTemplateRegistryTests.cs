namespace XE_Local_AI_Engine.Tests.Scheduler;

using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Scheduler;
using XE_Local_AI_Engine.Client.Services.Scheduler.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Unit tests for <see cref="ScheduledJobTemplateRegistry" />.
///     Covers: ListTemplates, GetTemplate hit/miss, TryGetHandler true/false, and duplicate-TemplateId ctor throw.
/// </summary>
public sealed class ScheduledJobTemplateRegistryTests
{
    [Test]
    public void ListTemplates_WhenMultipleHandlersRegistered_ReturnsAllDescriptors()
    {
        var h1 = new StubHandler("tpl-a", "Alpha");
        var h2 = new StubHandler("tpl-b", "Beta");
        var registry = new ScheduledJobTemplateRegistry([h1, h2]);

        var descriptors = registry.ListTemplates();

        AssertEx.Equal(expected: 2, descriptors.Count, "Should return one descriptor per handler.");
        AssertEx.True(descriptors.Any(d => d.TemplateId == "tpl-a"), "tpl-a descriptor must be present.");
        AssertEx.True(descriptors.Any(d => d.TemplateId == "tpl-b"), "tpl-b descriptor must be present.");
    }

    [Test]
    public void ListTemplates_WhenNoHandlersRegistered_ReturnsEmptyList()
    {
        var registry = new ScheduledJobTemplateRegistry([]);

        AssertEx.Empty(registry.ListTemplates(), "Empty handler set should yield empty template list.");
    }

    [Test]
    public void ListTemplates_ReturnsDescriptorsMatchingHandlerDescriptors()
    {
        var handler = new TestEchoScheduledJobHandler();
        var registry = new ScheduledJobTemplateRegistry([handler]);

        var descriptors = registry.ListTemplates();

        AssertEx.Equal(expected: 1, descriptors.Count);
        var d = descriptors[0];
        AssertEx.Equal(TestEchoScheduledJobHandler.Id, d.TemplateId);
        AssertEx.True(d.AllowManualTrigger, "test.echo declares AllowManualTrigger=true.");
        AssertEx.False(d.AllowAgentCreation, "test.echo declares AllowAgentCreation=false.");
    }

    [Test]
    public void GetTemplate_WhenTemplateIdKnown_ReturnsDescriptor()
    {
        var registry = new ScheduledJobTemplateRegistry([new StubHandler("tpl-x", "X")]);

        var descriptor = registry.GetTemplate("tpl-x");

        AssertEx.NotNull(descriptor, "Known template id should return a descriptor.");
        AssertEx.Equal("tpl-x", descriptor!.TemplateId);
        AssertEx.Equal("X", descriptor.DisplayName);
    }

    [Test]
    public void GetTemplate_WhenTemplateIdUnknown_ReturnsNull()
    {
        var registry = new ScheduledJobTemplateRegistry([new StubHandler("tpl-x", "X")]);

        var descriptor = registry.GetTemplate("tpl-unknown");

        AssertEx.Null(descriptor, "Unknown template id should return null.");
    }

    [Test]
    public void TryGetHandler_WhenTemplateIdKnown_ReturnsTrueWithNonNullHandler()
    {
        var handler = new StubHandler("tpl-y", "Y");
        var registry = new ScheduledJobTemplateRegistry([handler]);

        var found = registry.TryGetHandler("tpl-y", out var resolved);

        AssertEx.True(found, "TryGetHandler should return true for a registered template.");
        AssertEx.NotNull(resolved, "Out parameter must be non-null when TryGetHandler returns true.");
        AssertEx.Equal("tpl-y", resolved!.TemplateId);
    }

    [Test]
    public void TryGetHandler_WhenTemplateIdUnknown_ReturnsFalse()
    {
        var registry = new ScheduledJobTemplateRegistry([new StubHandler("tpl-y", "Y")]);

        var found = registry.TryGetHandler("tpl-z", out var resolved);

        AssertEx.False(found, "TryGetHandler should return false for an unregistered template.");
        AssertEx.Null(resolved, "Out parameter must be null when TryGetHandler returns false.");
    }

    [Test]
    public void Constructor_WhenDuplicateTemplateId_ThrowsInvalidOperationException()
    {
        var h1 = new StubHandler("dupe", "First");
        var h2 = new StubHandler("dupe", "Second");

        InvalidOperationException? caught = null;
        try
        {
            _ = new ScheduledJobTemplateRegistry([h1, h2]);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        AssertEx.NotNull(caught, "Constructor must throw InvalidOperationException for duplicate TemplateId.");
        AssertEx.Contains(caught!.Message, "dupe", StringComparison.OrdinalIgnoreCase,
            "Exception message should name the duplicate template id.");
    }

    private sealed class StubHandler(string templateId, string displayName) : IScheduledJobHandler
    {
        public string TemplateId { get; } = templateId;

        public ScheduledJobTemplateDescriptor Descriptor { get; } = new(templateId,
            displayName,
            "Stub handler for registry tests.",
            ParameterSchema: null,
            DefaultParameters: null,
            [ScheduleKind.OneShot],
            ScheduleKind.OneShot,
            SchedulerMisfirePolicy.Smart,
            DefaultMaxRuntimeSeconds: null,
            AllowManualTrigger: false);

        public Task ExecuteAsync(ScheduledJobExecutionContext context, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }
}
