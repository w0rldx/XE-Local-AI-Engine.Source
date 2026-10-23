namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     The EXTERNAL integration API: what an automation, a sensor or a webhook receiver calls with an
    ///     <c>xeint_</c> bearer key.
    /// </summary>
    /// <remarks>
    ///     Mapped OUTSIDE FastEndpoints (like <c>MapMcp</c> and the model proxy) but deliberately INSIDE the
    ///     <c>/api/local/v1</c> prefix, so <c>LocalApiSecurityMiddleware</c>'s loopback peer + Host + Origin gate still
    ///     covers it; moving it outside the prefix would silently drop that layer and leave the bearer key as the only
    ///     control. Off the OpenAPI document for the same reason the proxy is: the bodies are a caller contract rather
    ///     than node DTOs.
    /// </remarks>
    public static class IntegrationApi
    {
        public const string Invoke = "integration-api/triggers/{triggerName}/invoke";
        public const string ExecutionById = "integration-api/executions/{executionId}";
        public const string ExecutionEvents = "integration-api/executions/{executionId}/events";
        public const string ExecutionCancel = "integration-api/executions/{executionId}/cancel";
        public const string SessionById = "integration-api/sessions/{sessionId}";
    }
}
