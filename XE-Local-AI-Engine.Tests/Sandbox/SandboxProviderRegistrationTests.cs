namespace XE_Local_AI_Engine.Tests.Sandbox;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XE_Local_AI_Engine.Client.DependencyInjection.Modules;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Registration guard for the per-feature sandbox seam, asserted against a REAL built container assembled by the
///     production module extensions rather than by a hand-rolled <see cref="ServiceCollection" />.
///     <para>
///         It exists because nothing else would catch a mis-wired seam. The integration hosts run with
///         <c>UseEnvironment("Testing")</c>, so DI validation never executes, and every other sandbox test constructs
///         its provider directly — so the two role registrations, the option that drives them, and the instance
///         sharing they depend on had no coverage at all. A swap of the two factory delegates would have compiled,
///         started, and passed the whole suite while silently running Development Mode on AgentHome's provider and
///         breaking every Coder tool.
///     </para>
/// </summary>
public sealed class SandboxProviderRegistrationTests
{
    [Test]
    public async Task Container_ResolvesTheAgentRoleFromTheAgentHomeSection()
    {
        await using var host = BuildHost("process");

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name,
            host.Services.GetRequiredService<IAgentSandboxRuntimeProvider>().ProviderName);
    }

    [Test]
    public async Task Container_ResolvesTheDevelopmentRoleFromTheDevelopmentSection()
    {
        await using var host = BuildHost("process", developmentProvider: "fake");

        AssertEx.Equal(FakeSandboxRuntimeProvider.Name,
            host.Services.GetRequiredService<IDevelopmentSandboxRuntimeProvider>().ProviderName);
        // The override is one-way: naming a Development provider must not disturb what AgentHome and Coder execute on.
        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name,
            host.Services.GetRequiredService<IAgentSandboxRuntimeProvider>().ProviderName);
    }

    /// <summary>
    ///     Pins today's shipped configuration, which sets only <c>AgentHome:Sandbox:Provider</c>: Development Mode
    ///     still runs on <c>process</c>, so introducing the seam changed no runtime behaviour.
    /// </summary>
    [Test]
    public async Task Container_WhenOnlyTheAgentSectionIsConfigured_RunsDevelopmentOnThatProvider()
    {
        await using var host = BuildHost("process");

        AssertEx.Equal(ProcessSandboxRuntimeProvider.Name,
            host.Services.GetRequiredService<IDevelopmentSandboxRuntimeProvider>().ProviderName);
    }

    /// <summary>
    ///     The invariant this whole file exists for. <c>ProcessSandboxRuntimeProvider</c> allocates its jail root ONCE
    ///     per instance, and <c>CoderWorkspaceReader.TryConnectAsync</c> reaches AgentHome's live sandbox through
    ///     <c>ConnectAsync</c> with a matching attach key — so a second instance would answer "no workspace available"
    ///     to every coder tool, silently, with no error anywhere.
    /// </summary>
    [Test]
    public async Task Container_WhenBothRolesSelectTheSameProvider_ResolvesTheSameInstance()
    {
        await using var host = BuildHost("process", developmentProvider: "process");

        var agent = host.Services.GetRequiredService<IAgentSandboxRuntimeProvider>();
        var development = host.Services.GetRequiredService<IDevelopmentSandboxRuntimeProvider>();

        AssertEx.True(ReferenceEquals(agent, development),
            "Both roles resolved 'process' and must share one DI singleton; two instances would give Coder a jail root "
            + "AgentHome never wrote to.");
    }

    /// <summary>
    ///     Registration order is load-bearing in one direction only, and this pins it: <c>DockerSandboxRuntimeProvider</c>
    ///     is registered by a LATER module than the one that registers the role factories. Lazy factory delegates make
    ///     that safe, and the way to know it stayed safe is to resolve the container provider out of the real container.
    /// </summary>
    [Test]
    public async Task Container_ResolvesTheContainerProviderForTheDevelopmentRoleAcrossModules()
    {
        await using var host = BuildHost("process", developmentProvider: "docker");

        var development = host.Services.GetRequiredService<IDevelopmentSandboxRuntimeProvider>();

        AssertEx.Equal(DockerSandboxRuntimeProvider.Name, development.ProviderName);
        AssertEx.True(development is DockerSandboxRuntimeProvider);
    }

    /// <summary>
    ///     The bare contract is deliberately unregistered, so a new consumer that forgets to pick a role fails loudly
    ///     at wiring time instead of inheriting whichever provider happened to win.
    /// </summary>
    [Test]
    public async Task Container_DoesNotRegisterTheBareProviderContract()
    {
        await using var host = BuildHost("process");

        AssertEx.Null(host.Services.GetService<ISandboxRuntimeProvider>(),
            "Registering ISandboxRuntimeProvider would reintroduce exactly the global selection per-feature selection replaced.");
    }

    [Test]
    public async Task Container_WhenTheAgentProviderIsUnknown_FailsToResolveTheAgentRole()
    {
        await using var host = BuildHost("does-not-exist");

        var exception = await AssertEx.ThrowsAsync<InvalidOperationException>(() =>
        {
            _ = host.Services.GetRequiredService<IAgentSandboxRuntimeProvider>();
            return Task.CompletedTask;
        });

        AssertEx.Contains(exception.Message, "does-not-exist");
    }

    /// <summary>
    ///     A misspelled Development provider is rejected by the registered options validator, not by the DI factory at
    ///     first use — so the operator learns about it from startup, rather than from a Development attempt failing.
    /// </summary>
    [Test]
    public async Task Container_WhenTheDevelopmentProviderIsUnknown_FailsOptionsValidation()
    {
        await using var host = BuildHost("process", developmentProvider: "dokcer");

        var exception = await AssertEx.ThrowsAsync<OptionsValidationException>(() =>
        {
            _ = host.Services.GetRequiredService<IOptions<DevelopmentSandboxOptions>>().Value;
            return Task.CompletedTask;
        });

        AssertEx.Contains(string.Join(" ", exception.Failures), "dokcer");
    }

    private static TestHost BuildHost(string? agentProvider, string? developmentProvider = null)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (agentProvider is not null)
        {
            configurationValues["AgentHome:Sandbox:Provider"] = agentProvider;
        }

        if (developmentProvider is not null)
        {
            configurationValues["Development:Sandbox:Provider"] = developmentProvider;
        }

        // Development, not Production: SandboxOptionsValidator refuses to start Production with an unset provider, and
        // that rule has its own coverage — this file is about which provider each role gets, not about that gate.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Development
        });
        // ValidateOnBuild off, ValidateScopes on. HostApplicationBuilder turns BOTH on in Development, and
        // ValidateOnBuild walks every registration in the container — including AgentHomeIdentityProvider, whose
        // ITokenStore comes from a module this test deliberately does not compose. Whole-graph validation is a
        // different test's job (and needs the whole host); switching it off here keeps this file about the seam
        // rather than about which module registers what.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = false
        }));
        builder.Configuration.AddInMemoryCollection(configurationValues);
        // Supplied by other AddNode* modules in the real host; the two sandbox modules do not register it themselves.
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.AddNodeAgentHome(builder.Configuration);
        builder.AddNodeContainerSandbox(builder.Configuration);
        return new TestHost(builder.Build());
    }

    /// <summary>
    ///     Async-disposing wrapper. <see cref="IHost" /> declares only <see cref="IDisposable" />, and synchronous
    ///     disposal of a container holding an <see cref="IAsyncDisposable" />-only singleton — which
    ///     <see cref="DockerSandboxRuntimeProvider" /> is — throws rather than disposing it.
    /// </summary>
    private sealed class TestHost(IHost host) : IAsyncDisposable
    {
        public IServiceProvider Services => host.Services;

        public ValueTask DisposeAsync() =>
            ((IAsyncDisposable)host).DisposeAsync();
    }
}
