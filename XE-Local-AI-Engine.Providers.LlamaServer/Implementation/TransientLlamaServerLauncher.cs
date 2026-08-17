namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <inheritdoc />
internal sealed class TransientLlamaServerLauncher(
    ILlamaCppBinaryManager binaryManager,
    IGpuVariantSelector variantSelector,
    ILlamaServerProcessLauncher launcher,
    ILlamaServerHealthProbe healthProbe,
    ILogger<TransientLlamaServerLauncher> logger) : ITransientLlamaServerLauncher
{
    /// <summary>How often the readiness race re-checks whether the child died instead of becoming ready.</summary>
    private static readonly TimeSpan ExitPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly ILlamaServerHealthProbe _healthProbe = healthProbe ?? throw new ArgumentNullException(nameof(healthProbe));
    private readonly ILlamaServerProcessLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    private readonly ILogger<TransientLlamaServerLauncher> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IGpuVariantSelector _variantSelector = variantSelector ?? throw new ArgumentNullException(nameof(variantSelector));

    public async Task<T> RunAsync<T>(TransientLlamaServerRequest request,
        Func<TransientLlamaServerSession, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(body);
        if (!File.Exists(request.ModelFilePath))
        {
            throw new LlamaRuntimeException("The model file to load was not found.");
        }

        var variant = await _variantSelector.SelectVariantAsync(ct).ConfigureAwait(false);
        var binary = await _binaryManager.EnsureBinaryAsync(variant, ct).ConfigureAwait(false);
        var modelId = Path.GetFileName(request.ModelFilePath);

        // The key's model name is a LABEL here, not a registry lookup: BuildLaunchSpec only ever puts it on the spec
        // for diagnostics, and every file this spawn touches is an explicit path.
        var key = new LlamaServerProcessSupervisor.ProcessKey(modelId, ModelRole.Chat);
        var spec = LlamaServerLaunchArgumentComposer.BuildLaunchSpec(key,
            binary.ServerExecutablePath,
            request.ModelFilePath,
            AllocatePort(),
            variant,
            // Replay, not Explore: a smoke load must not run llama.cpp's auto-fit search, which is a placement
            // decision this throwaway process has no business making on behalf of the next real spawn.
            ResolvedLaunchArguments.Replay(request.ContextTokens),
            chatCacheReuse: 0,
            adapterFilePath: request.AdapterFilePath);

        var handle = _launcher.Launch(spec);
        try
        {
            await WaitForReadyOrExitAsync(handle, spec.BaseAddress, request.ReadinessTimeout, ct).ConfigureAwait(false);
            return await body(new TransientLlamaServerSession(spec.BaseAddress, modelId), ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                handle.TreeKill();
            }
            catch (Exception exception)
            {
                // Best-effort: disposal below still releases the OS resources, and a teardown failure must not mask
                // the outcome the caller came for.
                _logger.LogWarning(exception, "The transient llama-server (pid {ProcessId}) could not be tree-killed.", handle.ProcessId);
            }

            handle.Dispose();
        }
    }

    /// <summary>
    ///     A port the OS just told us is free. There is a window between the probe and llama-server's own bind, which
    ///     is the same window the supervisor's allocator lives with; the loser surfaces as a failed readiness rather
    ///     than as a silent wrong-server connection, because nothing else answers on that port.
    /// </summary>
    private static int AllocatePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, port: 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    ///     Races readiness against the child dying. Without the exit arm, a model the runtime cannot load would burn
    ///     the whole readiness budget before reporting a failure the first second already proved.
    /// </summary>
    private async Task WaitForReadyOrExitAsync(ILlamaServerProcessHandle handle,
        Uri baseAddress,
        TimeSpan readinessTimeout,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readyTask = _healthProbe.WaitForReadyAsync(baseAddress, readinessTimeout, linked.Token);
        var exitTask = WatchForExitAsync(handle, linked.Token);
        var winner = await Task.WhenAny(readyTask, exitTask).ConfigureAwait(false);
        await linked.CancelAsync().ConfigureAwait(false);
        try
        {
            if (winner == exitTask && handle.HasExited)
            {
                throw new LlamaRuntimeException("The model runtime exited while loading the model.");
            }

            if (!await readyTask.ConfigureAwait(false))
            {
                throw new LlamaRuntimeException("The model runtime did not become ready in time.");
            }
        }
        finally
        {
            await SwallowCancellationAsync(readyTask).ConfigureAwait(false);
            await SwallowCancellationAsync(exitTask).ConfigureAwait(false);
        }
    }

    private static async Task WatchForExitAsync(ILlamaServerProcessHandle handle, CancellationToken ct)
    {
        while (!handle.HasExited)
        {
            await Task.Delay(ExitPollInterval, ct).ConfigureAwait(false);
        }
    }

    private static async Task SwallowCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Both arms are abandoned once the race is decided; only the winner's outcome is reported.
        }
    }
}
