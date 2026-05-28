namespace XE_Local_AI_Engine.HostAgent.Linux;

using System.Data.Common;
using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;
using OllamaSharp;
using XE_Local_AI_Engine.HostAgent.Linux.Capabilities;
using XE_Local_AI_Engine.HostAgent.Linux.Capabilities.Implementation;
using XE_Local_AI_Engine.HostAgent.Linux.Docker;
using XE_Local_AI_Engine.HostAgent.Linux.Docker.Implementation;
using XE_Local_AI_Engine.HostAgent.Linux.Hosting;
using XE_Local_AI_Engine.HostAgent.Linux.Lifecycle;
using XE_Local_AI_Engine.HostAgent.Linux.Logs;
using XE_Local_AI_Engine.HostAgent.Linux.Models;
using XE_Local_AI_Engine.HostAgent.Linux.Reconciliation;
using XE_Local_AI_Engine.HostAgent.Linux.Security;
using XE_Local_AI_Engine.HostAgent.Linux.Services;

public static class Program
{
    public static void Main(string[] args)
    {
        if (IsRootRuntime())
        {
            Console.Error.WriteLine("LINUX_REFUSES_ROOT_RUNTIME");
            Environment.ExitCode = 78;
            return;
        }

        var builder = WebApplication.CreateBuilder(args);
        var socketOptions = HostAgentSocketOptions.FromConfiguration(builder.Configuration);
        var tcpOptions = HostAgentTcpOptions.FromConfiguration(builder.Configuration);
        var adminOptions = HostAgentAdminOptions.FromConfiguration(builder.Configuration);

        HostAgentHmacSecretBootstrap.EnsureNativeSecret(builder.Configuration);
        HostAgentSocketPaths.PrepareSocketDirectory(socketOptions.SocketPath);

        builder.AddServiceDefaults();
        builder.Services.AddSingleton(socketOptions);
        builder.Services.AddSingleton(tcpOptions);
        builder.Services.AddSingleton(adminOptions);
        builder.Services.AddHostedService<UnixSocketModeHostedService>();
        builder.Services.AddHostedService<HostAgentRuntimeMetadataHostedService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ReplayWindowCache>();
        builder.Services.AddSingleton<HmacRequestValidator>();
        builder.Services.Configure<HostAgentDockerOptions>(options =>
            HostAgentDockerOptions.Bind(options, builder.Configuration));
        builder.Services.Configure<HostAgentRuntimeOptions>(builder.Configuration.GetSection(HostAgentRuntimeOptions.SectionName));
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<HostAgentRuntimeOptions>>().Value);
        builder.Services.AddSingleton<IDockerRuntimeClient>(sp =>
            sp.GetRequiredService<IOptions<HostAgentDockerOptions>>().Value.UseFakeDriver
                ? ActivatorUtilities.CreateInstance<FakeDockerRuntimeClient>(sp)
                : ActivatorUtilities.CreateInstance<DockerRuntimeClient>(sp));
        builder.Services.AddHostedService<RootlessDockerBootstrapHostedService>();
        builder.Services.AddSingleton<ManifestReconciler>();
        builder.Services.AddSingleton<ContainerLifecycleService>();
        builder.Services.AddSingleton<ContainerLogService>();
        builder.Services.AddSingleton<HostAgentAdminTokenStore>();
        builder.Services.AddSingleton<HostAgentLinuxAdminService>();
        builder.Services.AddSingleton<IOllamaApiClient>(_ => new OllamaApiClient(ResolveOllamaEndpoint(builder.Configuration)));
        builder.Services.AddSingleton<BootstrapModelReadinessService>();
        builder.Services.AddHostedService<BootstrapModelReadinessHostedService>();
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.Configure<HostAgentCapabilityOptions>(options =>
            HostAgentCapabilityOptions.Bind(options, builder.Configuration));
        builder.Services.AddSingleton<CapabilityDetector>();
        builder.Services.Configure<HostAgentHmacOptions>(options =>
            HostAgentHmacOptions.Bind(options, builder.Configuration));
        builder.Services.AddGrpc(options => options.Interceptors.Add<HmacAuthenticationInterceptor>());

        builder.WebHost.ConfigureKestrel(serverOptions =>
        {
            serverOptions.ListenUnixSocket(socketOptions.SocketPath, listenOptions =>
                listenOptions.Protocols = HttpProtocols.Http2);

            if (tcpOptions.Enabled)
            {
                serverOptions.Listen(IPAddress.Loopback, tcpOptions.Port, listenOptions =>
                    listenOptions.Protocols = HttpProtocols.Http2);
            }

            serverOptions.Listen(IPAddress.Loopback, adminOptions.Port, listenOptions =>
                listenOptions.Protocols = HttpProtocols.Http1);
        });

        var app = builder.Build();

        ValidateHmacSecret(app.Services.GetRequiredService<IOptions<HostAgentHmacOptions>>().Value);

        app.MapGrpcService<HostAgentControlService>();
        app.UseLocalAdminRequestGuards();
        app.MapLocalAdminEndpoints();
        app.Run();
    }

    private static bool IsRootRuntime()
    {
        return OperatingSystem.IsLinux() && GetEffectiveUserId() == 0;
    }

    [DllImport("libc", EntryPoint = "geteuid", SetLastError = false)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetEffectiveUserId();

    private static void ValidateHmacSecret(HostAgentHmacOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Secret))
        {
            throw new InvalidOperationException("HostAgent.Linux requires an HMAC secret from configuration or XE_HOST_AGENT_HMAC_SECRET_FILE.");
        }
    }

    private static Uri ResolveOllamaEndpoint(IConfiguration configuration)
    {
        var endpoint = configuration["HostAgent:Ollama:Endpoint"]
                       ?? TryReadEndpointFromConnectionString(configuration.GetConnectionString("chat"))
                       ?? TryReadEndpointFromConnectionString(configuration.GetConnectionString("ollama"))
                       ?? Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
                       ?? "http://127.0.0.1:11434";

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException("HostAgent:Ollama:Endpoint must be an absolute URI.");
    }

    private static string? TryReadEndpointFromConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };
        return builder.TryGetValue("Endpoint", out var endpoint) && endpoint is string value
            ? value
            : null;
    }
}
