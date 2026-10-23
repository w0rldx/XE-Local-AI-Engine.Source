namespace XE_Local_AI_Engine.Client.Endpoints.Common;

public static partial class LocalApiRoutes
{
    /// <summary>
    ///     Operator review-and-land over an AgentHome changes.patch. Both POST: the preview runs git apply --check,
    ///     a command, not a read. Off every model-facing surface, so only an authenticated operator on loopback
    ///     reaches INodePatchApplyService.
    /// </summary>
    public static class AgentHomePatch
    {
        /// <summary>Non-mutating dry run: the per-file plan, the rejections, and the hash an apply must echo back.</summary>
        public const string Preview = "agent-home/runs/{runId}/patch/preview";

        /// <summary>
        ///     Lands the previewed patch. Requires the preview's <c>patchSha256</c>, so an approval is bound to the
        ///     diff the operator actually read.
        /// </summary>
        public const string Apply = "agent-home/runs/{runId}/patch/apply";
    }
}
