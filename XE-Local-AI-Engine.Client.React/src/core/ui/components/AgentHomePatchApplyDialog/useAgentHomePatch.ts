import { useMutation } from "@tanstack/react-query";

import { applyAgentHomePatchMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { getAgentHomeRunPatch, previewAgentHomePatch } from "@/core/api/generated/sdk.gen";

// Both routes are POSTs — the preview runs `git apply --check` against the host folders, which is a command rather
// than a read — so both arrive from the generated client as mutations rather than query options. The dialog fires the
// preview once on open and again whenever the operator asks for a re-read, which is the behavior a query-on-open
// would have given and the only part of it that matters here.

/** The non-mutating dry run: the per-file plan, the rejections, and the hash an apply has to echo back. */
export function usePreviewAgentHomePatch() {
	return useMutation({
		mutationFn: async ({ path }: { path: { runId: string } }) => {
			const { data: plan } = await previewAgentHomePatch({ path, throwOnError: true });
			if (!plan.canApply) {
				return { ...plan, reviewText: null };
			}
			const { data: patch } = await getAgentHomeRunPatch({ path, throwOnError: true });
			let reviewText: string | null = null;
			if (!patch.truncated && patch.text) {
				const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(patch.text));
				const hash = Array.from(new Uint8Array(digest), (byte) => byte.toString(16).padStart(2, "0")).join("");
				if (hash === plan.patchSha256) {
					reviewText = patch.text;
				}
			}
			return { ...plan, reviewText };
		},
	});
}

/**
 * The apply. Sends the preview's `patchSha256`, so the node refuses with a 409 if the bytes on disk are no longer the
 * ones the operator read — the approval is bound to the diff, not to the run id.
 */
export function useApplyAgentHomePatch() {
	return useMutation({ ...applyAgentHomePatchMutation() });
}
