namespace XE_Local_AI_Engine.Tests.Invocation;

using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="InvocationState.Clone" /> is the single snapshot routine both the worker event dispatcher and the
///     resume registry call, and its own documentation requires every member to be copied there — a member added to
///     the class but not to the clone travels as null on whichever path snapshots first, which is how the persisted
///     value silently becomes null for a turn that did dispatch.
/// </summary>
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
        var clone = new InvocationState { InvocationId = Guid.NewGuid() }.Clone();

        AssertEx.Null(clone.DispatchedTier);
        AssertEx.Null(clone.AuthoredEffort);
    }
}
