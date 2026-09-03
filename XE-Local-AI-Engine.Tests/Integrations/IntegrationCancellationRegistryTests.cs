namespace XE_Local_AI_Engine.Tests.Integrations;

using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The process-local cancel handles. It exists because the invocation runner only cancels the run it is CURRENTLY
///     driving, so an execution still waiting on the node's single lease would ignore that call entirely.
/// </summary>
public sealed class IntegrationCancellationRegistryTests
{
    [Test]
    public void Signal_CancelsTheRegisteredToken()
    {
        var registry = new IntegrationCancellationRegistry();
        var executionId = Guid.NewGuid();
        AssertEx.True(registry.TryRegister(executionId, out var token));
        AssertEx.False(token.IsCancellationRequested);

        AssertEx.True(registry.Signal(executionId));

        AssertEx.True(token.IsCancellationRequested);
    }

    [Test]
    public void TryRegister_RefusesASecondRegistrationForTheSameExecution()
    {
        var registry = new IntegrationCancellationRegistry();
        var executionId = Guid.NewGuid();
        AssertEx.True(registry.TryRegister(executionId, out _));

        AssertEx.False(registry.TryRegister(executionId, out _), "Two handles for one execution would leave one of them unreachable.");
    }

    [Test]
    public void Signal_ForAnUnknownOrRemovedExecution_ReturnsFalseRatherThanThrowing()
    {
        // Not an error: the durable stop marker is what a restart reads, and this registry is only the in-process
        // shortcut for a run that is still on this machine.
        var registry = new IntegrationCancellationRegistry();
        var executionId = Guid.NewGuid();

        AssertEx.False(registry.Signal(executionId));

        AssertEx.True(registry.TryRegister(executionId, out _));
        registry.Remove(executionId);
        AssertEx.False(registry.Signal(executionId));
    }

    [Test]
    public void Remove_DisposesTheHandleAndFreesTheIdForReuse()
    {
        var registry = new IntegrationCancellationRegistry();
        var executionId = Guid.NewGuid();
        AssertEx.True(registry.TryRegister(executionId, out _));

        registry.Remove(executionId);

        AssertEx.True(registry.TryRegister(executionId, out var token));
        AssertEx.False(token.IsCancellationRequested, "The reused id must get a fresh, uncancelled token.");
        registry.Remove(executionId);
        registry.Remove(executionId);
    }
}
