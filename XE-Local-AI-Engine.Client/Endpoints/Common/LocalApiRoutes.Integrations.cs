namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Operator-gated management of the external integration surface: the named triggers an integrator invokes, the
    ///     <c>xeint_</c> credentials that authenticate it, the sessions those invocations own, and the executions they
    ///     produce.
    /// </summary>
    /// <remarks>
    ///     Ordinary FastEndpoints routes behind <c>NodeAuthorizationPolicies.Operator</c> — the surface an integrator
    ///     actually calls is <see cref="IntegrationApi" />, and the two never overlap.
    /// </remarks>
    public static class Integrations
    {
        public const string Triggers = "integrations/triggers";
        public const string TriggerById = "integrations/triggers/{triggerId}";
        public const string Keys = "integrations/keys";
        public const string KeyById = "integrations/keys/{keyId}";
        public const string Sessions = "integrations/sessions";
        public const string SessionById = "integrations/sessions/{sessionId}";
        public const string Executions = "integrations/executions";
        public const string ExecutionById = "integrations/executions/{executionId}";
        public const string ExecutionEvents = "integrations/executions/{executionId}/events";
        public const string ExecutionCancel = "integrations/executions/{executionId}/cancel";
    }
}
