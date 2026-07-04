namespace XE_Local_AI_Engine.Tests.Providers.LlamaServer;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Configuration;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Bring-your-own override: the GPU-variant selector short-circuits to the configured variant (skipping the vendor
///     probe) when active and is byte-identical to the vendor rule when inactive; the override options are resolvable
///     ONLY from process env vars; and the launch-spec still emits/omits GPU placement args by variant so a selector that
///     returns <c>Cuda</c> composes into a GPU spawn.
/// </summary>
public sealed class OverrideSelectorAndOptionsTests
{
    private static readonly LlamaServerProcessSupervisor.ProcessKey ChatKey = new("llama3", ModelRole.Chat);

    [Test]
    public async Task SelectVariant_WhenOverrideActive_ReturnsConfiguredVariant()
    {
        // Override active with variant Cuda on a NON-Windows host whose probe would otherwise pick Vulkan: the override
        // wins and the vendor probe is never consulted.
        var options = new LlamaServerRuntimeOverrideOptions
        {
            ServerPath = "/opt/llama/llama-server",
            Variant = GpuVariant.Cuda
        };
        var selector = new GpuVariantSelector(new ThrowingVendorProbe(), isWindows: false, options);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Cuda, variant);
    }

    [Test]
    public async Task SelectVariant_WhenOverrideInactive_UsesVendorRule()
    {
        // No override → the existing Linux-NVIDIA → Vulkan rule is unchanged (the vendor probe drives the decision).
        var options = new LlamaServerRuntimeOverrideOptions(); // ServerPath null → inactive
        var selector = new GpuVariantSelector(new FakeVendorProbe(DetectedGpuVendor.Nvidia), isWindows: false, options);

        var variant = await selector.SelectVariantAsync(CancellationToken.None);

        AssertEx.Equal(GpuVariant.Vulkan, variant);
    }

    [Test]
    [NotInParallel("XE_LLAMACPP_OVERRIDE_ENV")]
    public void OverrideOptions_NotResolvableFromNodeSettingsStore()
    {
        // [sec HIGH-2] The override is bound ONLY from process env vars via FromEnvironment — there is no IConfiguration,
        // node-settings, or request-DTO source. Round-trip the env channel to prove that is the single activation path:
        // set the env var → active; clear it → inactive. (The type exposes no node-settings/IConfiguration constructor,
        // so a lower-trust store structurally cannot supply the override path.)
        var previousPath = Environment.GetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable);
        var previousVariant = Environment.GetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.VariantEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, "/opt/llama/llama-server");
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.VariantEnvironmentVariable, "cuda");

            var fromEnv = LlamaServerRuntimeOverrideOptions.FromEnvironment();
            AssertEx.True(fromEnv.IsActive, "An override set via the env channel must be active.");
            AssertEx.Equal("/opt/llama/llama-server", fromEnv.ServerPath);
            AssertEx.Equal(GpuVariant.Cuda, fromEnv.Variant);

            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, value: null);
            var cleared = LlamaServerRuntimeOverrideOptions.FromEnvironment();
            AssertEx.False(cleared.IsActive, "With the env var cleared the override must be inactive (no other source can supply it).");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, previousPath);
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.VariantEnvironmentVariable, previousVariant);
        }
    }

    [Test]
    [NotInParallel("XE_LLAMACPP_OVERRIDE_ENV")]
    public void OverrideOptions_WhenVariantSetButUnparseable_Throws()
    {
        var previousPath = Environment.GetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable);
        var previousVariant = Environment.GetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.VariantEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, "/opt/llama/llama-server");
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.VariantEnvironmentVariable, "rocm");

            var threw = false;
            try
            {
                _ = LlamaServerRuntimeOverrideOptions.FromEnvironment();
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            AssertEx.True(threw, "A set-but-unparseable variant must fail fast rather than silently default.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.ServerPathEnvironmentVariable, previousPath);
            Environment.SetEnvironmentVariable(LlamaServerRuntimeOverrideOptions.VariantEnvironmentVariable, previousVariant);
        }
    }

    [Test]
    public void BuildLaunchSpec_WhenVariantCuda_EmitsGpuPlacementArgs()
    {
        // Composes with selector→Cuda: a Cuda spawn with no frozen profile lets llama.cpp auto-fit (--fit on + --metrics).
        var spec = LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cuda,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256);

        AssertEx.Contains(spec.Arguments, "--fit");
        AssertEx.Contains(spec.Arguments, "--metrics");
    }

    [Test]
    public void BuildLaunchSpec_WhenVariantCpu_OmitsGpuPlacementArgs()
    {
        var spec = LlamaServerProcessSupervisor.BuildLaunchSpec(ChatKey,
            "/fake/bin/llama-server",
            "/fake/models/model.gguf",
            port: 8080,
            GpuVariant.Cpu,
            ResolvedLaunchArguments.Explore(),
            chatCacheReuse: 256);

        AssertEx.False(spec.Arguments.Contains("--fit"), "CPU must not emit --fit.");
        AssertEx.False(spec.Arguments.Contains("--metrics"), "CPU must not emit --metrics.");
    }

    private sealed class FakeVendorProbe(DetectedGpuVendor vendor) : IGpuVendorProbe
    {
        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
        {
            return Task.FromResult(vendor);
        }
    }

    private sealed class ThrowingVendorProbe : IGpuVendorProbe
    {
        public Task<DetectedGpuVendor> DetectVendorAsync(CancellationToken ct)
        {
            throw new InvalidOperationException("The vendor probe must not be called when the override is active.");
        }
    }
}
