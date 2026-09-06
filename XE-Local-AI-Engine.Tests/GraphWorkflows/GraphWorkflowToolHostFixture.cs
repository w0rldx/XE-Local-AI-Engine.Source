namespace XE_Local_AI_Engine.Tests.GraphWorkflows;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TUnit.Core.Interfaces;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     A graph-workflow host whose tool lane runs against a SCRIPTED invocation service. Exactly one seam is replaced,
///     and only for the outcomes the real service cannot be asked for on demand: a fault, a timeout, an oversized
///     answer, and a call that parks long enough for a test to watch a lane slot being held.
///     <para>
///         Everything else is the real thing — the store, the dispatcher, the state machine, the document writer and
///         the encrypted columns — and the D6 envelope itself is never faked anywhere: which tools may run at all is
///         asserted against the real service by <see cref="ToolInvocationServiceTests" />. Tests that need the real
///         one here take the plain <see cref="GraphWorkflowHostFixture" /> instead.
///     </para>
/// </summary>
public sealed class GraphWorkflowToolHostFixture : IAsyncInitializer, IAsyncDisposable
{
    public TestServerWebAppFactory Factory { get; } = NewFactory();

    /// <summary>A tool host of this test's own, for host-level state a concurrent sibling must not see.</summary>
    public static TestServerWebAppFactory NewFactory(params (string Key, string Value)[] configuration) =>
        GraphWorkflowHostFixture.NewFactory(static services =>
            {
                services.RemoveAll<IToolInvocationService>();
                services.AddSingleton<IToolInvocationService, FakeGraphWorkflowToolInvocation>();
            },

            // The concurrency cap counts LIVE runs across the whole database, and a shared host is a shared database.
            // At the shipped default of four, a class's fifth concurrent run sits Pending behind its siblings rather
            // than because of anything the test did. First in the list, so a caller may still pin it — which the
            // bounded fan-out test does, because for it the cap IS the thing under test.
            [("GraphWorkflows:MaxConcurrentRuns", "64"), .. configuration]);

    public Task InitializeAsync() =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() =>
        Factory.DisposeAsync();
}
