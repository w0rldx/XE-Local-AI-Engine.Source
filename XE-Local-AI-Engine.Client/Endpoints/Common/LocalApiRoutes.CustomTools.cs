namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>Node-wide user-defined custom tool library routes.</summary>
    /// <remarks>
    ///     Custom tools are operator-authored HttpFetch/Command tools that agent definitions enable per-agent (off by
    ///     default) and run under the existing human-in-the-loop approval. Every route is Operator-gated; the read path
    ///     masks secret header/env values.
    /// </remarks>
    public static class CustomTools
    {
        // Collection (GET list, POST create) and the individual tool resource (GET, PUT, DELETE).
        public const string Definitions = "custom-tools";
        public const string DefinitionById = "custom-tools/{customToolId}";

        // Authoring-time executable validation for the ProgramLaunch selector, desktop-only: a headless host has no operator picking a local binary. The literal segment sits under
        // the collection, so it never parses as a {customToolId}. POST carries the path in the body (no 415 trap); ok/reason comes from the same O_NOFOLLOW host-executable guard the executor runs.
        public const string ExecutableProbe = "custom-tools/executable-probe";
    }
}
