import { useMutation } from "@tanstack/react-query";

import { applyAgentHomePatchMutation, previewAgentHomePatchMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Both routes are POSTs — the preview runs `git apply --check` against the host folders, which is a command rather
// than a read — so both arrive from the generated client as mutations rather than query options. The dialog fires the
// preview once on open and again whenever the operator asks for a re-read, which is the behaviour a query-on-open
// would have given and the only part of it that matters here.

/** The non-mutating dry run: the per-file plan, the rejections, and the hash an apply has to echo back. */
export function usePreviewAgentHomePatch() {
	return useMutation({ ...withResponseValidation(previewAgentHomePatchMutation()) });
}

/**
 * The apply. Sends the preview's `patchSha256`, so the node refuses with a 409 if the bytes on disk are no longer the
 * ones the operator read — the approval is bound to the diff, not to the run id.
 */
export function useApplyAgentHomePatch() {
	return useMutation({ ...withResponseValidation(applyAgentHomePatchMutation()) });
}
