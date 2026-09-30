namespace XE_Local_AI_Engine.Tests.Providers.ProcessSupervision;

using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetArchTest.Rules;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;
using XE_Local_AI_Engine.Providers.LlamaServer.Implementation;
using XE_Local_AI_Engine.Providers.ProcessSupervision;
using XE_Local_AI_Engine.Providers.Python.Implementation;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Implementation;
using XE_Local_AI_Engine.Providers.WhisperCpp;
using XE_Local_AI_Engine.Providers.WhisperCpp.Implementation;
using XE_Local_AI_Engine.Tests.Providers.LlamaServer;
using XE_Local_AI_Engine.Tests.Testing;
using OS = TUnit.Core.Enums.OS;

/// <summary>
///     Proves the four providers that supervise a child process bind the shared containment in
///     <c>Providers.ProcessSupervision</c> rather than a private copy, and that each runtime's stale reaper survives
///     registration. <see cref="ProviderLauncherSharedHandleTests" /> proves the same at run time.
/// </summary>
[Category(TestCategories.Unit)]
public sealed class ProviderProcessSupervisionWiringTests
{
    private const string SharedNamespace = "XE_Local_AI_Engine.Providers.ProcessSupervision";

    /// <summary>
    ///     The compiled launchers and runners bind the shared handle types. The Python runner owns its handle privately,
    ///     so it cannot be observed through a return value; its IL is the evidence.
    /// </summary>
    [Test]
    [Arguments(typeof(LlamaServerProcessLauncher), "LinuxProcessGroupHandle")]
    [Arguments(typeof(LlamaServerProcessLauncher), "WindowsJobObjectProcessHandle")]
    [Arguments(typeof(LlamaServerProcessLauncher), "PlainProcessHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "LinuxProcessGroupHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "WindowsJobObjectProcessHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "PlainProcessHandle")]
    [Arguments(typeof(ImageServerProcessLauncher), "ProcessStderrTail")]
    [Arguments(typeof(WhisperServerProcessLauncher), "LinuxProcessGroupHandle")]
    [Arguments(typeof(WhisperServerProcessLauncher), "WindowsJobObjectProcessHandle")]
    [Arguments(typeof(WhisperServerProcessLauncher), "PlainProcessHandle")]
    [Arguments(typeof(WhisperServerProcessLauncher), "ProcessStderrTail")]
    [Arguments(typeof(LinuxPythonToolRunner), "LinuxProcessGroupHandle")]
    public void LauncherOrRunner_DependsOnTheSharedType(Type consumer, string sharedType)
    {
        var result = Types.InAssembly(consumer.Assembly)
                          .That()
                          .HaveName(consumer.Name)
                          .Should()
                          .HaveDependencyOn($"{SharedNamespace}.{sharedType}")
                          .GetResult();

        AssertEx.True(result.IsSuccessful,
            $"{consumer.Name} no longer uses the shared {sharedType}; a private copy of process containment has come back.");
    }

    /// <summary>
    ///     All three runtimes register the same reaper type. <c>AddHostedService</c> dedupes by implementation type, so a
    ///     registration through it would leave only the first runtime's reaper and silently stop reaping the others.
    /// </summary>
    [Test]
    public void EveryRuntimeRegistersItsOwnStaleReaper()
    {
        var services = new ServiceCollection();
        services.AddLlamaServerLocalModelProvider();
        services.AddStableDiffusionCppImageRuntime();
        services.AddWhisperCppRuntime();

        var reapers = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                                   && descriptor.ImplementationFactory?.Method.ReturnType == typeof(StaleProcessReaper))
                              .ToList();

        AssertEx.Equal(expected: 3, reapers.Count,
            "Expected one stale reaper each for llama-server, sd-server and whisper-server.");
    }

    /// <summary>
    ///     Each reaper reads back the receipts its own runtime writes, and for llama-server the SAME store the supervisor and the
    ///     transient launcher write through.
    /// </summary>
    /// <remarks>
    ///     A reaper built without one silently falls back to the binaries-root match and misses every BYO orphan.
    ///     The stores are private fields, so they are read reflectively; a rename fails loudly here. The reapers are constructed
    ///     from their descriptors, never started, so no process is scanned or signalled.
    /// </remarks>
    [Test]
    [RunOn(OS.Linux)]
    [SupportedOSPlatform("linux")]
    public async Task EachReaperClosesOverItsRuntimesReceiptStore_AndTheLlamaWritersShareIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"xe-receipt-wiring-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INodeDataDirectory>(new FakeNodeDataDirectory(root));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ILlamaCppBinaryManager>(new FakeBinaryManager());
        services.AddSingleton<IGpuVariantSelector>(new FakeVariantSelector());
        services.AddSingleton<IGgufModelStore>(new FakeModelStore());
        services.AddSingleton<ILlamaServerCapabilityManifestProbe>(new FakeLlamaServerCapabilityManifestProbe());
        services.AddSingleton<ILlamaServerLaunchFallbackStore>(new FakeLaunchFallbackStore());
        services.AddLlamaServerLocalModelProvider();
        services.AddStableDiffusionCppImageRuntime();
        services.AddWhisperCppRuntime();
        var reaperFactories = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                                                           && descriptor.ImplementationFactory?.Method.ReturnType == typeof(StaleProcessReaper))
                                      .Select(descriptor => descriptor.ImplementationFactory!)
                                      .ToList();
        await using var provider = services.BuildServiceProvider();
        try
        {
            string[] serverNames = ["llama-server", "sd-server", "whisper-server"];
            AssertEx.Equal(serverNames.Length, reaperFactories.Count);
            for (var index = 0; index < serverNames.Length; index++)
            {
                var reaper = (StaleProcessReaper)reaperFactories[index](provider);
                var store = AssertEx.NotNull(PrivateField<ProcessSpawnReceiptStore?>(reaper, "_receipts"), $"The {serverNames[index]} reaper has no receipt store.");
                AssertEx.True(ReferenceEquals(provider.GetRequiredKeyedService<ProcessSpawnReceiptStore>(serverNames[index]), store),
                    $"The {serverNames[index]} reaper must read through its runtime's one registered store.");
                await AssertWritesUnderAsync(store, Path.Combine(root, "runtime", serverNames[index]));
            }

            var llamaStore = provider.GetRequiredKeyedService<ProcessSpawnReceiptStore>("llama-server");
            AssertEx.True(ReferenceEquals(llamaStore, PrivateField<ProcessSpawnReceiptStore>(provider.GetRequiredService<LlamaServerProcessSupervisor>(), "_spawnReceipts")),
                "The supervisor must write through the store the reaper reads.");
            AssertEx.True(ReferenceEquals(llamaStore, PrivateField<ProcessSpawnReceiptStore>(provider.GetRequiredService<TransientLlamaServerLauncher>(), "_spawnReceipts")),
                "The transient launcher must share the llama-server store, not keep a private disabled one.");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    // The store proves where it writes by writing: a receipt for this test process lands in the runtime's own directory and leaves with the handle.
    private static async Task AssertWritesUnderAsync(ProcessSpawnReceiptStore store, string directory)
    {
        AssertEx.True(store.IsEnabled, "A host with a data directory must get an enabled store.");
        using var handle = new FakeProcessHandle(Environment.ProcessId);
        var tracked = await store.TrackAsync(handle, Environment.ProcessPath!, "wiring", CancellationToken.None);
        AssertEx.True(File.Exists(Path.Combine(directory, $"{Environment.ProcessId}.json")), $"The receipt must be written under {directory}.");
        tracked.Dispose();
        AssertEx.False(File.Exists(Path.Combine(directory, $"{Environment.ProcessId}.json")));
    }

    private static T PrivateField<T>(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException($"{owner.GetType().Name}.{name} no longer exists; update this wiring test.");
        return (T)field.GetValue(owner)!;
    }
}
