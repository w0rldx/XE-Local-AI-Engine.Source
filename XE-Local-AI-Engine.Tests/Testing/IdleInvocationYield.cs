namespace XE_Local_AI_Engine.Tests.Testing;

using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using XE_Local_AI_Engine.Client.Services.Events;
using XE_Local_AI_Engine.Client.Services.Events.Implementation;
using XE_Local_AI_Engine.Client.Services.Invocation;

/// <summary>A <see cref="BackgroundInvocationYield" /> over a real, never-taken invocation slot and a zero idle window.</summary>
/// <remarks>For worker tests that are not about the yield: the work runs at once, exactly as it did before the yield existed.</remarks>
internal static class IdleInvocationYield
{
    public static BackgroundInvocationYield Create() =>
        new(new WorkerEventDispatcher(Substitute.For<IInvocationRunner>(), Substitute.For<IInvocationHistory>(), NullLogger<WorkerEventDispatcher>.Instance, TimeProvider.System),
            TimeProvider.System,
            NullLogger<BackgroundInvocationYield>.Instance,
            TimeSpan.Zero);
}
