namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     External OpenAI-compatible provider routes: the operator's named connections (an Unsloth-served
    ///     llama-server, vLLM, LM Studio, a hosted OpenAI-compatible API) and their manually registered models.
    /// </summary>
    /// <remarks>
    ///     Every route is Operator-gated; the read path reports only whether an API key is stored, never the key.
    /// </remarks>
    public static class ExternalProviders
    {
        /// <summary>The whole connection list (GET). Carries the store revision every write compares against.</summary>
        public const string Connections = "external-providers/connections";

        /// <summary>One connection resource: GET, PUT (insert-or-replace), DELETE. <c>{connectionId}</c> is the slug.</summary>
        public const string ConnectionById = "external-providers/connections/{connectionId}";

        // Connect-time reachability check against GET {base}/models, run SERVER-side because the browser cannot reach an arbitrary operator endpoint through CORS. A literal segment beside
        // the collection, so it never parses as a {connectionId}. POST because the body carries a stored connection id or an unsaved draft (base URL + optional key): a pre-save "Test connection".
        public const string Probe = "external-providers/probe";
    }
}
