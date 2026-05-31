namespace XE_Local_AI_Engine.HostAgent.Windows;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

/// <summary>
///     Application service for runtime metadata hosted behavior.
/// </summary>
public sealed class RuntimeMetadataHostedService : IHostedService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IHostApplicationLifetime _applicationLifetime;

    private readonly HostAgentWindowsPaths _paths;
    private readonly IServer _server;
    private readonly TimeProvider _timeProvider;

    public RuntimeMetadataHostedService(HostAgentWindowsPaths paths,
        IServer server,
        IHostApplicationLifetime applicationLifetime,
        TimeProvider timeProvider)
    {
        _paths = paths;
        _server = server;
        _applicationLifetime = applicationLifetime;
        _timeProvider = timeProvider;
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
        Directory.CreateDirectory(_paths.RootDirectory);

        var exePath = ResolveExecutablePath();
        var metadata = new RuntimeMetadata(Environment.ProcessId,
            ResolveAdminPort(),
            exePath,
            ComputeSha256(exePath),
            _timeProvider.GetUtcNow(),
            Guid.NewGuid().ToString("N"),
            ResolveSessionId());

        var json = JsonSerializer.Serialize(metadata, SerializerOptions);
        File.WriteAllText(_paths.RuntimeMetadataPath, json);
    }

    private void DeleteRuntimeMetadata()
    {
        if (File.Exists(_paths.RuntimeMetadataPath))
        {
            File.Delete(_paths.RuntimeMetadataPath);
        }
    }

    private int ResolveAdminPort()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = addresses?.FirstOrDefault(static value => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase));

        return Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Port : 0;
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

    private static string ResolveSessionId()
    {
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(sid))
            {
                return sid;
            }
        }

        return Environment.GetEnvironmentVariable("SESSIONNAME")
               ?? Environment.UserName
               ?? "unknown";
    }
}
