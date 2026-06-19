import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { CreateSkillResponse, DeleteSkillResponse, UpdateSkillResponse } from "@/core/api/generated";
import {
	createSkillMutation,
	deleteSkillMutation,
	getSkillOptions,
	listSkillsOptions,
	listSkillsQueryKey,
	updateSkillMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toSkill, toSkillSummary } from "@/features/skills/models/SkillMappers";

// Server state for the node skill library. Reads use the generated hey-api `*Options()` (which wire the shared axios
// instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the optional-field
// generated response into the stricter domain view-model. Every generated options/mutation object is wrapped in
// withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError). Mutations
// invalidate the skill list; an edit also invalidates the single-skill cache for that id so a re-open shows fresh body.

// Lists skill summaries (no body — the list endpoint omits it for payload economy).
export function useSkills() {
	return useQuery({
		...withResponseValidation(listSkillsOptions()),
		select: (data) => (data.items ?? []).map(toSkillSummary),
	});
}

// Full single skill (body included). Disabled until an id is supplied so the editor only fetches the body when a
// skill is actually being edited.
export function useSkill(id: string | null) {
	return useQuery({
		...withResponseValidation(getSkillOptions({ path: { skillId: id ?? "" } })),
		enabled: id !== null,
		select: toSkill,
	});
}

function invalidateSkillsList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: listSkillsQueryKey() });
}

// Invalidates every single-skill (getSkill) query regardless of its id/baseURL. The generated query key is a
// single-element array whose first element carries an `_id: "getSkill"` discriminator; matching only that field
// (TanStack does a partial deep match on the key element) refreshes all open single-skill caches so a re-opened
// editor shows the freshly edited body.
function invalidateAllSingleSkills(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		queryKey: [{ _id: "getSkill" }],
	});
}

export function useCreateSkill() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(createSkillMutation()),
		onSuccess: (_data: CreateSkillResponse) => invalidateSkillsList(queryClient),
	});
}

export function useUpdateSkill() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(updateSkillMutation()),
		// Refresh both the list (name/description/enabled/version may have changed) and any open single-skill query
		// so a re-open shows the freshly edited body.
		onSuccess: (_data: UpdateSkillResponse) =>
			Promise.all([invalidateSkillsList(queryClient), invalidateAllSingleSkills(queryClient)]).then(() => undefined),
	});
}

export function useDeleteSkill() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(deleteSkillMutation()),
		onSuccess: (_data: DeleteSkillResponse) => invalidateSkillsList(queryClient),
	});
}
