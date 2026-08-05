import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	commitSkillImportMutation,
	getSkillResourceOptions,
	listSkillResourcesOptions,
	listSkillsQueryKey,
	previewSkillImportMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";

// Server state for skill import + bundled resources. Import is deliberately TWO calls: `preview` writes nothing and
// returns a report plus a token; `commit` persists exactly the materialised payload behind that token, so the report
// the operator approved is the payload that lands (no preview→persist re-fetch, no TOCTOU).

/** Dry run. Writes nothing on the node; returns the report (candidates, refused scripts, conflicts, problems). */
export function usePreviewSkillImport() {
	return useMutation(withResponseValidation(previewSkillImportMutation()));
}

/** Persists the operator's selection from a preview token. Imported skills land DISABLED (backend invariant). */
export function useCommitSkillImport() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(commitSkillImportMutation()),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: listSkillsQueryKey() }).then(() => undefined),
	});
}

/** Resource summaries (name/media type/size — no content) bundled with a stored skill. */
export function useSkillResources(skillId: string | null) {
	return useQuery({
		...withResponseValidation(listSkillResourcesOptions({ path: { skillId: skillId ?? "" } })),
		enabled: skillId !== null,
		select: (data) => data.items ?? [],
	});
}

/**
 * One resource's decrypted content. Fetched only when the operator opens a resource (`resourceName` non-null), so
 * bundled payloads — up to several MiB each — never load just because the editor was opened.
 */
export function useSkillResourceContent(skillId: string | null, resourceName: string | null) {
	return useQuery({
		...withResponseValidation(
			getSkillResourceOptions({ path: { resourceName: resourceName ?? "", skillId: skillId ?? "" } }),
		),
		enabled: skillId !== null && resourceName !== null,
	});
}
