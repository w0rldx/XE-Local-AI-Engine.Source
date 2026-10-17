namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

/// <summary>The HSTS and HTTPS-redirect step of the node pipeline, skipped entirely in local (desktop) mode.</summary>
internal static class TransportSecurityPipelineExtensions
{
    internal static WebApplication UseNodeTransportSecurity(this WebApplication app, bool isLocalMode)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Desktop mode serves plain HTTP on loopback only, so the HTTPS-redirect/HSTS pipeline is
        // bypassed entirely. Off-flag both branches are exactly as before. UseAntiforgery is scheme-agnostic and stays.
        if (!isLocalMode)
        {
            if (!app.Environment.IsDevelopment())
            {
                app.UseHsts();
            }

            app.UseHttpsRedirection();
        }

        return app;
    }
}
