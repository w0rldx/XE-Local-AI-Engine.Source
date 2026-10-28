namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InvocationState.Clone" /> is the one snapshot routine the worker event dispatcher and the resume
///     registry both call, so every member must be copied there: a member missing from the clone travels as null and
///     is persisted as null for a turn that did dispatch.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class InvocationStateTests
{
    [Test]
    public void Clone_CopiesDispatchedTierAndAuthoredEffort()
    {
        var state = new InvocationState
        {
            InvocationId = Guid.NewGuid(),
            DispatchedTier = "fast",
            AuthoredEffort = "auto"
        };

        var clone = state.Clone();

        AssertEx.Equal("fast", clone.DispatchedTier);
        AssertEx.Equal("auto", clone.AuthoredEffort);
    }

    [Test]
    public void Clone_WhenNotDispatched_KeepsBothNull()
    {
        var clone = new InvocationState
        {
            InvocationId = Guid.NewGuid()
        }.Clone();

        AssertEx.Null(clone.DispatchedTier);
        AssertEx.Null(clone.AuthoredEffort);
    }
}
