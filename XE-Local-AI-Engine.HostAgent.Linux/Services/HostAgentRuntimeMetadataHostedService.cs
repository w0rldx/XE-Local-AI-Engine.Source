namespace XE_Local_AI_Engine.HostAgent.Linux.Services;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using XE_Local_AI_Engine.HostAgent.Linux.Hosting;

public sealed class HostAgentRuntimeMetadataHostedService : IHostedService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly HostAgentAdminOptions _adminOptions;
    private readonly IHostApplicationLifetime _applicationLifetime;

    private readonly IServer _server;
    private readonly HostAgentTcpOptions _tcpOptions;
    private readonly TimeProvider _timeProvider;

    public HostAgentRuntimeMetadataHostedService(IServer server,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider,
        HostAgentAdminOptions adminOptions,
        HostAgentTcpOptions tcpOptions)
    {
        _server = server;
        _applicationLifetime = applicationLifetime;
        _timeProvider = timeProvider;
        _adminOptions = adminOptions;
        _tcpOptions = tcpOptions;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _applicationLifetime.ApplicationStarted.Register(WriteRuntimeMetadata);
        _applicationLifetime.ApplicationStopping.Register(DeleteRuntimeMetadata);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        DeleteRuntimeMetadata();
        return Task.CompletedTask;
    }

    private void WriteRuntimeMetadata()
    {
        var runtimeDirectory = ResolveRuntimeDirectory();
        Directory.CreateDirectory(runtimeDirectory);

        var exePath = ResolveExecutablePath();
        var metadata = new
        {
            pid = Environment.ProcessId,
            adminPort = ResolveAdminPort(),
            exePath,
            exeSha256 = ComputeSha256(exePath),
            startedAt = _timeProvider.GetUtcNow(),
            tokenGenerationId = Guid.NewGuid().ToString("N"),
            sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID") ?? Environment.UserName ?? "unknown"
        };

        File.WriteAllText(Path.Combine(runtimeDirectory, "runtime.json"), JsonSerializer.Serialize(metadata, SerializerOptions));
    }

    private static void DeleteRuntimeMetadata()
    {
        var runtimeDirectory = TryResolveRuntimeDirectory();
        if (runtimeDirectory is null)
        {
            return;
        }

        var path = Path.Combine(runtimeDirectory, "runtime.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private int ResolveAdminPort()
    {
        if (_adminOptions.Port > 0)
        {
            return _adminOptions.Port;
        }

        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?
                      .Select(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null)
                      .Where(uri => uri is not null && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                      .FirstOrDefault(uri => !_tcpOptions.Enabled || uri!.Port != _tcpOptions.Port);

        return address?.Port ?? 0;
    }

    private static string ResolveRuntimeDirectory()
    {
        return TryResolveRuntimeDirectory()
               ?? throw new InvalidOperationException("XDG_RUNTIME_DIR is required for HostAgent runtime metadata.");
    }

    private static string? TryResolveRuntimeDirectory()
    {
        var xdgRuntimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return string.IsNullOrWhiteSpace(xdgRuntimeDirectory)
            ? null
            : Path.Combine(xdgRuntimeDirectory, "xe-host-agent");
    }

    private static string ResolveExecutablePath()
    {
        return Environment.ProcessPath
               ?? Process.GetCurrentProcess().MainModule?.FileName
               ?? AppContext.BaseDirectory;
    }

    private static string ComputeSha256(string exePath)
    {
        using var stream = File.OpenRead(exePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
