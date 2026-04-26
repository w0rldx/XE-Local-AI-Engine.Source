namespace XE_Local_AI_Engine.Client.Testing;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XE_Local_AI_Engine.Client.Services.Connection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddHubMessageRecording(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.GetValue<bool>("Pipeline:CaptureEvents"))
        {
            services.AddSingleton<IOutboundEventRecorder, NoOpOutboundEventRecorder>();
            return services;
        }

        services.AddSingleton<IOutboundEventRecorder, HttpForwardingOutboundEventRecorder>();
        services.Decorate<IHubMessageSender>((inner, sp) =>
            new RecordingHubMessageSender(inner, sp.GetRequiredService<IOutboundEventRecorder>()));

        return services;
    }
}
