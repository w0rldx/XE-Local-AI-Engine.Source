namespace XE_Local_AI_Engine.Tests.Architecture;

using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Client.Services.Compute;
using XE_Local_AI_Engine.Client.Services.Sandbox;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container;
using XE_Local_AI_Engine.Client.Services.Sandbox.Container.Implementation;
using XE_Local_AI_Engine.Client.Services.Sandbox.Fake;
using XE_Local_AI_Engine.Client.Services.Sandbox.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     The replacement for a compile error, and the honest cost of ADR 0007.
///     <para>
///         "Docker cannot be wired into AgentHome" used to be an absent <c>implements</c> clause. Under ADR 0007
///         Decision 4 it is this file: an enumeration of every requirements constant this engine owns, each asserted
///         against the EXACT set of backends allowed to serve it. A compile error cannot be skipped, disabled or made
///         flaky and a test can — so this test is written to be the least flaky kind there is. It touches no daemon, no
///         network and no host mechanism: the capability sets it evaluates against are stated below, not measured, so
///         it decides the same way on a laptop with bwrap and Docker as on an offline CI runner with neither.
///     </para>
/// </summary>
public sealed class SandboxSubstrateSelectionArchitectureTests
{
    /// <summary>
    ///     The MAXIMUM each backend can ever advertise — every flag it would report on the most capable host there is.
    ///     Evaluating against the maximum is what makes the negative assertions strong: a backend excluded here is
    ///     excluded on every host, not merely on the one running the test. A host that measures less (no bwrap, no user
    ///     namespaces, Windows) is a subset, which can only shrink an allowed set, never widen it.
    ///     <para>
    ///         <c>ProviderToolchainAndCapabilityFlagsDoNotDrift</c> keeps this table honest against the real providers.
    ///     </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, SandboxProviderCapabilities> MaximumCapabilities =
        new Dictionary<string, SandboxProviderCapabilities>(StringComparer.Ordinal)
        {
            [FakeSandboxRuntimeProvider.Name] = SandboxProviderCapabilities.SupportsCopyInto
                                                | SandboxProviderCapabilities.SupportsCopyOut
                                                | SandboxProviderCapabilities.SupportsCommandCancellation
                                                | SandboxProviderCapabilities.SupportsAttach
                                                | SandboxProviderCapabilities.SupportsKill
                                                | SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                                | SandboxProviderCapabilities.SuppliesHostToolchain,
            [ProcessSandboxRuntimeProvider.Name] = SandboxProviderCapabilities.SupportsCopyInto
                                                   | SandboxProviderCapabilities.SupportsCopyOut
                                                   | SandboxProviderCapabilities.SupportsCommandCancellation
                                                   | SandboxProviderCapabilities.SupportsAttach
                                                   | SandboxProviderCapabilities.SupportsKill
                                                   | SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                                   | SandboxProviderCapabilities.SuppliesHostToolchain
                                                   | SandboxProviderCapabilities.SupportsResourceLimits
                                                   | SandboxProviderCapabilities.SupportsNetworkPolicy
                                                   | SandboxProviderCapabilities.SupportsFilesystemIsolation
                                                   | SandboxProviderCapabilities.SupportsHostFilesystemBoundary,
            [DockerSandboxRuntimeProvider.Name] = SandboxProviderCapabilities.SupportsCopyInto
                                                  | SandboxProviderCapabilities.SupportsCopyOut
                                                  | SandboxProviderCapabilities.SupportsReadOnlyMounts
                                                  | SandboxProviderCapabilities.SupportsNetworkPolicy
                                                  | SandboxProviderCapabilities.SupportsResourceLimits
                                                  | SandboxProviderCapabilities.SupportsCommandCancellation
                                                  | SandboxProviderCapabilities.SupportsAttach
                                                  | SandboxProviderCapabilities.SupportsKill
                                                  | SandboxProviderCapabilities.SupportsTrustedHostWorkspace
                                                  | SandboxProviderCapabilities.SuppliesImageToolchain
                                                  | SandboxProviderCapabilities.SupportsHostFilesystemBoundary
        };

    /// <summary>
    ///     Every consumer declaration, and the exact backends allowed to serve it.
    ///     <para>
    ///         <c>fake</c> appears in most sets and that is correct rather than a loophole: it executes nothing at all,
    ///         it is the CI-mandatory default, and it is the least privileged thing that can satisfy any declaration.
    ///         The assertion that matters in each row is which EXECUTING backend is admitted — <c>process</c> for every
    ///         host-toolchain workload, and <c>docker</c> for exactly one row.
    ///     </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> AllowedBackends =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            // AgentHome runs host binaries, keeps nothing, and asks for no boundary beyond the jail it always had.
            [nameof(SandboxWorkloads.AgentHome)] = ["fake", "process"],
            // Coder creates no sandbox: it attaches to AgentHome's, so it must resolve the same backend.
            [nameof(SandboxWorkloads.Coder)] = ["fake", "process"],
            [nameof(SandboxWorkloads.WorkSession)] = ["fake", "process"],
            // run_python is the one workload with a filesystem floor, and the fake has no mount namespace — so unlike
            // every other row the deterministic backend is excluded here and the refusal is the feature. The container
            // backend clears that floor (it advertises SupportsHostFilesystemBoundary) and is still excluded, on the
            // toolchain axis alone: compute runs an engine-provisioned host interpreter and never asks for an image.
            [nameof(SandboxWorkloads.RunPython)] = ["process"],
            // A Sandboxed stdio MCP server: the second workload to declare a filesystem floor, and excluded from the
            // deterministic backend for run_python's reason — the fake has no mount namespace, so it cannot serve a
            // boundary, and refusing is the feature. The container backend is excluded on the toolchain axis: an MCP
            // server is whatever the operator installed on this host (npx, uvx, a compiled binary), which no image
            // supplies. So this row resolves to the process backend and to nothing else, which is what
            // docs/security/mcp-trust-tiers.md claims and what would otherwise be only a comment.
            [nameof(SandboxWorkloads.McpStdio)] = ["process"],
            [nameof(SandboxWorkloads.DevelopmentModeHostToolchain)] = ["fake", "process"],
            // The ONLY row a container backend appears in, and the only declaration that names an image toolchain.
            [nameof(SandboxWorkloads.DevelopmentModeImageToolchain)] = ["docker"]
        };

    [Test]
    public void EveryDeclaration_IsServedByExactlyTheEnumeratedBackends()
    {
        foreach (var (name, requirements) in EnumerateDeclarations())
        {
            AssertEx.True(AllowedBackends.ContainsKey(name),
                $"SandboxWorkloads.{name} is a new consumer declaration and is not enumerated here. ADR 0007 Decision 4 makes "
                + "this enumeration the replacement for the compile-time guard, so a declaration it does not cover is a hole in it: "
                + "state the exact backends allowed to serve it.");

            var allowed = AllowedBackends[name];
            foreach (var (backend, toolchain) in SandboxProviderSelector.BackendRanking)
            {
                var unmet = SandboxProviderSelector.FindUnmetAxis(requirements, toolchain, () => MaximumCapabilities[backend]);
                var expected = Array.Exists(allowed, candidate => string.Equals(candidate, backend, StringComparison.Ordinal));

                AssertEx.Equal(expected,
                    unmet is null,
                    expected
                        ? $"SandboxWorkloads.{name} must be servable by '{backend}', but it cannot honour {unmet}."
                        : $"SandboxWorkloads.{name} must NOT be servable by '{backend}'. Widening a declaration so that a backend "
                          + "becomes eligible is a decision, not an implementation detail — see ADR 0007.");
            }
        }
    }

    /// <summary>
    ///     ADR 0004 §1's narrowing, restated as the property that survives the move from an <c>implements</c> clause to
    ///     a selected backend: a container backend serves exactly one workload, and only because that workload declares
    ///     it needs a toolchain the host cannot give it.
    /// </summary>
    [Test]
    public void OnlyDevelopmentMode_CanEverDeclareAnImageBackedToolchain()
    {
        var declaring = EnumerateDeclarations()
                        .Where(static declaration => declaration.Requirements.Toolchain == SandboxToolchainSource.EngineApprovedImage)
                        .Select(static declaration => declaration.Name)
                        .ToArray();

        AssertEx.Equal(1, declaring.Length, $"Expected exactly one image-backed declaration; found: {string.Join(", ", declaring)}.");
        AssertEx.Equal(nameof(SandboxWorkloads.DevelopmentModeImageToolchain), declaring[0]);
    }

    /// <summary>
    ///     The same enumeration, for the axis the operator-facing isolation summary reports as SERVED. Ceilings are a
    ///     PREFERENCE on the create request — a backend may drop them, and one that is never asked applies none — so
    ///     "the host can impose ceilings" and "this role is given ceilings" are different facts, and only the second is
    ///     worth reporting.
    ///     <para>
    ///         EVERY declaration asks now. <c>run_python</c> was the only one until 2026-08-25, and the follow-up that
    ///         changed it is the reason this test asserts the whole set rather than a count: a declaration that stops
    ///         asking is a role that silently becomes unbounded, which is exactly the state the isolation panel was
    ///         built to make visible, and it must not be reachable by deleting one line.
    ///     </para>
    /// </summary>
    [Test]
    public void EveryDeclaration_AsksForResourceCeilings()
    {
        var silent = EnumerateDeclarations()
                     .Where(static declaration => !declaration.Requirements.RequestsResourceLimits)
                     .Select(static declaration => declaration.Name)
                     .ToArray();

        AssertEx.Equal(0,
            silent.Length,
            "Every sandbox workload asks for the node's CPU / memory / process-count ceilings wherever the backend can "
            + $"impose them. These declare that they do not, so nothing bounds a runaway command in them: {string.Join(", ", silent)}. "
            + "Making a role unbounded is an operator decision, not an implementation detail.");
    }

    /// <summary>
    ///     The half of the ceilings guarantee that used to be unreachable from this file, because it constructs no
    ///     consumer: every create site derives its <c>SandboxCreateRequest.ResourceLimits</c> from
    ///     <see cref="SandboxResourceCeilings.Resolve" /> WITH ITS OWN DECLARATION, so "the request agrees with the
    ///     declaration" is a property of one pure function rather than of five call sites. This asserts that function
    ///     over every declaration and both sides of the capability gate; each site's own test then asserts it calls it.
    ///     <para>
    ///         It also asserts WHICH set each declaration gets, which is the half that matters since 2026-08-25: a
    ///         toolchain role handed <c>run_python</c>'s numbers does not build slowly, it fails
    ///         (<see cref="SandboxToolchainLimits" />), and nothing else in the tree would catch the profile being
    ///         changed on one constant.
    ///     </para>
    /// </summary>
    [Test]
    public void ResourceCeilings_AreTheProfileTheDeclarationNamesAndTheCapabilityAllows()
    {
        var computeDefaults = new ComputeOptions();
        var nodeDefaults = new LocalContainerOptions();
        var expectedToolchain = SandboxResourceCeilings.ResolveToolchain(nodeDefaults.ToolchainLimits);

        foreach (var (name, requirements) in EnumerateDeclarations())
        {
            foreach (var (backend, _) in SandboxProviderSelector.BackendRanking)
            {
                var capabilities = MaximumCapabilities[backend];
                var ceilings = SandboxResourceCeilings.Resolve(requirements, capabilities, computeDefaults, nodeDefaults);
                var expected = requirements.RequestsResourceLimits
                               && capabilities.HasFlag(SandboxProviderCapabilities.SupportsResourceLimits);

                AssertEx.Equal(expected,
                    ceilings is not null,
                    $"SandboxWorkloads.{name} on '{backend}': a role gets ceilings exactly when it declares a profile "
                    + "AND the backend advertises that it can impose them.");

                if (ceilings is null)
                {
                    continue;
                }

                var wanted = requirements.Ceilings switch
                {
                    SandboxCeilingProfile.ComputeTool => new SandboxResourceLimits
                    {
                        CpuCount = computeDefaults.CpuCount,
                        MemoryMb = computeDefaults.MemoryMb,
                        PidsLimit = computeDefaults.PidsLimit
                    },
                    SandboxCeilingProfile.HostToolchain => expectedToolchain,
                    _ => null
                };

                AssertEx.Equal(wanted,
                    ceilings,
                    $"SandboxWorkloads.{name} declares {requirements.Ceilings} and must be given exactly that profile's "
                    + "numbers. Moving a toolchain role onto the compute profile is how a `dotnet build` starts being "
                    + "OOM-killed on every attempt.");
            }
        }
    }

    /// <summary>
    ///     Exactly one workload is on the tight compute profile, and it is the one that runs model-written scripts.
    ///     Widening that set is a decision — the profile carries a 2 GB / 64-task ceiling that no real toolchain
    ///     survives — so it fails here rather than in a user's first failed attempt.
    /// </summary>
    [Test]
    public void OnlyRunPython_IsOnTheComputeCeilingProfile()
    {
        var compute = EnumerateDeclarations()
                      .Where(static declaration => declaration.Requirements.Ceilings == SandboxCeilingProfile.ComputeTool)
                      .Select(static declaration => declaration.Name)
                      .ToArray();

        AssertEx.Equal(1, compute.Length, $"Expected exactly one compute-profile declaration; found: {string.Join(", ", compute)}.");
        AssertEx.Equal(nameof(SandboxWorkloads.RunPython), compute[0]);
    }

    /// <summary>
    ///     The derived host-toolchain defaults, as a pure function of the two host facts — so the formula is pinned
    ///     without needing a machine of any particular shape.
    /// </summary>
    [Test]
    [Arguments(16, 64L * 1024 * 1024 * 1024, 16, 49152)]
    [Arguments(4, 4L * 1024 * 1024 * 1024, 4, 4096)]
    [Arguments(2, 2L * 1024 * 1024 * 1024, 2, 2048)]
    [Arguments(1, 0L, 1, SandboxToolchainLimits.DefaultMemoryFloorMb)]
    public void ToolchainDefaults_AreDerivedFromTheHost(int processors, long physicalBytes, double expectedCpu, int expectedMemoryMb)
    {
        var derived = SandboxResourceCeilings.DeriveToolchainDefaults(processors, physicalBytes);

        AssertEx.Equal(expectedCpu, derived.CpuCount);

        // 75% of RAM, floored at 4096 MB so a small machine still gets a workable build ceiling, and capped at
        // physical RAM so that floor cannot promise memory the host does not have.
        AssertEx.Equal(expectedMemoryMb, derived.MemoryMb);
        AssertEx.Equal(SandboxToolchainLimits.DefaultPidsLimit, derived.PidsLimit);
    }

    /// <summary>
    ///     The effective toolchain ceilings on THIS host: unset configuration derives from the machine, and whatever it
    ///     derives must itself clear the floors the operator-facing validator enforces. A host whose derived default
    ///     would have been rejected as an override is a formula that promises a ceiling no build could meet.
    /// </summary>
    [Test]
    public void ToolchainDefaults_OnThisHost_ClearTheFloorsTheValidatorEnforces()
    {
        var effective = SandboxResourceCeilings.ResolveToolchain(new SandboxToolchainLimits());

        AssertEx.Equal(SandboxResourceCeilings.DeriveToolchainDefaults(Environment.ProcessorCount,
                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes),
            effective,
            "unset configuration must be exactly the host derivation, not a second set of literals.");
        AssertEx.True(effective.CpuCount >= 1, $"derived CpuCount was {effective.CpuCount}.");
        AssertEx.True(effective.MemoryMb >= SandboxToolchainLimits.MinimumMemoryMb, $"derived MemoryMb was {effective.MemoryMb}.");
        AssertEx.True(effective.PidsLimit >= SandboxToolchainLimits.MinimumPidsLimit, $"derived PidsLimit was {effective.PidsLimit}.");
    }

    /// <summary>
    ///     The selector reads a backend's toolchain from its own code-owned ranking rather than from
    ///     <see cref="ISandboxRuntimeProvider.Capabilities" />, so that resolving a host-toolchain workload never has to
    ///     construct or probe a backend it is about to reject. That is two statements of one fact, so this asserts they
    ///     agree — and that every real provider stays within the maximum this file evaluates against.
    /// </summary>
    [Test]
    public async Task ProviderToolchainAndCapabilityFlagsDoNotDrift()
    {
        await using var services = BuildBackends();

        foreach (var (name, toolchain) in SandboxProviderSelector.BackendRanking)
        {
            var provider = AssertEx.NotNull(Locate(services, name), $"No registered backend named '{name}'.");
            var advertised = provider.Capabilities;

            var expectedFlag = toolchain == SandboxToolchainSource.EngineApprovedImage
                ? SandboxProviderCapabilities.SuppliesImageToolchain
                : SandboxProviderCapabilities.SuppliesHostToolchain;
            var forbiddenFlag = toolchain == SandboxToolchainSource.EngineApprovedImage
                ? SandboxProviderCapabilities.SuppliesHostToolchain
                : SandboxProviderCapabilities.SuppliesImageToolchain;

            AssertEx.True(advertised.HasFlag(expectedFlag),
                $"'{name}' is ranked as supplying {toolchain} but does not advertise {expectedFlag}.");
            AssertEx.False(advertised.HasFlag(forbiddenFlag),
                $"'{name}' advertises {forbiddenFlag} as well; a backend supplies exactly one toolchain.");
            AssertEx.Equal(advertised,
                advertised & MaximumCapabilities[name],
                $"'{name}' advertises a capability this file's maximum for it does not list, so the enumeration above is "
                + "evaluating against a stale picture of what that backend can do.");
        }
    }

    private static IEnumerable<(string Name, SandboxRequirements Requirements)> EnumerateDeclarations()
    {
        return typeof(SandboxWorkloads)
               .GetFields(BindingFlags.Public | BindingFlags.Static)
               .Where(static field => field.FieldType == typeof(SandboxRequirements))
               .Select(static field => (field.Name, (SandboxRequirements)field.GetValue(null)!));
    }

    private static ISandboxRuntimeProvider? Locate(IServiceProvider services, string name)
    {
        return name switch
        {
            FakeSandboxRuntimeProvider.Name => services.GetService<FakeSandboxRuntimeProvider>(),
            ProcessSandboxRuntimeProvider.Name => services.GetService<ProcessSandboxRuntimeProvider>(),
            DockerSandboxRuntimeProvider.Name => services.GetService<DockerSandboxRuntimeProvider>(),
            _ => null
        };
    }

    private static ServiceProvider BuildBackends()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddOptions<LocalContainerOptions>().Bind(configuration.GetSection(LocalContainerOptions.SectionName));
        services.AddOptions<ContainerSandboxOptions>().Bind(configuration.GetSection(ContainerSandboxOptions.SectionName));
        services.AddSingleton(Substitute.For<IDockerRuntimeClientFactory>());
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(Path.Combine(Path.GetTempPath(), "xe-substrate-arch-tests")));
        services.AddSingleton<FakeSandboxRuntimeProvider>();
        services.AddSingleton<ProcessSandboxRuntimeProvider>();
        services.AddSingleton<DockerSandboxRuntimeProvider>();
        return services.BuildServiceProvider();
    }
}
