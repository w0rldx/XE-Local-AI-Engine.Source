namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Inbound OpenAI-compatible model proxy: the surface an EXTERNAL tool points at to use this node's local models
    ///     as a plain OpenAI provider, with none of the node's agent scaffolding (no persona/tools/memory/RAG).
    /// </summary>
    public static class Proxy
    {
        /// <summary>
        ///     Operator-gated management of the single inbound model-proxy bearer credential (GET status / POST generate /
        ///     DELETE revoke). A FastEndpoints route; the <c>v1/*</c> passthrough routes below are hand-mapped instead.
        /// </summary>
        public const string ApiKey = "proxy/key";

        /// <summary>
        ///     The OpenAI-compatible base an external tool configures (its <c>base_url</c>); the <c>v1/*</c> routes
        ///     below hang off it.
        /// </summary>
        /// <remarks>
        ///     Mapped OUTSIDE FastEndpoints (like <c>MapMcp</c>) but INSIDE the <c>/api/local/v1</c> prefix, so
        ///     <c>LocalApiSecurityMiddleware</c>'s loopback peer + Host + Origin gate still covers them. The full base
        ///     an operator hands to a client is <c>{scheme}://{host}/api/local/v1/proxy/v1</c>.
        /// </remarks>
        public const string OpenAiBase = "proxy/v1";

        /// <summary>OpenAI chat-completions passthrough. Forwarded verbatim to the resolved llama-server child's own <c>/v1/chat/completions</c>.</summary>
        public const string ChatCompletions = "proxy/v1/chat/completions";

        /// <summary>OpenAI embeddings passthrough. Forwarded verbatim to the resolved llama-server child's own <c>/v1/embeddings</c>.</summary>
        public const string Embeddings = "proxy/v1/embeddings";

        /// <summary>OpenAI model list. SYNTHESIZED from the local GGUF catalog (a child only knows the one model it loaded), not a passthrough.</summary>
        public const string Models = "proxy/v1/models";
    }
}
