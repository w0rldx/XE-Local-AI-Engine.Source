namespace XE_Local_AI_Engine.Tests.Sandbox;

using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation.Launch;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Mapping coverage for <see cref="SandboxLaunchPlan" />: what wrapper chain a given (policy × host containment)
///     produces. These start no processes — the plan is a pure function precisely so the containment mapping can be
///     asserted exactly, including the layer ORDER, which is load-bearing and would otherwise only be observable by
///     running something. Real behavior of the resulting chain (OOM kill, egress denial) is covered by the live-gated
///     tests in <see cref="ProcessSandboxRuntimeProviderTests" />.
/// </summary>
public sealed class SandboxLaunchPlanTests
{
    [Test]
    public async Task SandboxLaunchPlan_WhenHostContainsNothing_ReturnsThePlainCommandUnwrapped()
    {
        var plan = SandboxLaunchPlan.Create("/bin/echo", ["hello"], Limits(memoryMb: 512, denyEgress: true), SandboxContainment.None);

        // A policy asking for everything on a host that offers nothing must still run the command — degraded, not failed.
        AssertEx.Equal("/bin/echo", plan.FileName);
        AssertEx.Equal(expected: 1, plan.Arguments.Count);
        AssertEx.Equal("hello", plan.Arguments[0]);
        AssertEx.False(plan.AppliedResourceLimits);
        AssertEx.False(plan.AppliedNetworkIsolation);
        AssertEx.False(plan.AppliedProcessGroup);
        AssertEx.Empty(plan.WrapperEnvironment);

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_MapsEveryResourceLimit_OntoItsSystemdProperty()
    {
        var plan = SandboxLaunchPlan.Create("/bin/true", [], Limits(memoryMb: 256, pidsLimit: 32, cpuCount: 0.5), FullContainment());

        var properties = PropertyValues(plan);
        AssertEx.Contains(properties, "MemoryMax=256M");
        AssertEx.Contains(properties, "TasksMax=32");
        AssertEx.Contains(properties, "CPUQuota=50%");
        AssertEx.True(plan.AppliedResourceLimits);

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_PairsMemoryMaxWithMemorySwapMaxZero_SoTheCeilingActuallyOomKills()
    {
        // Measured, not assumed: with swap available, MemoryMax alone lets a child reclaim to swap and allocate straight
        // past the ceiling (400 MiB succeeded under MemoryMax=128M). MemorySwapMax=0 is what turns the ceiling into a
        // real kill, so it must never be emitted independently of MemoryMax.
        var withMemory = PropertyValues(SandboxLaunchPlan.Create("/bin/true", [], Limits(memoryMb: 128), FullContainment()));
        AssertEx.Contains(withMemory, "MemoryMax=128M");
        AssertEx.Contains(withMemory, "MemorySwapMax=0");

        var withoutMemory = PropertyValues(SandboxLaunchPlan.Create("/bin/true", [], Limits(pidsLimit: 8), FullContainment()));
        AssertEx.False(withoutMemory.Contains("MemorySwapMax=0"), "MemorySwapMax must not appear without MemoryMax");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_RoundsFractionalCpuCountUp_SoARequestIsNeverSilentlyTightened()
    {
        // 1.5 cores is 150%. A truncating conversion would hand the caller 100% — a tighter ceiling than they asked for.
        AssertEx.Contains(PropertyValues(SandboxLaunchPlan.Create("/bin/true", [], Limits(cpuCount: 1.5), FullContainment())), "CPUQuota=150%");
        AssertEx.Contains(PropertyValues(SandboxLaunchPlan.Create("/bin/true", [], Limits(cpuCount: 0.25), FullContainment())), "CPUQuota=25%");
        // A quota that does not land on a whole percent rounds UP rather than down.
        AssertEx.Contains(PropertyValues(SandboxLaunchPlan.Create("/bin/true", [], Limits(cpuCount: 0.001), FullContainment())), "CPUQuota=1%");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_WhenLimitsCarryNoUsableCeiling_DoesNotWrapInASystemdScope()
    {
        // An empty or non-positive limits record must not produce a systemd-run wrapper carrying no properties.
        foreach (var policy in new[]
                 {
                     new SandboxLaunchPolicy { ResourceLimits = new SandboxResourceLimits() },
                     Limits(memoryMb: 0),
                     Limits(pidsLimit: -1)
                 })
        {
            var plan = SandboxLaunchPlan.Create("/bin/true", [], policy, FullContainment());
            AssertEx.False(plan.AppliedResourceLimits, "an empty limits record must not wrap the command");
            AssertEx.False(plan.Arguments.Contains("--scope"), "no systemd scope should be created");
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_WhenEgressDenied_LaunchesInAnEmptyNetworkNamespaceAsTheCurrentUser()
    {
        var plan = SandboxLaunchPlan.Create("/bin/true", [], new SandboxLaunchPolicy { DenyNetworkEgress = true }, FullContainment());

        var arguments = plan.Arguments;
        AssertEx.True(plan.AppliedNetworkIsolation);
        AssertEx.Contains(arguments, "--net");
        AssertEx.Contains(arguments, "--user");
        // --map-current-user, never --map-root-user: the namespace is what isolates, and pretending to be root only
        // changes how tools behave inside the sandbox versus outside it.
        AssertEx.Contains(arguments, "--map-current-user");
        AssertEx.False(arguments.Contains("--map-root-user"), "the child must not be mapped to root");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_WhenEgressNotRequested_DoesNotCreateANetworkNamespace()
    {
        var plan = SandboxLaunchPlan.Create("/bin/true", [], SandboxLaunchPolicy.Unconstrained, FullContainment());

        AssertEx.False(plan.AppliedNetworkIsolation);
        AssertEx.False(plan.Arguments.Contains("--net"), "an unrestricted sandbox keeps the host network");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_WhenLimitsApplied_StripsTheUserBusAddressBeforeTheChildRuns()
    {
        // SECURITY: systemd-run --user needs the bus address, but a network namespace does NOT confine UNIX sockets, so
        // a child that inherited it could start a unit OUTSIDE its own scope and namespace — verified live before this
        // guard existed. The env -u layer must therefore be present, and must be the LAST wrapper before the command.
        var plan = SandboxLaunchPlan.Create("/bin/true", [], Limits(memoryMb: 128, denyEgress: true), FullContainment());

        var arguments = plan.Arguments;
        foreach (var variable in SandboxLaunchPlan.UserBusVariableNames)
        {
            AssertEx.Contains(arguments, variable);
            // The bus address is injected for the wrapper, so it must be present there…
            AssertEx.True(plan.WrapperEnvironment.ContainsKey(variable), $"{variable} must be supplied to the wrapper");
        }

        var envIndex = arguments.ToList().FindIndex(argument => argument.EndsWith("/env", StringComparison.Ordinal));
        var commandIndex = arguments.ToList().IndexOf("/bin/true");
        AssertEx.True(envIndex >= 0, "the env(1) strip layer must be present");
        AssertEx.True(envIndex < commandIndex, "the strip must happen before the sandboxed command");

        // …and nothing between the strip and the command may re-introduce it.
        var betweenStripAndCommand = arguments.Skip(envIndex).Take(commandIndex - envIndex).ToList();
        AssertEx.Contains(betweenStripAndCommand, "-u");

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_WhenNoLimits_DoesNotInjectTheUserBusAddressAtAll()
    {
        // The bus address exists in the environment ONLY to serve systemd-run. Without that layer it must never be
        // added, because then nothing would strip it back out.
        var plan = SandboxLaunchPlan.Create("/bin/true", [], new SandboxLaunchPolicy { DenyNetworkEgress = true }, FullContainment());

        AssertEx.Empty(plan.WrapperEnvironment);

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_OrdersTheWrapperLayers_SetsidOutermost_ScopeOutsideNamespace_StripInnermost()
    {
        // The order is not cosmetic. setsid must be outermost or the started pid is not the process-group id the reaper
        // keys on; systemd-run must sit outside unshare or it cannot reach the user bus; the strip must be innermost or
        // it would remove the address the scope itself needs.
        var plan = SandboxLaunchPlan.Create("/bin/true", ["--flag"], Limits(memoryMb: 128, denyEgress: true), FullContainment());

        AssertEx.Equal("/usr/bin/setsid", plan.FileName);

        var arguments = plan.Arguments.ToList();
        var systemdIndex = arguments.IndexOf("/usr/bin/systemd-run");
        var unshareIndex = arguments.IndexOf("/usr/bin/unshare");
        var envIndex = arguments.IndexOf("/usr/bin/env");
        var commandIndex = arguments.IndexOf("/bin/true");

        AssertEx.True(systemdIndex >= 0 && systemdIndex < unshareIndex, "the systemd scope must wrap outside the namespace");
        AssertEx.True(unshareIndex < envIndex, "the namespace must wrap outside the environment strip");
        AssertEx.True(envIndex < commandIndex, "the environment strip must be the last layer before the command");

        // The original command and its arguments survive intact at the tail.
        AssertEx.Equal("--flag", arguments[^1]);
        AssertEx.True(plan.AppliedProcessGroup);
        AssertEx.True(plan.AppliedResourceLimits);
        AssertEx.True(plan.AppliedNetworkIsolation);

        await Task.CompletedTask;
    }

    [Test]
    public async Task SandboxLaunchPlan_AppliesOnlyTheMechanismsTheHostActuallyHas()
    {
        // Partial containment is the common degraded case (setsid but no user systemd bus). Each layer is independent:
        // what the host cannot do is simply absent, and the Applied flags report that honestly rather than claiming it.
        var processGroupOnly = SandboxContainment.None with
        {
            SupportsProcessGroup = true,
            SetsidPath = "/usr/bin/setsid",
            ResourceLimitsUnavailableReason = "no user systemd bus"
        };

        var plan = SandboxLaunchPlan.Create("/bin/true", [], Limits(memoryMb: 128, denyEgress: true), processGroupOnly);

        AssertEx.Equal("/usr/bin/setsid", plan.FileName);
        AssertEx.True(plan.AppliedProcessGroup);
        AssertEx.False(plan.AppliedResourceLimits, "a host without the mechanism must not claim to enforce limits");
        AssertEx.False(plan.AppliedNetworkIsolation, "a host without the mechanism must not claim to deny egress");
        AssertEx.False(plan.Arguments.Contains("--scope"));
        AssertEx.False(plan.Arguments.Contains("--net"));

        await Task.CompletedTask;
    }

    // ---- helpers ----

    private static SandboxContainment FullContainment()
    {
        return new SandboxContainment
        {
            SupportsProcessGroup = true,
            SupportsResourceLimits = true,
            SupportsNetworkIsolation = true,
            SetsidPath = "/usr/bin/setsid",
            SystemdRunPath = "/usr/bin/systemd-run",
            UnsharePath = "/usr/bin/unshare",
            EnvPath = "/usr/bin/env",
            UserBusEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["XDG_RUNTIME_DIR"] = "/run/user/1000",
                ["DBUS_SESSION_BUS_ADDRESS"] = "unix:path=/run/user/1000/bus"
            }
        };
    }

    private static SandboxLaunchPolicy Limits(int? memoryMb = null,
        int? pidsLimit = null,
        double? cpuCount = null,
        bool denyEgress = false)
    {
        return new SandboxLaunchPolicy
        {
            ResourceLimits = new SandboxResourceLimits
            {
                MemoryMb = memoryMb,
                PidsLimit = pidsLimit,
                CpuCount = cpuCount
            },
            DenyNetworkEgress = denyEgress
        };
    }

    // The value that follows each -p flag, i.e. the systemd properties the scope is created with.
    private static List<string> PropertyValues(SandboxLaunchDescriptor plan)
    {
        var values = new List<string>();
        for (var index = 0; index < plan.Arguments.Count - 1; index++)
        {
            if (string.Equals(plan.Arguments[index], "-p", StringComparison.Ordinal))
            {
                values.Add(plan.Arguments[index + 1]);
            }
        }

        return values;
    }
}
